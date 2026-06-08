using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using JobTrackerApi.Models;

namespace JobTrackerApi.Services
{
    public class ClaudeEmailParserService
    {
        private const string ClaudeMessagesUrl = "https://api.anthropic.com/v1/messages";

        private readonly ILogger<ClaudeEmailParserService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _model;
        private readonly int _maxTokens;

        public ClaudeEmailParserService(ILogger<ClaudeEmailParserService> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;

            // Fail fast at startup if the key is missing; the actual header is
            // applied to the named "claude" HttpClient in Program.cs.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDE_API_KEY")))
            {
                throw new Exception("CLAUDE_API_KEY not found");
            }

            _model = Environment.GetEnvironmentVariable("CLAUDE_MODEL")
                ?? "claude-sonnet-4-6";

            _maxTokens = int.Parse(Environment.GetEnvironmentVariable("CLAUDE_MAX_TOKENS") ?? "1024");
        }

        /// <summary>
        /// POSTs to the Claude Messages API with exponential backoff. Retries on
        /// 429 (respecting Retry-After) and 5xx; returns the final response either
        /// way so callers can fall back to heuristic extraction.
        /// </summary>
        private async Task<HttpResponseMessage> PostWithRetryAsync(string requestJson, int maxAttempts = 3)
        {
            HttpResponseMessage response = null!;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var httpClient = _httpClientFactory.CreateClient("claude");
                // Content can't be reused across attempts, so build it each time.
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                response = await httpClient.PostAsync(ClaudeMessagesUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                var status = (int)response.StatusCode;
                var retriable = status == 429 || status >= 500;
                if (!retriable || attempt == maxAttempts)
                {
                    return response;
                }

                // Respect Retry-After when present, otherwise exponential backoff (2s, 4s, …).
                TimeSpan delay;
                if (response.Headers.RetryAfter?.Delta is TimeSpan retryAfterDelta)
                {
                    delay = retryAfterDelta;
                }
                else if (response.Headers.RetryAfter?.Date is DateTimeOffset retryAfterDate)
                {
                    delay = retryAfterDate - DateTimeOffset.UtcNow;
                }
                else
                {
                    delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                }

                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.FromSeconds(1);
                }

                _logger.LogWarning(
                    "Claude API returned {Status}; retrying in {Delay}s (attempt {Attempt}/{Max})",
                    status, delay.TotalSeconds, attempt, maxAttempts);

                response.Dispose();
                await Task.Delay(delay);
            }

            return response;
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
                    system = ExtractionSystemPrompt,
                    // Structured outputs: constrain the response to the schema so the
                    // result is always valid JSON in the expected shape.
                    output_config = new
                    {
                        format = new
                        {
                            type = "json_schema",
                            schema = BuildExtractionSchema()
                        }
                    },
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
                var response = await PostWithRetryAsync(json);

                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogInformation($"📥 Claude API Status: {response.StatusCode}");
                _logger.LogInformation($"📥 Claude API Response: {responseContent.Substring(0, Math.Min(1000, responseContent.Length))}");

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"❌ Claude API error: {response.StatusCode} - {responseContent}");
                    return CreateFallbackExtraction(email);
                }

                var claudeResponse = JsonSerializer.Deserialize<ClaudeApiResponse>(responseContent);
                var text = ExtractText(claudeResponse);

                _logger.LogInformation($"🤖 Claude raw response: {text}");

                var jsonText = ExtractJsonFromText(text);
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

                // Only a direct application response is a real "job application";
                // job alerts, newsletters, and promotional mail are not.
                if (!string.IsNullOrEmpty(extracted.EmailType))
                {
                    extracted.IsJobApplication = extracted.EmailType.Equals(
                        "application_response", StringComparison.OrdinalIgnoreCase);
                }

                // Check if this is actually a job application email
                if (!extracted.IsJobApplication)
                {
                    _logger.LogInformation(
                        $"🚫 Email {email.GmailMessageId} classified as '{extracted.EmailType}' (not an application)");
                    extracted.Confidence = 0;
                    return extracted;
                }

                // Post-process and validate
                PostProcessExtractedData(extracted, email);

                // Set description
                extracted.Description = "Automatically created from your email via AI";

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

        private void PostProcessExtractedData(EmailExtractedData data, ProcessedEmail email)
        {
            // 1. Normalize confidence to 0-100
            if (data.Confidence <= 1.0)
            {
                data.Confidence *= 100;
            }
            data.Confidence = Math.Clamp(data.Confidence, 0, 100);

            // 2. Trust the model on the company name. If it returned a sentinel/unknown
            //    value, leave it null rather than synthesizing a "Recruitment Agency
            //    (via X)" or domain-based placeholder — that synthesis was a major
            //    source of wrong company names. Downstream supplies a neutral
            //    placeholder when company is missing.
            if (!string.IsNullOrWhiteSpace(data.CompanyName))
            {
                var cleaned = CleanCompanyName(data.CompanyName);
                data.CompanyName =
                    cleaned.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
                    cleaned.Equals("Unknown Company", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : cleaned;
            }

            // 3. Validate status against the canonical taxonomy; null it out if the
            //    model returned something off-list rather than keyword-guessing.
            if (!string.IsNullOrWhiteSpace(data.ApplicationStatus) &&
                !ApplicationStatuses.All.Contains(data.ApplicationStatus, StringComparer.OrdinalIgnoreCase))
            {
                data.ApplicationStatus = null;
            }

            data.Description = "Automatically generated from your email via AI";
        }

        private bool IsRecruitmentPlatform(string email)
        {
            var recruitmentDomains = new[]
            {
                "pnet.co.za", "indeed.com", "linkedin.com", "glassdoor.com",
                "bamboohr.com", "workday.com", "greenhouse.io", "lever.co",
                "smartrecruiters.com", "jobvite.com", "talentsoft.com",
                "careers.page", "myworkdayjobs.com", "icims.com"
            };

            return recruitmentDomains.Any(domain => email.ToLower().Contains(domain));
        }

        private string DetermineRecruitmentSource(ProcessedEmail email)
        {
            var emailLower = email.FromEmail.ToLower();

            if (emailLower.Contains("pnet.co.za"))
                return "Recruitment Agency (via PNet)";
            if (emailLower.Contains("indeed.com"))
                return "Recruitment Agency (via Indeed)";
            if (emailLower.Contains("linkedin.com"))
                return "Recruitment Agency (via LinkedIn)";
            if (emailLower.Contains("glassdoor"))
                return "Recruitment Agency (via Glassdoor)";
            if (emailLower.Contains("bamboohr.com") || emailLower.Contains("workday") ||
                emailLower.Contains("greenhouse") || emailLower.Contains("lever"))
                return "Recruitment Agency (via ATS Platform)";
            if (emailLower.Contains("smartrecruiters.com"))
                return "Recruitment Agency (via SmartRecruiters)";
            if (emailLower.Contains("jobvite.com"))
                return "Recruitment Agency (via Jobvite)";

            // Try to extract agency/company name from subject or body
            var agencyName = ExtractAgencyFromContent(email);
            if (!string.IsNullOrEmpty(agencyName))
                return $"Recruitment Agency (via {agencyName})";

            // Default fallback - extract domain name as last resort
            var domain = email.FromEmail.Split('@').LastOrDefault()?.Split('.').FirstOrDefault() ?? "Unknown";
            return $"Recruitment Agency (via {char.ToUpper(domain[0]) + domain.Substring(1)})";
        }

        private string ExtractAgencyFromContent(ProcessedEmail email)
        {
            var content = $"{email.Subject} {email.BodyPlainText ?? email.BodyHtml ?? ""}";

            // Look for "on behalf of" patterns
            var behalfMatch = Regex.Match(content, @"on behalf of\s+([A-Z][A-Za-z\s&]+?)(?:\.|,|\s+for)", RegexOptions.IgnoreCase);
            if (behalfMatch.Success && behalfMatch.Groups[1].Value.Length > 2)
            {
                return behalfMatch.Groups[1].Value.Trim();
            }

            return string.Empty;
        }

        private string ExtractPositionFromEmail(ProcessedEmail email)
        {
            var content = $"{email.Subject} {email.BodyPlainText ?? email.BodyHtml ?? email.Snippet}";

            // Pattern 1: Extract from subject line first (most reliable)
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

            // Pattern 2: Common application phrases
            var patterns = new[]
            {
                @"application for\s+(?:the\s+)?([^.!,\n]+?)(?:\s+has\s+been|\s+to|\.|!|,)",
                @"applied (?:to|for)\s+(?:the\s+)?([^.!,\n]+?)(?:\s+position|\s+role|\.|!|,)",
                @"your\s+(?:application|submission)\s+for\s+(?:the\s+)?([^.!,\n]+)",
                @"position[:\s]+([A-Z][^.!,\n]+?)(?:\s+at|\.|!|,)",
                @"role[:\s]+([A-Z][^.!,\n]+?)(?:\s+at|\.|!|,)",
                @"as\s+(?:a|an)\s+([A-Z][^.!,\n]+?)(?:\s+at|\s+with|\.|!|,)",
                @"((?:Senior|Junior|Lead|Principal|Staff)?\s*(?:Software|Front\s*End|Back\s*End|Full\s*Stack|Web|Mobile|Data|DevOps|Cloud)\s+(?:Engineer|Developer|Architect|Designer|Analyst|Manager))",
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(content, pattern, RegexOptions.IgnoreCase);
                if (match.Success && match.Groups[1].Success)
                {
                    var position = match.Groups[1].Value.Trim();
                    position = position.Replace("  ", " ").Trim();

                    if (position.Length > 3 && position.Length < 100)
                    {
                        _logger.LogInformation($"🎯 Extracted position from pattern: '{position}'");
                        return position;
                    }
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

                // Skip generic/recruitment domains
                var genericDomains = new[]
                {
                    "bamboohr.com", "workday.com", "greenhouse.io", "lever.co",
                    "gmail.com", "yahoo.com", "outlook.com", "pnet.co.za", "indeed.com",
                    "linkedin.com", "glassdoor.com", "smartrecruiters.com"
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

            return "Applied";
        }

        private string CleanCompanyName(string companyName)
        {
            if (string.IsNullOrWhiteSpace(companyName)) return "Unknown Company";

            // Only clean up trailing punctuation and whitespace, preserve company suffixes (Corp, Inc, etc.)
            companyName = companyName.Trim().TrimEnd('.', ',');
            return companyName;
        }

        private EmailExtractedData CreateFallbackExtraction(ProcessedEmail email)
        {
            var companyName = "Unknown Company";

            // Check if it's from a recruitment platform FIRST
            if (IsRecruitmentPlatform(email.FromEmail))
            {
                companyName = DetermineRecruitmentSource(email);
                _logger.LogInformation($"🔍 Fallback: Recruitment platform detected - '{companyName}'");
            }
            else
            {
                // Try to extract company
                var extracted = ExtractCompanyFromEmail(email);

                if (string.IsNullOrWhiteSpace(extracted) ||
                    extracted.Equals("Unknown Company", StringComparison.OrdinalIgnoreCase))
                {
                    // Still couldn't find it, use recruitment source as ultimate fallback
                    companyName = DetermineRecruitmentSource(email);
                    _logger.LogInformation($"🔍 Fallback: Using recruitment source - '{companyName}'");
                }
                else
                {
                    companyName = extracted;
                }
            }

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
                Confidence = 30,
                RecruiterEmail = email.FromEmail,
                IsJobApplication = true,
                Description = "Automatically generated from your email via AI"
            };
        }

        // System prompt: the classification taxonomy and extraction rules. The
        // response shape itself is enforced by the JSON schema (structured outputs),
        // so this focuses purely on *what* to extract and *how* to judge it.
        private const string ExtractionSystemPrompt = @"You extract structured data from a SINGLE email for a job-application tracker. Be precise and never invent information.

STEP 1 - Classify the email (emailType):
- 'application_response': a direct response to a job the recipient personally applied to or is being considered for - application received/confirmation, interview invitation or scheduling, assessment/coding-challenge invite, rejection, offer, or a recruiter personally reaching out about a specific role for this person.
- 'job_alert': automated job listings, recommendations, or digests ('jobs matching your search', 'N new jobs', 'jobs you may be interested in'). NOT an application.
- 'newsletter': periodic content or updates not tied to the recipient's own application.
- 'marketing_promotional': product promotions, upsells, webinars, sales, or advertising - including from job boards ('upgrade to Premium', 'boost your profile', 'complete your profile').
- 'other': anything not job-application related.

Only 'application_response' yields application data. For every other type, set all data fields to null and confidence to 0.

STEP 2 - Extract (only for 'application_response'; otherwise null):
- companyName: the EMPLOYER the person applied to. Use the company named in the body, subject, or signature/legal footer. If the email is from a recruiting agency, job board, or ATS (LinkedIn, Indeed, PNet, Greenhouse, Workday, Lever, SmartRecruiters, iCIMS, etc.) acting on behalf of a client, use the CLIENT company only if it is clearly named. NEVER use the job board / ATS / agency name, the email domain, or the sender's display name as the company unless it is unmistakably the actual employer. If you are not sure, return null - do not guess.
- position: the exact job title applied for. If not stated, null.
- applicationStatus: exactly one of 'Applied', 'In Progress', 'Interview Scheduled', 'Offer', 'Rejected', 'Accepted', 'Declined' - whichever the email conveys. If unclear, null.
- recruiterName: the real human contact (an actual person's name) who sent or signed the email. Do NOT use team mailboxes, role labels ('Recruiting Team', 'Talent Acquisition'), automated senders ('no-reply'), ATS/platform names, or the company name. If no real person is identifiable, null.
- recruiterEmail: that person's email address if present, otherwise null.
- interviewDate: ISO-8601 datetime of a scheduled interview, otherwise null.
- interviewType: 'phone', 'video', or 'onsite', otherwise null.
- jobUrl: a direct link to the specific job posting, otherwise null.
- salaryRange: stated compensation, otherwise null.
- confidence: integer 0-100, your certainty in the extracted company + position + status (0 for any non-application type).";

        // JSON schema for structured outputs. Nullable fields use a string|null type
        // union; the model returns null when the value isn't present in the email.
        private static object BuildExtractionSchema() => new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                emailType = new
                {
                    type = "string",
                    @enum = new[] { "application_response", "job_alert", "newsletter", "marketing_promotional", "other" }
                },
                companyName = new { type = new[] { "string", "null" } },
                position = new { type = new[] { "string", "null" } },
                applicationStatus = new { type = new[] { "string", "null" } },
                recruiterName = new { type = new[] { "string", "null" } },
                recruiterEmail = new { type = new[] { "string", "null" } },
                interviewDate = new { type = new[] { "string", "null" } },
                interviewType = new { type = new[] { "string", "null" } },
                jobUrl = new { type = new[] { "string", "null" } },
                salaryRange = new { type = new[] { "string", "null" } },
                confidence = new { type = "integer" }
            },
            required = new[]
            {
                "emailType", "companyName", "position", "applicationStatus",
                "recruiterName", "recruiterEmail", "interviewDate", "interviewType",
                "jobUrl", "salaryRange", "confidence"
            }
        };

        private string BuildPrompt(ProcessedEmail email)
        {
            var body = email.BodyPlainText ?? email.BodyHtml ?? email.Snippet ?? "";

            if (string.IsNullOrWhiteSpace(email.BodyPlainText) && !string.IsNullOrWhiteSpace(email.BodyHtml))
            {
                body = StripHtml(email.BodyHtml);
            }

            if (body.Length > 4000)
                body = body[..4000] + "...";

            return $@"Classify and extract from this email.

Subject: {email.Subject}
From: {email.From}
From email: {email.FromEmail}
Date: {email.Date:yyyy-MM-dd HH:mm}

Body:
{body}";
        }

        private string ExtractText(ClaudeApiResponse? response)
        {
            if (response?.Content == null || response.Content.Count == 0)
            {
                _logger.LogWarning("⚠️ Response or Content is null/empty");
                return "{}";
            }

            _logger.LogInformation($"📊 Content blocks count: {response.Content.Count}");

            foreach (var block in response.Content)
            {
                _logger.LogInformation($"📦 Block type: {block.Type}, Has text: {!string.IsNullOrEmpty(block.Text)}");

                if (block.Type == "text" && !string.IsNullOrEmpty(block.Text))
                {
                    _logger.LogInformation($"✅ Extracted text length: {block.Text.Length}");
                    return block.Text;
                }
            }

            _logger.LogWarning("⚠️ No text content found in response blocks");
            return "{}";
        }

        private string ExtractJsonFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "{}";

            // Remove markdown code blocks
            text = Regex.Replace(text, @"```(?:json)?\s*", "", RegexOptions.IgnoreCase);
            text = text.Trim();

            // Extract JSON object (handles nested objects)
            var match = Regex.Match(text, @"\{(?:[^{}]|(?<open>\{)|(?<-open>\}))+(?(open)(?!))\}", RegexOptions.Singleline);
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
    }

    // Response classes
    public class ClaudeApiResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public List<ContentBlock> Content { get; set; } = new();

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("stop_reason")]
        public string StopReason { get; set; } = string.Empty;

        [JsonPropertyName("usage")]
        public Usage Usage { get; set; } = new();
    }

    public class ContentBlock
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
    }

    public class Usage
    {
        [JsonPropertyName("input_tokens")]
        public int InputTokens { get; set; }

        [JsonPropertyName("output_tokens")]
        public int OutputTokens { get; set; }
    }
}