using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Google.Apis.Auth.OAuth2;
using JobTrackerApi.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using System.Text;
using System.Text.RegularExpressions;

namespace JobTrackerApi.Services
{
    public class GmailEmailService
    {
        private readonly IMongoCollection<ProcessedEmail> _processedEmailCollection;
        private readonly GmailAuthService _authService;
        private readonly ILogger<GmailEmailService> _logger;

        // Common job-related keywords and domains
        private static readonly string[] JobKeywords = new[]
        {
            "application", "interview", "job", "position", "career", "opportunity",
            "resume", "cv", "candidate", "recruitment", "offer", "hiring"
        };

        private static readonly string[] JobBoardDomains = new[]
        {
            "linkedin.com", "indeed.com", "glassdoor.com", "ziprecruiter.com",
            "monster.com", "careerbuilder.com", "dice.com", "simplyhired.com",
            "workday.com", "greenhouse.io", "lever.co", "jobs.lever.co",
            "myworkdayjobs.com", "icims.com", "smartrecruiters.com"
        };

        public GmailEmailService(
            IOptions<JobApplicationDatabaseSettings> dbSettings,
            GmailAuthService authService,
            ILogger<GmailEmailService> logger)
        {
            _authService = authService;
            _logger = logger;

            var settings = dbSettings.Value;
            var mongoClient = new MongoClient(settings.ConnectionString);
            var mongoDatabase = mongoClient.GetDatabase(settings.DatabaseName);
            _processedEmailCollection = mongoDatabase.GetCollection<ProcessedEmail>(
                settings.ProcessedEmailCollectionName
            );
        }

        // Fetch new emails for a user
        public async Task<List<ProcessedEmail>> FetchNewEmailsAsync(string userId, DateTime? since = null)
        {
            try
            {
                var accessToken = await _authService.RefreshAccessTokenAsync(userId);
                var service = CreateGmailService(accessToken);

                // Build query to find job-related emails
                var query = BuildSearchQuery(since);

                var listRequest = service.Users.Messages.List("me");
                listRequest.Q = query;
                listRequest.MaxResults = 50; // Fetch up to 50 emails per sync

                var messages = await listRequest.ExecuteAsync();

                if (messages.Messages == null || messages.Messages.Count == 0)
                {
                    _logger.LogInformation($"No new emails found for user {userId}");
                    return new List<ProcessedEmail>();
                }

                var processedEmails = new List<ProcessedEmail>();

                foreach (var message in messages.Messages)
                {
                    try
                    {
                        // Check if already processed
                        var exists = await _processedEmailCollection
                            .Find(e => e.GmailMessageId == message.Id && e.UserId == userId)
                            .AnyAsync();

                        if (exists)
                        {
                            continue; // Skip already processed emails
                        }

                        // Fetch full message details
                        var fullMessage = await service.Users.Messages.Get("me", message.Id).ExecuteAsync();
                        var processedEmail = ParseEmailMessage(fullMessage, userId);

                        // Save to database
                        await _processedEmailCollection.InsertOneAsync(processedEmail);
                        processedEmails.Add(processedEmail);

                        _logger.LogInformation($"Processed email {message.Id} for user {userId}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to process email {message.Id} for user {userId}");
                    }
                }

                return processedEmails;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to fetch emails for user {userId}");
                throw;
            }
        }

        // Parse Gmail message into ProcessedEmail model
        private ProcessedEmail ParseEmailMessage(Message message, string userId)
        {
            var headers = message.Payload?.Headers ?? new List<MessagePartHeader>();

            var subject = GetHeader(headers, "Subject") ?? "";
            var from = GetHeader(headers, "From") ?? "";
            var to = GetHeader(headers, "To") ?? "";
            var dateStr = GetHeader(headers, "Date") ?? "";

            // Extract email address from "Name <email@domain.com>" format
            var fromEmail = ExtractEmailAddress(from);

            // Parse date
            var date = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(dateStr))
            {
                if (DateTime.TryParse(dateStr, out var parsedDate))
                {
                    date = parsedDate.ToUniversalTime();
                }
            }

            // Extract body
            var (plainText, html) = ExtractEmailBody(message.Payload);

            // Determine if job-related
            var isJobRelated = IsJobRelatedEmail(subject, from, plainText ?? html ?? "");

            return new ProcessedEmail
            {
                UserId = userId,
                GmailMessageId = message.Id,
                ThreadId = message.ThreadId,
                Subject = subject,
                From = from,
                FromEmail = fromEmail,
                To = to,
                Date = date,
                Snippet = message.Snippet,
                BodyPlainText = plainText,
                BodyHtml = html,
                Labels = message.LabelIds?.ToList() ?? new List<string>(),
                IsJobRelated = isJobRelated,
                ProcessedAt = DateTime.UtcNow,
                ProcessingStatus = "pending"
            };
        }

        // Extract email body (plain text and HTML)
        private (string? plainText, string? html) ExtractEmailBody(MessagePart? payload)
        {
            if (payload == null) return (null, null);

            string? plainText = null;
            string? html = null;

            if (payload.MimeType == "text/plain" && !string.IsNullOrEmpty(payload.Body?.Data))
            {
                plainText = DecodeBase64(payload.Body.Data);
            }
            else if (payload.MimeType == "text/html" && !string.IsNullOrEmpty(payload.Body?.Data))
            {
                html = DecodeBase64(payload.Body.Data);
            }
            else if (payload.Parts != null)
            {
                foreach (var part in payload.Parts)
                {
                    if (part.MimeType == "text/plain" && !string.IsNullOrEmpty(part.Body?.Data))
                    {
                        plainText = DecodeBase64(part.Body.Data);
                    }
                    else if (part.MimeType == "text/html" && !string.IsNullOrEmpty(part.Body?.Data))
                    {
                        html = DecodeBase64(part.Body.Data);
                    }
                    else if (part.Parts != null)
                    {
                        var (nestedPlain, nestedHtml) = ExtractEmailBody(part);
                        plainText ??= nestedPlain;
                        html ??= nestedHtml;
                    }
                }
            }

            return (plainText, html);
        }

        // Decode base64url encoded string
        private string DecodeBase64(string base64Url)
        {
            try
            {
                var base64 = base64Url.Replace('-', '+').Replace('_', '/');
                switch (base64.Length % 4)
                {
                    case 2: base64 += "=="; break;
                    case 3: base64 += "="; break;
                }
                var bytes = Convert.FromBase64String(base64);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return "";
            }
        }

        // Check if email is job-related
        private bool IsJobRelatedEmail(string subject, string from, string body)
        {
            var content = $"{subject} {from} {body}".ToLower();

            // Check for job-related keywords
            var hasKeyword = JobKeywords.Any(keyword => content.Contains(keyword));

            // Check for job board domains
            var fromDomain = ExtractDomain(from);
            var isFromJobBoard = JobBoardDomains.Any(domain => fromDomain.Contains(domain));

            return hasKeyword || isFromJobBoard;
        }

        // Build Gmail search query
        private string BuildSearchQuery(DateTime? since)
        {
            var queries = new List<string>();

            // Search for job-related keywords
            var keywordQuery = string.Join(" OR ", JobKeywords.Select(k => $"subject:{k} OR {k}"));
            queries.Add($"({keywordQuery})");

            // Search from job board domains
            var domainQuery = string.Join(" OR ", JobBoardDomains.Select(d => $"from:*@{d}"));
            queries.Add($"({domainQuery})");

            // Combine with OR
            var mainQuery = string.Join(" OR ", queries);

            // Add date filter if provided
            if (since.HasValue)
            {
                var afterDate = since.Value.ToString("yyyy/MM/dd");
                mainQuery = $"({mainQuery}) after:{afterDate}";
            }

            return mainQuery;
        }

        // Helper methods
        private string? GetHeader(IList<MessagePartHeader> headers, string name)
        {
            return headers.FirstOrDefault(h =>
                h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
        }

        private string ExtractEmailAddress(string from)
        {
            var match = Regex.Match(from, @"<(.+?)>|^([^\s<>]+@[^\s<>]+)$");
            return match.Success ? (match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : match.Groups[2].Value) : from;
        }

        private string ExtractDomain(string email)
        {
            var emailAddress = ExtractEmailAddress(email);
            var atIndex = emailAddress.LastIndexOf('@');
            return atIndex > 0 ? emailAddress.Substring(atIndex + 1).ToLower() : "";
        }

        private GmailService CreateGmailService(string accessToken)
        {
            var credential = GoogleCredential.FromAccessToken(accessToken);

            return new GmailService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "JobTracker"
            });
        }

        // Get processed emails for a user
        public async Task<List<ProcessedEmail>> GetProcessedEmailsAsync(string userId, int limit = 50)
        {
            return await _processedEmailCollection
                .Find(e => e.UserId == userId)
                .SortByDescending(e => e.Date)
                .Limit(limit)
                .ToListAsync();
        }

        // Get job-related emails only
        public async Task<List<ProcessedEmail>> GetJobRelatedEmailsAsync(string userId, int limit = 50)
        {
            return await _processedEmailCollection
                .Find(e => e.UserId == userId && e.IsJobRelated)
                .SortByDescending(e => e.Date)
                .Limit(limit)
                .ToListAsync();
        }
    }
}