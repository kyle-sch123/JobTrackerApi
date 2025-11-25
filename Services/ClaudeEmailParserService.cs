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
                ?? "claude-3-5-sonnet-20241022"; // Using Sonnet for better accuracy

            _maxTokens = int.Parse(Environment.GetEnvironmentVariable("CLAUDE_MAX_TOKENS") ?? "1024");
        }

        public async Task<EmailExtractedData> ParseEmailAsync(ProcessedEmail email)
        {
            var prompt = BuildPrompt(email);

            try
            {
                _logger.LogInformation($"📧 Parsing email {email.GmailMessageId} - Subject: {email.Subject}");

                var requestBody = new
                {
                    model = _model,
                    max_tokens = _maxTokens,
                    temperature = 0.2,
                    system = @"You are an expert at extracting job application data from emails.
        CRITICAL: Always extract the position/role name from phrases like 'application for [Position]' or 'applied to [Position]'.
        Look for company names in signatures, footers, or email addresses.
        Return valid JSON with ALL fields present (use null for missing data).",
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

                // Log the full response for debugging
                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogInformation($"📥 Claude API Status: {response.StatusCode}");
                _logger.LogInformation($"📥 Claude API Full Response: {responseContent.Substring(0, Math.Min(500, responseContent.Length))}");

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"❌ Claude API error: {response.StatusCode} - {responseContent}");
                    return CreateFallbackExtraction(email);
                }
                var claudeResponse = JsonSerializer.Deserialize<ClaudeApiResponse>(responseContent);

                var text = ExtractText(claudeResponse);
                var jsonText = ExtractJsonFromText(text);

                _logger.LogInformation($"🤖 Claude raw response: {text.Substring(0, Math.Min(200, text.Length))}...");
                _logger.LogInformation($"📝 Extracted JSON: {jsonText}");

                var extracted = JsonSerializer.Deserialize<EmailExtractedData>(
                    jsonText,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );

                if (extracted == null)
                {
                    _logger.LogWarning("⚠️ Failed to deserialize, using fallback extraction");
                    return CreateFallbackExtraction(email);
                }

                // Post-process and validate
                PostProcessExtractedData(extracted, email);

                _logger.LogInformation(
                    $"✅ Successfully parsed: Company='{extracted.CompanyName}', " +
                    $"Position='{extracted.Position}', Status={extracted.ApplicationStatus}, " +
                    $"Confidence={extracted.Confidence:F1}%"
                );

                return extracted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"💥 Claude parsing failed for message {email.GmailMessageId}");
                return CreateFallbackExtraction(email);
            }
        }


        // Add this method to ClaudeEmailParserService.cs
        public async Task<EmailExtractedData> ParseEmailWithPromptAsync(ProcessedEmail email, string customPrompt)
        {
            try
            {
                _logger.LogInformation($"📧 Parsing with custom prompt: {email.GmailMessageId}");

                var requestBody = new
                {
                    model = _model,
                    max_tokens = _maxTokens,
                    temperature = 0.2,
                    messages = new[]
                    {
                new
                {
                    role = "user",
                    content = customPrompt
                }
            }
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("https://api.anthropic.com/v1/messages", content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"❌ Claude API error: {response.StatusCode}");
                    return CreateFallbackExtraction(email);
                }

                var claudeResponse = JsonSerializer.Deserialize<ClaudeApiResponse>(responseContent);
                var text = ExtractText(claudeResponse);
                var jsonText = ExtractJsonFromText(text);

                var extracted = JsonSerializer.Deserialize<EmailExtractedData>(
                    jsonText,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );

                if (extracted != null)
                {
                    PostProcessExtractedData(extracted, email);
                    return extracted;
                }

                return CreateFallbackExtraction(email);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Custom prompt parsing failed");
                return CreateFallbackExtraction(email);
            }
        }

        private void PostProcessExtractedData(EmailExtractedData data, ProcessedEmail email)
        {
            // 1. Normalize confidence
            if (data.Confidence <= 1.0)
            {
                data.Confidence *= 100;
            }
            data.Confidence = Math.Clamp(data.Confidence, 0, 100);

            // 2. Fix company name if missing or generic
            if (string.IsNullOrWhiteSpace(data.CompanyName) ||
                data.CompanyName.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                data.CompanyName.Equals("Unknown Company", StringComparison.OrdinalIgnoreCase))
            {
                data.CompanyName = ExtractCompanyFromEmail(email);
                data.Confidence = Math.Max(0, data.Confidence - 15);
                _logger.LogInformation($"🔍 Fallback company extraction: '{data.CompanyName}'");
            }

            // 3. Fix position if missing - CRITICAL FOR YOUR CASE
            if (string.IsNullOrWhiteSpace(data.Position))
            {
                data.Position = ExtractPositionFromEmail(email);
                data.Confidence = Math.Max(0, data.Confidence - 10);
                _logger.LogInformation($"🔍 Fallback position extraction: '{data.Position}'");
            }

            // 4. Validate status
            var validStatuses = new[] { "Applied", "Interview Scheduled", "Rejected", "Offer", "In Progress" };
            if (string.IsNullOrWhiteSpace(data.ApplicationStatus) ||
                !validStatuses.Contains(data.ApplicationStatus, StringComparer.OrdinalIgnoreCase))
            {
                data.ApplicationStatus = DetectStatusFromEmail(email);
                _logger.LogInformation($"🔍 Fallback status detection: '{data.ApplicationStatus}'");
            }

            // 5. Clean up company name
            data.CompanyName = CleanCompanyName(data.CompanyName);
        }

        private string ExtractPositionFromEmail(ProcessedEmail email)
        {
            var content = $"{email.Subject} {email.BodyPlainText ?? email.BodyHtml ?? email.Snippet}";

            // Pattern 1: Extract from subject line first (most reliable)
            // Format: "Thanks for applying | Frontend Developer | JO-123"
            var subjectPipeMatch = Regex.Match(email.Subject, @"\|\s*([^|]+?)\s*\|", RegexOptions.IgnoreCase);
            if (subjectPipeMatch.Success && subjectPipeMatch.Groups[1].Success)
            {
                var position = subjectPipeMatch.Groups[1].Value.Trim();
                if (position.Length > 3 && position.Length < 100 && !position.StartsWith("JO-"))
                {
                    _logger.LogInformation($"🎯 Extracted position from subject pipes: '{position}'");
                    return position;
                }
            }

            // Pattern 2: "application for [Position]"
            var patterns = new[]
            {
                @"application for\s+(?:the\s+)?([^.!,\n]+?)(?:\s+has\s+been|\s+to|\.|!|,)",
                @"applied (?:to|for)\s+(?:the\s+)?([^.!,\n]+?)(?:\s+position|\s+role|\.|!|,)",
                @"your\s+(?:application|submission)\s+for\s+(?:the\s+)?([^.!,\n]+)",
                @"position[:\s]+([A-Z][^.!,\n]+?)(?:\s+at|\.|!|,)",
                @"role[:\s]+([A-Z][^.!,\n]+?)(?:\s+at|\.|!|,)",
                @"as\s+(?:a|an)\s+([A-Z][^.!,\n]+?)(?:\s+at|\s+with|\.|!|,)",
                // Common job title patterns
                @"((?:Senior|Junior|Lead|Principal|Staff)?\s*(?:Software|Front\s*End|Back\s*End|Full\s*Stack|Web|Mobile|Data|DevOps|Cloud)\s+(?:Engineer|Developer|Architect|Designer|Analyst|Manager))",
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(content, pattern, RegexOptions.IgnoreCase);
                if (match.Success && match.Groups[1].Success)
                {
                    var position = match.Groups[1].Value.Trim();
                    // Clean up the position
                    position = position.Replace("  ", " ").Trim();

                    if (position.Length > 3 && position.Length < 100)
                    {
                        _logger.LogInformation($"🎯 Extracted position from pattern '{pattern}': '{position}'");
                        return position;
                    }
                }
            }

            // If still nothing, try simple subject extraction
            var simpleSubjectMatch = Regex.Match(email.Subject, @"(?:for|regarding|re:)\s+([^-|]+)", RegexOptions.IgnoreCase);
            if (simpleSubjectMatch.Success)
            {
                var position = simpleSubjectMatch.Groups[1].Value.Trim();
                if (position.Length > 3 && position.Length < 100)
                {
                    return position;
                }
            }

            return "Position Not Specified";
        }

        private string ExtractCompanyFromEmail(ProcessedEmail email)
        {
            // Strategy 1: From email body
            var bodyCompany = ExtractCompanyFromBody(email.BodyPlainText ?? email.BodyHtml ?? "");
            if (!string.IsNullOrEmpty(bodyCompany) && bodyCompany != "Unknown Company")
            {
                return bodyCompany;
            }

            // Strategy 2: From "From" name
            var fromName = email.From.Split('<')[0].Trim();
            if (!string.IsNullOrEmpty(fromName) && !fromName.Contains("@") && fromName.Length > 2)
            {
                fromName = Regex.Replace(fromName, @"\s+(Team|Careers|Recruiting|HR|Jobs|Talent)$", "", RegexOptions.IgnoreCase);
                if (fromName.Length > 2 && !fromName.ToLower().Contains("noreply"))
                {
                    return fromName;
                }
            }

            // Strategy 3: From email domain
            var domainCompany = ExtractCompanyFromDomain(email.FromEmail);
            if (!string.IsNullOrEmpty(domainCompany) && domainCompany != "Unknown Company")
            {
                return domainCompany;
            }

            return "Unknown Company";
        }

        private string ExtractCompanyFromBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "Unknown Company";

            var patterns = new[]
            {
                @"on behalf of\s+([A-Z][A-Za-z\s&]+)",
                @"at\s+([A-Z][A-Za-z\s&]+?)(?:\.|!|\s+team)",
                @"([A-Z][A-Za-z\s&]+?)\s+(?:team|careers|recruiting|talent)",
                @"from\s+([A-Z][A-Za-z\s&]+)",
                @"opportunities? at\s+([A-Z][A-Za-z\s&]+)",
                @"([A-Z][A-Za-z\s&]+?)\s+is\s+(?:hiring|looking|seeking)",
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(body, pattern);
                if (match.Success && match.Groups[1].Value.Length >= 2)
                {
                    var company = match.Groups[1].Value.Trim();
                    if (company.Length <= 50)
                    {
                        return CleanCompanyName(company);
                    }
                }
            }

            return "Unknown Company";
        }

        private string ExtractCompanyFromDomain(string email)
        {
            try
            {
                var domain = email.Split('@').LastOrDefault()?.ToLower() ?? "";

                // Skip generic domains
                var genericDomains = new[]
                {
                    "bamboohr.com", "workday.com", "greenhouse.io", "lever.co",
                    "gmail.com", "yahoo.com", "outlook.com", "pnet.co.za", "indeed.com"
                };

                if (genericDomains.Any(d => domain.Contains(d)))
                {
                    return "Unknown Company";
                }

                // Extract base domain
                domain = domain.Replace("jobs.", "").Replace("careers.", "").Replace("recruiting.", "");
                var parts = domain.Split('.');
                if (parts.Length > 0)
                {
                    var baseName = parts[0];
                    return char.ToUpper(baseName[0]) + baseName.Substring(1);
                }
            }
            catch { }

            return "Unknown Company";
        }

        private string DetectStatusFromEmail(ProcessedEmail email)
        {
            var content = $"{email.Subject} {email.BodyPlainText ?? email.BodyHtml ?? email.Snippet}".ToLower();

            // Rejection (highest priority)
            if (Regex.IsMatch(content, @"unfortunately|not moving forward|other candidates|not selected|not proceeding|regret to inform|will not be|have decided not to|pursue other candidates|not the right fit"))
                return "Rejected";

            // Interview
            if (Regex.IsMatch(content, @"interview|schedule|meeting|speak with|discussion|next step|phone screen|video call"))
                return "Interview Scheduled";

            // Offer
            if (Regex.IsMatch(content, @"offer letter|pleased to offer|congratulations|compensation|we'?d like to offer|accept this offer"))
                return "Offer";

            // Applied (confirmation)
            if (Regex.IsMatch(content, @"application (?:has been |successfully )?(?:sent|submitted|received)|thank you for applying|confirmation|we have received|successfully sent"))
                return "Applied";

            // In Progress
            if (Regex.IsMatch(content, @"reviewing|under review|next steps|considering|evaluating|in process"))
                return "In Progress";

            return "Applied"; // Default
        }

        private string CleanCompanyName(string companyName)
        {
            if (string.IsNullOrWhiteSpace(companyName)) return "Unknown Company";

            // Remove common suffixes
            companyName = Regex.Replace(companyName, @"\s+(Inc\.?|LLC|Ltd\.?|Corp\.?|Corporation|Limited)$", "", RegexOptions.IgnoreCase);

            return companyName.Trim();
        }

        private EmailExtractedData CreateFallbackExtraction(ProcessedEmail email)
        {
            var companyName = ExtractCompanyFromEmail(email);
            var position = ExtractPositionFromEmail(email);
            var status = DetectStatusFromEmail(email);

            _logger.LogWarning(
                $"⚠️ Using FALLBACK extraction for {email.GmailMessageId}: " +
                $"Company='{companyName}', Position='{position}', Status='{status}'"
            );

            return new EmailExtractedData
            {
                CompanyName = companyName,
                Position = position,
                ApplicationStatus = status,
                Confidence = 30, // Low confidence for complete fallback
                RecruiterEmail = email.FromEmail
            };
        }

        private string BuildPrompt(ProcessedEmail email)
        {
            var body = email.BodyPlainText ?? email.BodyHtml ?? email.Snippet ?? "";

            if (string.IsNullOrWhiteSpace(email.BodyPlainText) && !string.IsNullOrWhiteSpace(email.BodyHtml))
            {
                body = StripHtml(email.BodyHtml);
            }

            if (body.Length > 4000)
                body = body[..4000] + "...";

            return $@"Extract job application information from this email. 

CRITICAL INSTRUCTIONS:
1. Look for position/role in phrases like ""application for [POSITION]"" or ""applied to [POSITION]""
2. Extract company name from signature, footer, or email metadata
3. For rejection emails (keywords: unfortunately, not proceeding, other candidates), set status to 'Rejected' with high confidence
4. ALL fields must be present in JSON (use null if truly not found)

Return ONLY valid JSON (no markdown, no explanation):

{{
  ""companyName"": ""string - company name or null"",
  ""position"": ""string - job title/position or null"",
  ""applicationStatus"": ""Applied|Interview Scheduled|Rejected|Offer|In Progress"",
  ""interviewDate"": ""ISO8601 string or null"",
  ""recruiterName"": ""string or null"",
  ""recruiterEmail"": ""string or null"",
  ""jobUrl"": ""string or null"",
  ""salaryRange"": ""string or null"",
  ""interviewType"": ""phone|video|onsite or null"",
  ""confidence"": 0-100
}}

EMAIL DETAILS:
Subject: {email.Subject}
From: {email.From}
From Email: {email.FromEmail}
Date: {email.Date:yyyy-MM-dd HH:mm}

BODY:
{body}

JSON:";
        }

        private string ExtractText(ClaudeApiResponse? response)
        {
            if (response?.Content == null || response.Content.Count == 0)
                return "{}";

            foreach (var block in response.Content)
            {
                if (block.Type == "text" && !string.IsNullOrEmpty(block.Text))
                    return block.Text;
            }

            return "{}";
        }

        private string ExtractJsonFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "{}";

            // Remove markdown
            text = Regex.Replace(text, @"```(?:json)?\s*", "", RegexOptions.IgnoreCase);
            text = text.Trim();

            // Extract JSON
            var match = Regex.Match(text, @"\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}", RegexOptions.Singleline);
            return match.Success ? match.Value : "{}";
        }

        private string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";

            html = Regex.Replace(html, "<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "<[^>]+>", " ");
            html = Regex.Replace(html, @"\s+", " ");
            return html.Trim();
        }

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

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    // Response classes
    public class ClaudeApiResponse
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public List<ContentBlock> Content { get; set; } = new();
        public string Model { get; set; } = string.Empty;
        public string StopReason { get; set; } = string.Empty;
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