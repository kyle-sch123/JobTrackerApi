using JobTrackerApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace JobTrackerApi.Controllers
{
    [ApiController]
    [Route("api/email-sync")]
    public class EmailSyncController : BaseController
    {
        private readonly EmailSyncService _syncService;
        private readonly ILogger<EmailSyncController> _logger;

        public EmailSyncController(
            EmailSyncService syncService,
            ILogger<EmailSyncController> logger)
        {
            _syncService = syncService;
            _logger = logger;
        }

        // POST: api/email-sync/run
        // Manually trigger a Gmail scan for the authenticated user (fetch new
        // emails + run them through the hybrid processing pipeline). Runs
        // synchronously and returns the resulting sync summary.
        [HttpPost("run")]
        public async Task<IActionResult> Run()
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized(new { error = "User not authenticated" });
            }

            _logger.LogInformation("Manual email sync requested by user {UserId}", userId);

            var history = await _syncService.SyncUserEmailsAsync(userId, "manual");

            return Ok(new
            {
                status = history.Status,
                triggerType = history.TriggerType,
                emailsProcessed = history.EmailsProcessed,
                emailsParsed = history.EmailsParsed,
                startedAt = history.SyncStartedAt,
                completedAt = history.SyncCompletedAt,
                errors = history.Errors
            });
        }

        // GET: api/email-sync/history
        // Recent sync runs for the authenticated user.
        [HttpGet("history")]
        public async Task<IActionResult> History([FromQuery] int limit = 20)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized(new { error = "User not authenticated" });
            }

            var history = await _syncService.GetSyncHistoryAsync(userId, limit);
            return Ok(history);
        }

        // GET: api/email-sync/latest
        // Most recent sync run for the authenticated user.
        [HttpGet("latest")]
        public async Task<IActionResult> Latest()
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized(new { error = "User not authenticated" });
            }

            var latest = await _syncService.GetLatestSyncAsync(userId);
            if (latest == null)
            {
                return Ok(new { message = "No sync runs yet" });
            }

            return Ok(latest);
        }
    }
}
