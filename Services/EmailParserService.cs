using JobTrackerApi.Models;
using System.Text.RegularExpressions;

namespace JobTrackerApi.Services
{
    // Simple rule-based parser with a placeholder for an AI/LLM agent implementation.
    public class EmailParserService
    {
        private readonly ILogger<EmailParserService> _logger;

        public EmailParserService(ILogger<EmailParserService> logger)
        {
            _logger = logger;
        }

        // Parses a ProcessedEmail and returns extracted structured data.
        // This is intentionally conservative: it uses heuristics and returns a confidence score.
        public Task<EmailExtractedData> ParseEmailAsync(ProcessedEmail email)
        {
            var data = new EmailExtractedData();

            try
            {
                var combined = $"{email.Subject}\n{email.BodyPlainText ?? email.BodyHtml ?? email.Snippet}".ToLowerInvariant();

                // Company: look for common patterns like "at <Company>" or from domain
                var company = ExtractCompany(combined) ?? ExtractCompanyFromFromHeader(email.From);
                data.CompanyName = company;

                // Position: look for "position", "role", or "for <Position>"
                data.Position = ExtractPosition(combined);

                // Job url
                data.JobUrl = ExtractFirstUrl(email.BodyHtml ?? email.BodyPlainText ?? "");

                // Application status heuristics
                data.ApplicationStatus = InferStatus(combined);

                // Interview date: look for date-like strings
                data.InterviewDate = ExtractDate(combined);

                // Recruiter name: look for "from <Name>" patterns in From header
                data.RecruiterName = ExtractNameFromFromHeader(email.From);

                // Confidence: crude heuristic based on how many fields we extracted
                var score = 0;
                if (!string.IsNullOrEmpty(data.CompanyName)) score += 2;
                if (!string.IsNullOrEmpty(data.Position)) score += 2;
                if (!string.IsNullOrEmpty(data.ApplicationStatus)) score += 1;
                if (data.InterviewDate.HasValue) score += 1;
                if (!string.IsNullOrEmpty(data.JobUrl)) score += 1;

                data.Confidence = Math.Min(1.0, score / 6.0);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Email parsing failed for message {MessageId}", email.GmailMessageId);
            }

            return Task.FromResult(data);
        }

        private string? ExtractCompany(string text)
        {
            // Look for patterns like "at Acme Corp" or "@acmecorp.com" or line containing "company:"
            var m = Regex.Match(text, @"\bat\s+([A-Z0-9][A-Za-z0-9 &'\-\.]{2,60})", RegexOptions.IgnoreCase);
            if (m.Success) return Capitalize(m.Groups[1].Value.Trim());

            m = Regex.Match(text, @"company[:\-]\s*([A-Za-z0-9 &'\-\.]{2,60})", RegexOptions.IgnoreCase);
            if (m.Success) return Capitalize(m.Groups[1].Value.Trim());

            return null;
        }

        private string? ExtractCompanyFromFromHeader(string from)
        {
            if (string.IsNullOrEmpty(from)) return null;
            // domain-based company
            var match = Regex.Match(from, @"@([A-Za-z0-9\-]+)\.", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return Capitalize(match.Groups[1].Value);
            }
            return null;
        }

        private string? ExtractPosition(string text)
        {
            var m = Regex.Match(text, @"\bposition\s+(?:of|for)?\s*[:\-]?\s*([A-Za-z0-9 &'\-]{2,80})", RegexOptions.IgnoreCase);
            if (m.Success) return Capitalize(m.Groups[1].Value.Trim());

            m = Regex.Match(text, @"\bfor\s+the\s+role\s+of\s+([A-Za-z0-9 &'\-]{2,80})", RegexOptions.IgnoreCase);
            if (m.Success) return Capitalize(m.Groups[1].Value.Trim());

            // fallback: look for "role:" or "job:" lines
            m = Regex.Match(text, @"\b(role|job|position)[:\-]\s*([A-Za-z0-9 &'\-]{2,80})", RegexOptions.IgnoreCase);
            if (m.Success) return Capitalize(m.Groups[2].Value.Trim());

            return null;
        }

        private string? ExtractFirstUrl(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = Regex.Match(text, @"https?://[^\s'\>]+", RegexOptions.IgnoreCase);
            return m.Success ? m.Value : null;
        }

        private string? InferStatus(string text)
        {
            if (text.Contains("interview") && text.Contains("scheduled") || text.Contains("invite")) return "Interview Scheduled";
            if (text.Contains("offer") || text.Contains("congratulations") || text.Contains("we are pleased to offer")) return "Offered";
            if (text.Contains("rejected") || text.Contains("not moving forward") || text.Contains("unfortunately")) return "Rejected";
            if (text.Contains("application received") || text.Contains("thank you for applying")) return "Applied";
            return null;
        }

        private DateTime? ExtractDate(string text)
        {
            // Very naive date extraction: look for YYYY-MM-DD or common words
            var m = Regex.Match(text, @"(\d{4}-\d{2}-\d{2})");
            if (m.Success && DateTime.TryParse(m.Groups[1].Value, out var d)) return d;

            // Look for month names
            m = Regex.Match(text, @"(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\s+\d{1,2}(?:,\s*\d{4})?", RegexOptions.IgnoreCase);
            if (m.Success && DateTime.TryParse(m.Value, out d)) return d;

            return null;
        }

        private string? ExtractNameFromFromHeader(string from)
        {
            if (string.IsNullOrEmpty(from)) return null;
            var m = Regex.Match(from, @"^\s*([^<]+)\s*<", RegexOptions.IgnoreCase);
            if (m.Success) return Capitalize(m.Groups[1].Value.Trim());
            return null;
        }

        private string Capitalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return Regex.Replace(s.Trim(), "(^|\\s)([a-z])", m => m.Groups[1].Value + m.Groups[2].Value.ToUpper());
        }
    }
}
