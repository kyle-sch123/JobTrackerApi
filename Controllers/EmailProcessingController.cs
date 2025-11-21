using JobTrackerApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace JobTrackerApi.Controllers
{
    [ApiController]
    [Route("api/email-processing")]
    public class EmailProcessingController : BaseController
    {
        private readonly EmailProcessingService _processingService;
        private readonly ILogger<EmailProcessingController> _logger;

        public EmailProcessingController(
            EmailProcessingService processingService,
            ILogger<EmailProcessingController> logger)
        {
            _processingService = processingService;
            _logger = logger;
        }

        // POST: api/email-processing/process-pending
        // Process all pending emails for current user
        [HttpPost("process-pending")]
        public async Task<IActionResult> ProcessPending()
        {
            try
            {
                var userId = GetUserId();
                var results = await _processingService.ProcessPendingEmailsAsync(userId);

                var summary = new
                {
                    totalProcessed = results.Count,
                    autoProcessed = results.Count(r => r.Action == "auto_processed"),
                    requiresReview = results.Count(r => r.Action == "requires_review"),
                    lowConfidence = results.Count(r => r.Action == "low_confidence"),
                    applicationsCreated = results.Count(r => !string.IsNullOrEmpty(r.JobApplicationId) && r.Message?.Contains("Created") == true),
                    applicationsUpdated = results.Count(r => !string.IsNullOrEmpty(r.JobApplicationId) && r.Message?.Contains("Updated") == true),
                    results = results
                };

                return Ok(summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process pending emails");
                return StatusCode(500, new { error = "Failed to process emails: " + ex.Message });
            }
        }

        // GET: api/email-processing/review-queue
        // Get emails that need manual review
        [HttpGet("review-queue")]
        public async Task<IActionResult> GetReviewQueue()
        {
            try
            {
                var userId = GetUserId();
                var emails = await _processingService.GetEmailsRequiringReviewAsync(userId);

                return Ok(emails);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get review queue");
                return StatusCode(500, new { error = "Failed to get review queue" });
            }
        }

        // GET: api/email-processing/stats
        // Get processing statistics
        [HttpGet("stats")]
        public async Task<IActionResult> GetStats()
        {
            try
            {
                var userId = GetUserId();
                // This would query your collections for stats
                // Placeholder implementation
                return Ok(new
                {
                    message = "Stats endpoint - to be implemented",
                    userId = userId
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get processing stats");
                return StatusCode(500, new { error = "Failed to get stats" });
            }
        }
    }
}