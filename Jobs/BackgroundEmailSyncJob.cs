using JobTrackerApi.Services;

namespace JobTrackerApi.Jobs
{
    public class BackgroundEmailSyncJob
    {
        private readonly EmailSyncService _syncService;
        private readonly ILogger<BackgroundEmailSyncJob> _logger;

        public BackgroundEmailSyncJob(
            EmailSyncService syncService,
            ILogger<BackgroundEmailSyncJob> logger)
        {
            _syncService = syncService;
            _logger = logger;
        }

        // This method will be called by Hangfire on schedule
        public async Task ExecuteAsync()
        {
            try
            {
                _logger.LogInformation("Starting scheduled email sync job");
                
                var results = await _syncService.SyncAllUsersAsync();
                
                var successCount = results.Count(r => r.Status == "completed");
                var failCount = results.Count(r => r.Status == "failed");
                var totalEmails = results.Sum(r => r.EmailsProcessed);

                _logger.LogInformation(
                    $"Scheduled email sync completed. Users: {results.Count}, " +
                    $"Success: {successCount}, Failed: {failCount}, " +
                    $"Total emails processed: {totalEmails}"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled email sync job failed");
                throw; // Re-throw so Hangfire knows the job failed
            }
        }
    }
}