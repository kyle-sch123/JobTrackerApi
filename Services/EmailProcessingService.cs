using JobTrackerApi.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace JobTrackerApi.Services
{
    public class EmailProcessingService
    {
        private readonly IMongoCollection<ProcessedEmail> _processedEmailCollection;
        private readonly HybridEmailParser _hybridParser; // CHANGED: Use hybrid parser
        private readonly ApplicationMatchingService _matchingService;
        private readonly JobApplicationService _jobApplicationService;
        private readonly JobRelatedEmailFilter _jobFilter;
        private readonly ILogger<EmailProcessingService> _logger;

        public EmailProcessingService(
            IOptions<JobApplicationDatabaseSettings> dbSettings,
            HybridEmailParser hybridParser, // CHANGED: Inject hybrid parser
            ApplicationMatchingService matchingService,
            JobApplicationService jobApplicationService,
            JobRelatedEmailFilter jobFilter,
            ILogger<EmailProcessingService> logger)
        {
            _hybridParser = hybridParser;
            _matchingService = matchingService;
            _jobApplicationService = jobApplicationService;
            _jobFilter = jobFilter;
            _logger = logger;

            var settings = dbSettings.Value;
            var mongoClient = new MongoClient(settings.ConnectionString);
            var mongoDatabase = mongoClient.GetDatabase(settings.DatabaseName);
            _processedEmailCollection = mongoDatabase.GetCollection<ProcessedEmail>(
                settings.ProcessedEmailCollectionName
            );
        }

        // Process a single email with hybrid parsing
        public async Task<ProcessingResult> ProcessEmailWithHybridAsync(ProcessedEmail email, bool forceProcess = false)
        {
            var result = new ProcessingResult
            {
                EmailId = email.Id!,
                Success = false
            };

            try
            {
                _logger.LogInformation($"🔄 Processing email {email.GmailMessageId} (forced: {forceProcess})");

                // Step 0: Pre-filter - Check if email is job-related to avoid unnecessary AI calls
                var isJobRelated = forceProcess || _jobFilter.IsJobRelated(email);

                if (!isJobRelated)
                {
                    _logger.LogInformation($"🚫 Email not job-related, skipping AI processing: {email.Subject}");

                    // Mark as not job-related and skip processing
                    var skipUpdate = Builders<ProcessedEmail>.Update
                        .Set(e => e.IsJobRelated, false)
                        .Set(e => e.ProcessingStatus, "skipped_not_job_related")
                        .Set(e => e.ProcessedAt, DateTime.UtcNow);

                    await _processedEmailCollection.UpdateOneAsync(e => e.Id == email.Id, skipUpdate);

                    result.Success = true;
                    result.Action = "skipped_not_job_related";
                    result.Message = "Email determined not job-related, skipped AI processing";

                    return result;
                }

                // Mark as job-related
                var jobRelatedUpdate = Builders<ProcessedEmail>.Update
                    .Set(e => e.IsJobRelated, true);

                await _processedEmailCollection.UpdateOneAsync(e => e.Id == email.Id, jobRelatedUpdate);

                // Step 1: Parse email with HYBRID parser (rule-based → LLM)
                var extractedData = await _hybridParser.ParseEmailAsync(email);

                result.ExtractedData = extractedData;
                result.Confidence = extractedData.Confidence;
                result.ExtractionMethod = extractedData.ExtractionMethod; // NEW: Track method used

                // Update email with extracted data
                var emailUpdate = Builders<ProcessedEmail>.Update
                    .Set(e => e.ExtractedData, extractedData)
                    .Set(e => e.AiParsed, true)
                    .Set(e => e.ExtractionMethod, extractedData.ExtractionMethod); // NEW: Store method

                await _processedEmailCollection.UpdateOneAsync(e => e.Id == email.Id, emailUpdate);

                // Step 2: Determine action based on confidence
                if (forceProcess || _hybridParser.ShouldAutoProcess(extractedData.Confidence))
                {
                    result.Action = "auto_processed";
                    await AutoProcessApplication(email, extractedData, result);
                }
                else if (_hybridParser.RequiresReview(extractedData.Confidence))
                {
                    result.Action = "requires_review";
                    result.Message = "Extracted data requires user review";

                    var reviewUpdate = Builders<ProcessedEmail>.Update
                        .Set(e => e.ProcessingStatus, "requires_review");

                    await _processedEmailCollection.UpdateOneAsync(e => e.Id == email.Id, reviewUpdate);
                }
                else
                {
                    result.Action = "low_confidence";
                    result.Message = "Confidence too low for automatic processing";

                    var ignoreUpdate = Builders<ProcessedEmail>.Update
                        .Set(e => e.ProcessingStatus, "ignored");

                    await _processedEmailCollection.UpdateOneAsync(e => e.Id == email.Id, ignoreUpdate);
                }

                result.Success = true;
                _logger.LogInformation(
                    $"✅ Processed: Action={result.Action}, Confidence={extractedData.Confidence:F1}%, " +
                    $"Method={extractedData.ExtractionMethod}"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"❌ Failed to process email {email.GmailMessageId}");
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
            // Step 1: Check if matching application exists
            var existingApp = await _matchingService.FindMatchingApplicationAsync(
                email.UserId,
                extractedData
            );

            // FIXED: Simplified logic - if no existing app, always create new
            if (existingApp == null)
            {
                // No matching application found - create new one
                var newApp = CreateJobApplicationFromEmail(email, extractedData);
                await _jobApplicationService.CreateAsync(newApp);

                var emailUpdate = Builders<ProcessedEmail>.Update
                    .Set(e => e.JobApplicationId, newApp.Id)
                    .Set(e => e.ProcessingStatus, "processed");

                await _processedEmailCollection.UpdateOneAsync(e => e.Id == email.Id, emailUpdate);

                result.JobApplicationId = newApp.Id;
                result.Message = $"Created new application for {extractedData.CompanyName} - {extractedData.Position}";

                _logger.LogInformation(
                    $"✅ Created job application {newApp.Id} from email {email.GmailMessageId} " +
                    $"(Company: {extractedData.CompanyName}, Position: {extractedData.Position})"
                );
            }
            else
            {
                // Existing application found - decide whether to update or create new
                var shouldCreateNew = _matchingService.ShouldCreateNew(existingApp, extractedData, email);

                if (shouldCreateNew)
                {
                    // Create new application (e.g., same company but different position)
                    var newApp = CreateJobApplicationFromEmail(email, extractedData);
                    await _jobApplicationService.CreateAsync(newApp);

                    var emailUpdate = Builders<ProcessedEmail>.Update
                        .Set(e => e.JobApplicationId, newApp.Id)
                        .Set(e => e.ProcessingStatus, "processed");

                    await _processedEmailCollection.UpdateOneAsync(e => e.Id == email.Id, emailUpdate);

                    result.JobApplicationId = newApp.Id;
                    result.Message = $"Created separate application for {extractedData.CompanyName} - {extractedData.Position} " +
                                   $"(different from existing application)";

                    _logger.LogInformation(
                        $"✅ Created new job application {newApp.Id} (separate from existing {existingApp.Id}) " +
                        $"from email {email.GmailMessageId}"
                    );
                }
                else
                {
                    // Update existing application
                    await UpdateJobApplicationFromEmail(existingApp, email, extractedData);

                    var emailUpdate = Builders<ProcessedEmail>.Update
                        .Set(e => e.JobApplicationId, existingApp.Id)
                        .Set(e => e.ProcessingStatus, "processed");

                    await _processedEmailCollection.UpdateOneAsync(e => e.Id == email.Id, emailUpdate);

                    result.JobApplicationId = existingApp.Id;
                    result.Message = $"Updated existing application for {extractedData.CompanyName}";

                    _logger.LogInformation(
                        $"🔄 Updated existing job application {existingApp.Id} from email {email.GmailMessageId}"
                    );
                }
            }
        }

        private JobApplication CreateJobApplicationFromEmail(
            ProcessedEmail email,
            EmailExtractedData extractedData)
        {
            var companyName = extractedData.CompanyName;
            if (string.IsNullOrWhiteSpace(companyName) || companyName == "Unknown Company")
            {
                companyName = $"Company from {email.FromEmail}";
            }

            var position = extractedData.Position;
            if (string.IsNullOrWhiteSpace(position))
            {
                position = "Position Not Specified";
            }

            var status = extractedData.ApplicationStatus;
            if (string.IsNullOrWhiteSpace(status))
            {
                status = "Applied";
            }

            return new JobApplication
            {
                userId = email.UserId,
                company = companyName,
                jobTitle = position,
                status = status,
                applicationDate = email.Date,
                notes = $"Auto-created from email: {email.Subject}\n" +
                       $"Extraction method: {extractedData.ExtractionMethod}\n" +
                       $"Confidence: {extractedData.Confidence:F0}%\n" +
                       $"Original From: {email.From}",

                RecruiterName = extractedData.RecruiterName,
                RecruiterEmail = extractedData.RecruiterEmail ?? email.FromEmail,
                InterviewDate = extractedData.InterviewDate,
                InterviewType = extractedData.InterviewType,
                SalaryRange = extractedData.SalaryRange,
                AutoCreated = true,
                AiConfidence = extractedData.Confidence,
                RequiresReview = extractedData.Confidence < 70,
                EmailIds = new List<string> { email.Id! },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
        }

        private async Task UpdateJobApplicationFromEmail(
            JobApplication app,
            ProcessedEmail email,
            EmailExtractedData extractedData)
        {
            var shouldUpdateStatus = ShouldUpdateStatus(app.status, extractedData.ApplicationStatus);

            if (shouldUpdateStatus && !string.IsNullOrWhiteSpace(extractedData.ApplicationStatus))
            {
                app.status = extractedData.ApplicationStatus;
                app.autoStatusUpdated = true;
                _logger.LogInformation($"Updated status to {extractedData.ApplicationStatus} for application {app.Id}");
            }

            if (extractedData.InterviewDate.HasValue)
            {
                app.InterviewDate = extractedData.InterviewDate;
                app.InterviewType = extractedData.InterviewType;
            }

            if (!string.IsNullOrEmpty(extractedData.RecruiterName) && string.IsNullOrEmpty(app.RecruiterName))
            {
                app.RecruiterName = extractedData.RecruiterName;
            }

            if (!string.IsNullOrEmpty(extractedData.RecruiterEmail) && string.IsNullOrEmpty(app.RecruiterEmail))
            {
                app.RecruiterEmail = extractedData.RecruiterEmail;
            }

            if (app.EmailIds == null)
            {
                app.EmailIds = new List<string>();
            }

            if (!app.EmailIds.Contains(email.Id!))
            {
                app.EmailIds.Add(email.Id!);
            }

            app.UpdatedAt = DateTime.UtcNow;

            var noteAddition = $"\n[Hybrid Update {DateTime.UtcNow:yyyy-MM-dd HH:mm}]: " +
                              $"Status={extractedData.ApplicationStatus}, " +
                              $"Confidence={extractedData.Confidence:F0}%, " +
                              $"Method={extractedData.ExtractionMethod}";
            app.notes = (app.notes ?? "") + noteAddition;

            await _jobApplicationService.UpdateAsync(app.Id!, app);
        }

        private bool ShouldUpdateStatus(string currentStatus, string? newStatus)
        {
            if (string.IsNullOrEmpty(newStatus)) return false;

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

            return newStatus == "Rejected" || newLevel > currentLevel;
        }

        public async Task<List<ProcessingResult>> ProcessPendingEmailsAsync(string userId)
        {
            var results = new List<ProcessingResult>();

            var pendingEmails = await _processedEmailCollection
                .Find(e => e.UserId == userId &&
                          e.IsJobRelated &&
                          !e.AiParsed &&
                          e.ProcessingStatus == "pending")
                .ToListAsync();

            _logger.LogInformation($"📧 Processing {pendingEmails.Count} pending emails for user {userId}");

            foreach (var email in pendingEmails)
            {
                var result = await ProcessEmailWithHybridAsync(email);
                results.Add(result);

                await Task.Delay(500);
            }

            // Log statistics
            var ruleBasedCount = results.Count(r => r.ExtractedData?.ExtractionMethod == "rule-based");
            var refinedCount = results.Count(r => r.ExtractedData?.ExtractionMethod == "hybrid-refined");
            var llmFullCount = results.Count(r => r.ExtractedData?.ExtractionMethod == "llm-full");

            _logger.LogInformation(
                $"✅ Completed: {results.Count} total, " +
                $"Rule-based: {ruleBasedCount} ({ruleBasedCount * 100.0 / results.Count:F0}%), " +
                $"Refined: {refinedCount}, " +
                $"LLM-full: {llmFullCount}"
            );

            return results;
        }

        public async Task<List<ProcessedEmail>> GetEmailsRequiringReviewAsync(string userId)
        {
            return await _processedEmailCollection
                .Find(e => e.UserId == userId && e.ProcessingStatus == "requires_review")
                .SortByDescending(e => e.Date)
                .ToListAsync();
        }

        public async Task<ProcessedEmail?> GetEmailByIdAsync(string emailId, string userId)
        {
            return await _processedEmailCollection
                .Find(e => e.Id == emailId && e.UserId == userId)
                .FirstOrDefaultAsync();
        }

        public async Task<object> GetProcessingStatsAsync(string userId)
        {
            var allEmails = await _processedEmailCollection
                .Find(e => e.UserId == userId)
                .ToListAsync();

            var jobRelatedEmails = allEmails.Where(e => e.IsJobRelated).ToList();

            return new
            {
                totalEmails = allEmails.Count,
                jobRelatedEmails = jobRelatedEmails.Count,
                aiParsed = jobRelatedEmails.Count(e => e.AiParsed),
                pending = jobRelatedEmails.Count(e => e.ProcessingStatus == "pending"),
                processed = jobRelatedEmails.Count(e => e.ProcessingStatus == "processed"),
                requiresReview = jobRelatedEmails.Count(e => e.ProcessingStatus == "requires_review"),
                ignored = jobRelatedEmails.Count(e => e.ProcessingStatus == "ignored"),
                failed = jobRelatedEmails.Count(e => e.ProcessingStatus == "failed"),
                averageConfidence = jobRelatedEmails.Where(e => e.ExtractedData != null)
                    .Select(e => e.ExtractedData!.Confidence)
                    .DefaultIfEmpty(0)
                    .Average(),
                breakdown = new
                {
                    highConfidence = jobRelatedEmails.Count(e => e.ExtractedData != null && e.ExtractedData.Confidence >= 70),
                    mediumConfidence = jobRelatedEmails.Count(e => e.ExtractedData != null && e.ExtractedData.Confidence >= 40 && e.ExtractedData.Confidence < 70),
                    lowConfidence = jobRelatedEmails.Count(e => e.ExtractedData != null && e.ExtractedData.Confidence < 40)
                },
                extractionMethods = new
                {
                    ruleBased = jobRelatedEmails.Count(e => e.ExtractionMethod == "rule-based"),
                    hybridRefined = jobRelatedEmails.Count(e => e.ExtractionMethod == "hybrid-refined"),
                    llmFull = jobRelatedEmails.Count(e => e.ExtractionMethod == "llm-full")
                }
            };
        }

        public async Task<bool> MarkEmailAsIgnoredAsync(string emailId, string userId)
        {
            var update = Builders<ProcessedEmail>.Update
                .Set(e => e.ProcessingStatus, "ignored");

            var result = await _processedEmailCollection.UpdateOneAsync(
                e => e.Id == emailId && e.UserId == userId,
                update
            );

            return result.ModifiedCount > 0;
        }

        public async Task<bool> UpdateExtractedDataAsync(string emailId, string userId, EmailExtractedData extractedData)
        {
            var update = Builders<ProcessedEmail>.Update
                .Set(e => e.ExtractedData, extractedData)
                .Set(e => e.AiParsed, true);

            var result = await _processedEmailCollection.UpdateOneAsync(
                e => e.Id == emailId && e.UserId == userId,
                update
            );

            return result.ModifiedCount > 0;
        }
    }

    public class ProcessingResult
    {
        public string EmailId { get; set; } = null!;
        public bool Success { get; set; }
        public string Action { get; set; } = null!;
        public string? Message { get; set; }
        public string? JobApplicationId { get; set; }
        public EmailExtractedData? ExtractedData { get; set; }
        public double Confidence { get; set; }
        public string? ExtractionMethod { get; set; } // NEW: Track which method was used
    }
}