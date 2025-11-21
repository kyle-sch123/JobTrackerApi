using JobTrackerApi.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace JobTrackerApi.Services
{
    public class EmailSyncService
    {
        private readonly IMongoCollection<EmailSyncHistory> _syncHistoryCollection;
        private readonly IMongoCollection<UserEmailConnection> _connectionCollection;
        private readonly GmailAuthService _authService;
        private readonly GmailEmailService _emailService;
        private readonly ClaudeEmailParserService _parserService;
        private readonly JobApplicationService _jobService;
        private readonly ILogger<EmailSyncService> _logger;

        public EmailSyncService(
            IOptions<JobApplicationDatabaseSettings> dbSettings,
            GmailAuthService authService,
            GmailEmailService emailService,
            ClaudeEmailParserService parserService,
            JobApplicationService jobService,
            ILogger<EmailSyncService> logger)
        {
            _authService = authService;
            _emailService = emailService;
            _parserService = parserService;
            _jobService = jobService;
            _logger = logger;

            var settings = dbSettings.Value;
            var mongoClient = new MongoClient(settings.ConnectionString);
            var mongoDatabase = mongoClient.GetDatabase(settings.DatabaseName);
            
            _syncHistoryCollection = mongoDatabase.GetCollection<EmailSyncHistory>(
                settings.EmailSyncHistoryCollectionName
            );
            _connectionCollection = mongoDatabase.GetCollection<UserEmailConnection>(
                settings.UserEmailConnectionCollectionName
            );
        }

        // Sync emails for a specific user
        public async Task<EmailSyncHistory> SyncUserEmailsAsync(string userId, string triggerType = "manual")
        {
            var syncHistory = new EmailSyncHistory
            {
                UserId = userId,
                SyncStartedAt = DateTime.UtcNow,
                Status = "running",
                TriggerType = triggerType
            };

            try
            {
                await _syncHistoryCollection.InsertOneAsync(syncHistory);

                // Get user's connection
                var connection = await _authService.GetConnectionAsync(userId);
                if (connection == null)
                {
                    throw new Exception("No active Gmail connection found");
                }

                // Get last sync time
                var lastSyncTime = connection.LastSyncAt ?? DateTime.UtcNow.AddDays(-30);

                // Fetch new emails
                var newEmails = await _emailService.FetchNewEmailsAsync(userId, lastSyncTime);

                // Process each email: parse and create/update job applications when appropriate
                var processedCount = 0;
                foreach (var email in newEmails)
                {
                    try
                    {
                        var extracted = await _parserService.ParseEmailAsync(email);
                        email.ExtractedData = extracted;
                        email.AiParsed = true;

                        // Mark as job related if parser found company or position or has non-zero confidence
                        if (!string.IsNullOrEmpty(extracted.CompanyName) || !string.IsNullOrEmpty(extracted.Position) || extracted.Confidence > 0.2)
                        {
                            email.IsJobRelated = true;

                            // Try to match existing job by job URL or company+position
                            JobApplication? match = null;
                            var existingJobs = await _jobService.GetByUserAsync(userId);

                            // Try to match by company + job title
                            if (!string.IsNullOrEmpty(extracted.CompanyName) || !string.IsNullOrEmpty(extracted.Position))
                            {
                                match = existingJobs.FirstOrDefault(j =>
                                    (!string.IsNullOrEmpty(extracted.CompanyName) && !string.IsNullOrEmpty(j.company) && j.company.Equals(extracted.CompanyName, StringComparison.OrdinalIgnoreCase))
                                    || (!string.IsNullOrEmpty(extracted.Position) && !string.IsNullOrEmpty(j.jobTitle) && j.jobTitle.Equals(extracted.Position, StringComparison.OrdinalIgnoreCase))
                                );
                            }

                            if (match != null)
                            {
                                // Update fields conservatively
                                match.company = string.IsNullOrEmpty(match.company) ? (extracted.CompanyName ?? match.company) : match.company;
                                match.jobTitle = string.IsNullOrEmpty(match.jobTitle) ? (extracted.Position ?? match.jobTitle) : match.jobTitle;
                                match.status = extracted.ApplicationStatus ?? match.status;

                                await _jobService.UpdateAsync(match.Id, match);
                                email.JobApplicationId = match.Id;
                            }
                            else
                            {
                                // Create new job application
                                var newJob = new JobApplication
                                {
                                    userId = userId,
                                    jobTitle = extracted.Position ?? (email.Subject ?? ""),
                                    company = extracted.CompanyName ?? "",
                                    applicationDate = email.Date,
                                    status = extracted.ApplicationStatus ?? "Applied"
                                };

                                await _jobService.CreateAsync(newJob);
                                email.JobApplicationId = newJob.Id;
                            }
                        }

                        email.ProcessingStatus = "processed";
                        processedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to parse/process email {MessageId}", email.GmailMessageId);
                        email.ProcessingStatus = "failed";
                        email.AiParsed = false;
                        email.ExtractedData = null;
                    }

                    // Save the updated processed email record
                    await _emailService.UpdateProcessedEmailAsync(email);
                }

                // Update sync history
                syncHistory.EmailsProcessed = newEmails.Count;
                syncHistory.EmailsParsed = processedCount;
                syncHistory.Status = "completed";
                syncHistory.SyncCompletedAt = DateTime.UtcNow;

                // Update connection's last sync time
                var connectionUpdate = Builders<UserEmailConnection>.Update
                    .Set(c => c.LastSyncAt, DateTime.UtcNow)
                    .Set(c => c.LastSyncStatus, "success");

                await _connectionCollection.UpdateOneAsync(c => c.Id == connection.Id, connectionUpdate);

                _logger.LogInformation($"Successfully synced {newEmails.Count} emails for user {userId} ({processedCount} parsed)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to sync emails for user {userId}");
                
                syncHistory.Status = "failed";
                syncHistory.SyncCompletedAt = DateTime.UtcNow;
                syncHistory.Errors.Add(ex.Message);

                // Update connection status
                var connection = await _authService.GetConnectionAsync(userId);
                if (connection != null)
                {
                    var connectionUpdate = Builders<UserEmailConnection>.Update
                        .Set(c => c.LastSyncStatus, "failed")
                        .Set(c => c.SyncErrorMessage, ex.Message);

                    await _connectionCollection.UpdateOneAsync(c => c.Id == connection.Id, connectionUpdate);
                }
            }
            finally
            {
                await _syncHistoryCollection.ReplaceOneAsync(s => s.Id == syncHistory.Id, syncHistory);
            }

            return syncHistory;
        }

        // Sync all users (for background job)
        public async Task<List<EmailSyncHistory>> SyncAllUsersAsync()
        {
            var results = new List<EmailSyncHistory>();
            
            try
            {
                var activeConnections = await _authService.GetAllActiveConnectionsAsync();
                _logger.LogInformation($"Starting scheduled sync for {activeConnections.Count} users");

                foreach (var connection in activeConnections)
                {
                    try
                    {
                        var syncResult = await SyncUserEmailsAsync(connection.UserId, "scheduled");
                        results.Add(syncResult);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Failed to sync emails for user {connection.UserId}");
                    }

                    // Add small delay between users to avoid rate limiting
                    await Task.Delay(1000);
                }

                _logger.LogInformation($"Completed scheduled sync for {activeConnections.Count} users");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync all users");
            }

            return results;
        }

        // Get sync history for a user
        public async Task<List<EmailSyncHistory>> GetSyncHistoryAsync(string userId, int limit = 20)
        {
            return await _syncHistoryCollection
                .Find(s => s.UserId == userId)
                .SortByDescending(s => s.SyncStartedAt)
                .Limit(limit)
                .ToListAsync();
        }

        // Get latest sync status for a user
        public async Task<EmailSyncHistory?> GetLatestSyncAsync(string userId)
        {
            return await _syncHistoryCollection
                .Find(s => s.UserId == userId)
                .SortByDescending(s => s.SyncStartedAt)
                .FirstOrDefaultAsync();
        }
    }
}