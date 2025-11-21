using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JobTrackerApi.Models;

namespace JobTrackerApi.Services
{
    public class ClaudeEmailParserService
    {
        private readonly ILogger<ClaudeEmailParserService> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _model;
        private readonly int _maxTokens;
        private readonly string _apiKey;

        public ClaudeEmailParserService(ILogger<ClaudeEmailParserService> logger, IConfiguration configuration)
        {
            _logger = logger;

            _apiKey = Environment.GetEnvironmentVariable("CLAUDE_API_KEY")
                ?? throw new Exception("CLAUDE_API_KEY not found");

            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("x-api-key", _apiKey);
            _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

            _model = Environment.GetEnvironmentVariable("CLAUDE_MODEL") 
                ?? "claude-3-5-haiku-20241022";

            _maxTokens = int.Parse(Environment.GetEnvironmentVariable("CLAUDE_MAX_TOKENS") ?? "256");
        }

        public async Task<EmailExtractedData> ParseEmailAsync(ProcessedEmail email)
        {
            var prompt = BuildPrompt(email);

            try
            {
                _logger.LogInformation($"Parsing email {email.GmailMessageId} with Claude");

                var requestBody = new
                {
                    model = _model,
                    max_tokens = _maxTokens,
                    messages = new[]
                    {
                        new
                        {
                            role = "user",
                            content = prompt
                        }
                    }
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("https://api.anthropic.com/v1/messages", content);
                response.EnsureSuccessStatusCode();

                var responseContent = await response.Content.ReadAsStringAsync();
                var claudeResponse = JsonSerializer.Deserialize<ClaudeApiResponse>(responseContent);

                var text = ExtractText(claudeResponse);
                var jsonText = ExtractJsonFromText(text);

                var extracted = JsonSerializer.Deserialize<EmailExtractedData>(
                    jsonText,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );

                NormalizeConfidence(extracted);

                return extracted ?? CreateDefault(email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Claude parsing failed for message {email.GmailMessageId}");
                return CreateDefault(email);
            }
        }

        // ------------ Helpers ------------ //

        private string BuildPrompt(ProcessedEmail email)
        {
            var body = email.BodyPlainText ?? email.BodyHtml ?? email.Snippet ?? "";

            if (string.IsNullOrWhiteSpace(email.BodyPlainText) && !string.IsNullOrWhiteSpace(email.BodyHtml))
            {
                body = StripHtml(email.BodyHtml);
            }

            if (body.Length > 3000)
                body = body[..3000] + "...";

            return $@"
Extract job application information from this email. 
Return **ONLY a JSON object** with this exact structure:

{{
  ""companyName"": ""string"",
  ""position"": ""string"",
  ""applicationStatus"": ""Applied | Interview Scheduled | Rejected | Offer | In Progress"",
  ""interviewDate"": ""ISO8601 string or null"",
  ""recruiterName"": ""string or null"",
  ""recruiterEmail"": ""string or null"",
  ""jobUrl"": ""string or null"",
  ""salaryRange"": ""string or null"",
  ""interviewType"": ""string or null"",
  ""confidence"": 0-100
}}

Email:
Subject: {email.Subject}
From: {email.From}
Date: {email.Date:yyyy-MM-dd HH:mm}
Body:
{body}
";
        }

        private string ExtractText(ClaudeApiResponse response)
        {
            if (response?.Content == null)
                return "{}";

            foreach (var block in response.Content)
            {
                if (block.Type == "text")
                    return block.Text ?? "{}";
            }

            return "{}";
        }

        private string ExtractJsonFromText(string text)
        {
            text = Regex.Replace(text, @"```json|```", "", RegexOptions.IgnoreCase);

            var match = Regex.Match(text, @"\{.*\}", RegexOptions.Singleline);
            return match.Success ? match.Value : "{}";
        }

        private void NormalizeConfidence(EmailExtractedData? data)
        {
            if (data == null) return;

            if (data.Confidence <= 1)
                data.Confidence *= 100;

            data.Confidence = Math.Clamp(data.Confidence, 0, 100);
        }

        private EmailExtractedData CreateDefault(ProcessedEmail email)
        {
            return new EmailExtractedData
            {
                CompanyName = ExtractCompany(email.FromEmail),
                Position = "Unknown Position",
                ApplicationStatus = "Applied",
                Confidence = 20
            };
        }

        private string ExtractCompany(string email)
        {
            try
            {
                var domain = email.Split('@').Last();
                domain = domain.Replace(".com", "").Replace(".co", "").Replace(".org", "");
                return char.ToUpper(domain[0]) + domain[1..];
            }
            catch
            {
                return "Unknown";
            }
        }

        private string StripHtml(string html)
        {
            html = Regex.Replace(html, "<[^>]+>", " ");
            return Regex.Replace(html, @"\s+", " ").Trim();
        }

        // Public helper for your EmailSync workflow
        public bool ShouldAutoProcess(double confidence)
        {
            var threshold = double.Parse(Environment.GetEnvironmentVariable("AI_CONFIDENCE_THRESHOLD_AUTO") ?? "80");
            return confidence >= threshold;
        }

        public bool RequiresReview(double confidence)
        {
            var auto = double.Parse(Environment.GetEnvironmentVariable("AI_CONFIDENCE_THRESHOLD_AUTO") ?? "80");
            var review = double.Parse(Environment.GetEnvironmentVariable("AI_CONFIDENCE_THRESHOLD_REVIEW") ?? "50");

            return confidence >= review && confidence < auto;
        }

        // Dispose pattern for HttpClient
        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    // Response classes for Claude API
    public class ClaudeApiResponse
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public List<ContentBlock> Content { get; set; } = new();
        public string Model { get; set; } = string.Empty;
        public string StopReason { get; set; } = string.Empty;
        public string StopSequence { get; set; } = string.Empty;
        public Usage Usage { get; set; } = new();
    }

    public class ContentBlock
    {
        public string Type { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
    }

    public class Usage
    {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
    }
}