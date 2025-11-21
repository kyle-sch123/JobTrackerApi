using JobTrackerApi.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace JobTrackerApi.Services
{
    public class EmailProcessingService
    {
        private readonly IMongoCollection<ProcessedEmail> _processedEmailCollection;
        private readonly ClaudeEmailParserService _parserService;
        private readonly ApplicationMatchingService _matchingService;
        private readonly JobApplicationService _jobApplicationService;
        private readonly ILogger<EmailProcessingService> _logger;

        public EmailProcessingService(
            IOptions<JobApplicationDatabaseSettings> dbSettings,
            ClaudeEmailParserService parserService,
            ApplicationMatchingService matchingService,
            JobApplicationService jobApplicationService,
            ILogger<EmailProcessingService> logger)
        {
            _parserService = parserService;
            _matchingService = matchingService;
            _jobApplicationService = jobApplicationService;
            _logger = logger;

            var settings = dbSettings.Value;
            var mongoClient = new MongoClient(settings.ConnectionString);
            var mongoDatabase = mongoClient.GetDatabase(settings.DatabaseName);
            _processedEmailCollection = mongoDatabase.GetCollection<ProcessedEmail>(
                settings.ProcessedEmailCollectionName
            );
        }

        // Process a single email with AI
        public async Task<ProcessingResult> ProcessEmailWithAIAsync(ProcessedEmail email)
        {
            var result = new ProcessingResult
            {
                EmailId = email.Id!,
                Success = false
            };

            try
            {
                _logger.LogInformation($"Processing email {email.GmailMessageId} with AI");

                // Step 1: Parse email with Claude
                var extractedData = await _parserService.ParseEmailAsync(email);
                
                result.ExtractedData = extractedData;
                result.Confidence = extractedData.Confidence;

                // Update email with extracted data
                var emailUpdate = Builders<ProcessedEmail>.Update
                    .Set(e => e.ExtractedData, extractedData)
                    .Set(e => e.AiParsed, true);

                await _processedEmailCollection.UpdateOneAsync(e => e.Id == email.Id, emailUpdate);

                // Step 2: Determine action based on confidence
                if (_parserService.ShouldAutoProcess(extractedData.Confidence))
                {
                    // High confidence - auto create/update
                    result.Action = "auto_processed";
                    await AutoProcessApplication(email, extractedData, result);
                }
                else if (_parserService.RequiresReview(extractedData.Confidence))
                {
                    // Medium confidence - flag for review
                    result.Action = "requires_review";
                    result.Message = "Extracted data requires user review";
                    
                    var reviewUpdate = Builders<ProcessedEmail>.Update
                        .Set(e => e.ProcessingStatus, "requires_review");
                    
                    await _processedEmailCollection.UpdateOneAsync(e => e.Id == email.Id, reviewUpdate);
                }
                else
                {
                    // Low confidence - ignore or manual processing
                    result.Action = "low_confidence";
                    result.Message = "Confidence too low for automatic processing";
                    
                    var ignoreUpdate = Builders<ProcessedEmail>.Update
                        .Set(e => e.ProcessingStatus, "ignored");
                    
                    await _processedEmailCollection.UpdateOneAsync(e => e.Id == email.Id, ignoreUpdate);
                }

                result.Success = true;
                _logger.LogInformation(
                    $"Processed email {email.GmailMessageId}: Action={result.Action}, " +
                    $"Confidence={extractedData.Confidence:F1}%"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to process email {email.GmailMessageId}");
                result.Message = ex.Message;
                
                var failedUpdate = Builders<ProcessedEmail>.Update
                    .Set(e => e.ProcessingStatus, "failed");
                
                await _processedEmailCollection.UpdateOneAsync(e => e.Id == email.Id, failedUpdate);
            }

            return result;
        }

        private async Task AutoProcessApplication(
            ProcessedEmail email, 
            EmailExtractedData extractedData,
            ProcessingResult result)
        {
            // Find matching existing application
            var existingApp = await _matchingService.FindMatchingApplicationAsync(
                email.UserId, 
                extractedData
            );

            var shouldCreateNew = _matchingService.ShouldCreateNew(existingApp, extractedData, email);

            if (shouldCreateNew)
            {
                // Create new job application
                var newApp = CreateJobApplicationFromEmail(email, extractedData);
                await _jobApplicationService.CreateAsync(newApp);

                // Link email to job application
                var emailUpdate = Builders<ProcessedEmail>.Update
                    .Set(e => e.JobApplicationId, newApp.Id)
                    .Set(e => e.ProcessingStatus, "processed");

                await _processedEmailCollection.UpdateOneAsync(e => e.Id == email.Id, emailUpdate);

                result.JobApplicationId = newApp.Id;
                result.Message = $"Created new application for {extractedData.CompanyName} - {extractedData.Position}";
                
                _logger.LogInformation($"Created job application {newApp.Id} from email {email.GmailMessageId}");
            }
            else if (existingApp != null)
            {
                // Update existing application
                await UpdateJobApplicationFromEmail(existingApp, email, extractedData);

                // Link email to job application
                var emailUpdate = Builders<ProcessedEmail>.Update
                    .Set(e => e.JobApplicationId, existingApp.Id)
                    .Set(e => e.ProcessingStatus, "processed");

                await _processedEmailCollection.UpdateOneAsync(e => e.Id == email.Id, emailUpdate);

                result.JobApplicationId = existingApp.Id;
                result.Message = $"Updated application for {extractedData.CompanyName}";
                
                _logger.LogInformation($"Updated job application {existingApp.Id} from email {email.GmailMessageId}");
            }
        }

        private JobApplication CreateJobApplicationFromEmail(
            ProcessedEmail email, 
            EmailExtractedData extractedData)
        {
            return new JobApplication
            {
                userId = email.UserId,
                company = extractedData.CompanyName ?? "Unknown",
                jobTitle = extractedData.Position ?? "Unknown Position",
                status = extractedData.ApplicationStatus ?? "Applied",
                applicationDate = email.Date,
                notes = $"Auto-created from email: {email.Subject}",
                
                // New fields from Phase 1
                RecruiterName = extractedData.RecruiterName,
                RecruiterEmail = extractedData.RecruiterEmail,
                InterviewDate = extractedData.InterviewDate,
                InterviewType = extractedData.InterviewType,
                SalaryRange = extractedData.SalaryRange,
                AutoCreated = true,
                AiConfidence = extractedData.Confidence,
                RequiresReview = false,
                EmailIds = new List<string> { email.Id! }
            };
        }

        private async Task UpdateJobApplicationFromEmail(
            JobApplication app, 
            ProcessedEmail email, 
            EmailExtractedData extractedData)
        {
            // Determine what to update based on extracted data
            var shouldUpdateStatus = ShouldUpdateStatus(app.status, extractedData.ApplicationStatus);
            
            if (shouldUpdateStatus)
            {
                app.status = extractedData.ApplicationStatus ?? app.status;
                app.autoStatusUpdated = true;
            }

            // Update interview details if present
            if (extractedData.InterviewDate.HasValue)
            {
                app.InterviewDate = extractedData.InterviewDate;
                app.InterviewType = extractedData.InterviewType;
            }

            // Update recruiter info if present
            if (!string.IsNullOrEmpty(extractedData.RecruiterName))
            {
                app.RecruiterName = extractedData.RecruiterName;
            }

            if (!string.IsNullOrEmpty(extractedData.RecruiterEmail))
            {
                app.RecruiterEmail = extractedData.RecruiterEmail;
            }

            // Add email ID to tracking
            if (app.EmailIds == null)
            {
                app.EmailIds = new List<string>();
            }
            
            if (!app.EmailIds.Contains(email.Id!))
            {
                app.EmailIds.Add(email.Id!);
            }

            // Append notes
            var noteAddition = $"\n[AI Update {DateTime.UtcNow:yyyy-MM-dd}]: {email.Subject}";
            app.notes = (app.notes ?? "") + noteAddition;

            await _jobApplicationService.UpdateAsync(app.Id!, app);
        }

        private bool ShouldUpdateStatus(string currentStatus, string? newStatus)
        {
            if (string.IsNullOrEmpty(newStatus)) return false;

            // Status progression rules
            var statusHierarchy = new Dictionary<string, int>
            {
                { "Applied", 1 },
                { "In Progress", 2 },
                { "Interview Scheduled", 3 },
                { "Offer", 4 },
                { "Rejected", 5 },
                { "Accepted", 6 },
                { "Declined", 6 }
            };

            var currentLevel = statusHierarchy.GetValueOrDefault(currentStatus, 0);
            var newLevel = statusHierarchy.GetValueOrDefault(newStatus, 0);

            // Only update if new status is more advanced (except rejection can happen anytime)
            return newStatus == "Rejected" || newLevel > currentLevel;
        }

        // Process all pending emails for a user
        public async Task<List<ProcessingResult>> ProcessPendingEmailsAsync(string userId)
        {
            var results = new List<ProcessingResult>();

            var pendingEmails = await _processedEmailCollection
                .Find(e => e.UserId == userId && 
                          e.IsJobRelated && 
                          !e.AiParsed &&
                          e.ProcessingStatus == "pending")
                .ToListAsync();

            _logger.LogInformation($"Processing {pendingEmails.Count} pending emails for user {userId}");

            foreach (var email in pendingEmails)
            {
                var result = await ProcessEmailWithAIAsync(email);
                results.Add(result);

                // Small delay to avoid rate limiting
                await Task.Delay(500);
            }

            return results;
        }

        // Get emails that require manual review
        public async Task<List<ProcessedEmail>> GetEmailsRequiringReviewAsync(string userId)
        {
            return await _processedEmailCollection
                .Find(e => e.UserId == userId && e.ProcessingStatus == "requires_review")
                .SortByDescending(e => e.Date)
                .ToListAsync();
        }
    }

    public class ProcessingResult
    {
        public string EmailId { get; set; } = null!;
        public bool Success { get; set; }
        public string Action { get; set; } = null!; // "auto_processed", "requires_review", "low_confidence"
        public string? Message { get; set; }
        public string? JobApplicationId { get; set; }
        public EmailExtractedData? ExtractedData { get; set; }
        public double Confidence { get; set; }
    }
}