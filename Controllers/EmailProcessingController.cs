using JobTrackerApi.Models;
using JobTrackerApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace JobTrackerApi.Controllers
{
    [ApiController]
    [Route("api/email-processing")]
    public class EmailProcessingController : BaseController
    {
        private readonly EmailProcessingService _processingService;
        private readonly ClaudeEmailParserService _parserService;
        private readonly ILogger<EmailProcessingController> _logger;

        public EmailProcessingController(
            EmailProcessingService processingService,
            ClaudeEmailParserService parserService,
            ILogger<EmailProcessingController> logger)
        {
            _processingService = processingService;
            _parserService = parserService;
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
                _logger.LogInformation($"Processing pending emails for user {userId}");

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

                _logger.LogInformation(
                    $"Completed processing: {summary.totalProcessed} emails, " +
                    $"{summary.applicationsCreated} created, {summary.applicationsUpdated} updated"
                );

                return Ok(summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process pending emails");
                return StatusCode(500, new { error = "Failed to process emails: " + ex.Message });
            }
        }

        // POST: api/email-processing/test-parse
        // Test the parser with custom email content (useful for debugging)
        [HttpPost("test-parse")]
        public async Task<IActionResult> TestParse([FromBody] TestEmailRequest request)
        {
            try
            {
                var userId = GetUserId();

                _logger.LogInformation($"Testing parser with subject: {request.Subject}");

                var testEmail = new ProcessedEmail
                {
                    Id = "test-" + Guid.NewGuid().ToString(),
                    UserId = userId,
                    GmailMessageId = "test",
                    Subject = request.Subject,
                    From = request.From,
                    FromEmail = request.FromEmail,
                    Date = DateTime.UtcNow,
                    BodyPlainText = request.Body,
                    IsJobRelated = true,
                    ProcessingStatus = "pending"
                };

                var extracted = await _parserService.ParseEmailAsync(testEmail);

                var shouldAutoProcess = _parserService.ShouldAutoProcess(extracted.Confidence);
                var requiresReview = _parserService.RequiresReview(extracted.Confidence);

                return Ok(new
                {
                    input = new
                    {
                        subject = request.Subject,
                        from = request.From,
                        fromEmail = request.FromEmail,
                        bodyPreview = request.Body.Substring(0, Math.Min(200, request.Body.Length)) + (request.Body.Length > 200 ? "..." : "")
                    },
                    extracted = new
                    {
                        companyName = extracted.CompanyName,
                        position = extracted.Position,
                        applicationStatus = extracted.ApplicationStatus,
                        interviewDate = extracted.InterviewDate,
                        recruiterName = extracted.RecruiterName,
                        recruiterEmail = extracted.RecruiterEmail,
                        jobUrl = extracted.JobUrl,
                        salaryRange = extracted.SalaryRange,
                        interviewType = extracted.InterviewType,
                        confidence = extracted.Confidence
                    },
                    processing = new
                    {
                        shouldAutoProcess = shouldAutoProcess,
                        requiresReview = requiresReview,
                        action = shouldAutoProcess ? "Would auto-create/update application" :
                                requiresReview ? "Would flag for manual review" :
                                "Would ignore (confidence too low)"
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to test parse email");
                return StatusCode(500, new { error = "Failed to parse email: " + ex.Message });
            }
        }

        // POST: api/email-processing/test-single/{emailId}
        // Test processing a single email from the database
        [HttpPost("test-single/{emailId}")]
        public async Task<IActionResult> TestSingleEmail(string emailId)
        {
            try
            {
                var userId = GetUserId();

                _logger.LogInformation($"Testing single email {emailId} for user {userId}");

                var email = await _processingService.GetEmailByIdAsync(emailId, userId);
                if (email == null)
                {
                    return NotFound(new { error = "Email not found or does not belong to user" });
                }

                var result = await _processingService.ProcessEmailWithHybridAsync(email);

                return Ok(new
                {
                    email = new
                    {
                        id = email.Id,
                        subject = email.Subject,
                        from = email.From,
                        fromEmail = email.FromEmail,
                        date = email.Date,
                        isJobRelated = email.IsJobRelated,
                        processingStatus = email.ProcessingStatus
                    },
                    processingResult = result,
                    extractedData = email.ExtractedData
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to test single email {emailId}");
                return StatusCode(500, new { error = ex.Message });
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

                return Ok(new
                {
                    count = emails.Count,
                    emails = emails.Select(e => new
                    {
                        id = e.Id,
                        subject = e.Subject,
                        from = e.From,
                        date = e.Date,
                        extractedData = e.ExtractedData,
                        processingStatus = e.ProcessingStatus
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get review queue");
                return StatusCode(500, new { error = "Failed to get review queue" });
            }
        }

        //GET METHOD TO TROUBLESHOOT CLAUDE CALLS.
        [HttpGet("test-claude")]
        public async Task<IActionResult> TestClaude()
        {
            try
            {
                var apiKey = Environment.GetEnvironmentVariable("CLAUDE_API_KEY");
                if (string.IsNullOrEmpty(apiKey))
                {
                    return Ok(new { error = "CLAUDE_API_KEY not found in environment" });
                }

                var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
                httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

                var requestBody = new
                {
                    model = "claude-3-5-haiku-20241022",
                    max_tokens = 100,
                    messages = new[]
                    {
                new
                {
                    role = "user",
                    content = "Say hello!"
                }
            }
                };

                var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync("https://api.anthropic.com/v1/messages", content);
                var responseContent = await response.Content.ReadAsStringAsync();

                return Ok(new
                {
                    status = response.StatusCode.ToString(),
                    apiKeyPresent = !string.IsNullOrEmpty(apiKey),
                    apiKeyPrefix = apiKey.Substring(0, Math.Min(20, apiKey.Length)) + "...",
                    response = responseContent
                });
            }
            catch (Exception ex)
            {
                return Ok(new { error = ex.Message, stackTrace = ex.StackTrace });
            }
        }


        // GET: api/email-processing/stats
        // Get processing statistics for the current user
        [HttpGet("stats")]
        public async Task<IActionResult> GetStats()
        {
            try
            {
                var userId = GetUserId();
                var stats = await _processingService.GetProcessingStatsAsync(userId);

                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get processing stats");
                return StatusCode(500, new { error = "Failed to get stats" });
            }
        }

        // POST: api/email-processing/approve/{emailId}
        // Manually approve an email for processing (for review queue items)
        [HttpPost("approve/{emailId}")]
        public async Task<IActionResult> ApproveEmail(string emailId, [FromBody] ApprovalRequest? request)
        {
            try
            {
                var userId = GetUserId();

                _logger.LogInformation($"Manually approving email {emailId} for user {userId}");

                var email = await _processingService.GetEmailByIdAsync(emailId, userId);
                if (email == null)
                {
                    return NotFound(new { error = "Email not found" });
                }

                // User can override the extracted data if needed
                if (request != null && request.OverrideData != null)
                {
                    email.ExtractedData = request.OverrideData;
                    email.ExtractedData.Confidence = 100; // User approval = 100% confidence
                }

                // Force process with high confidence
                var result = await _processingService.ProcessEmailWithHybridAsync(email, forceProcess: true);

                return Ok(new
                {
                    message = "Email approved and processed",
                    result = result
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to approve email {emailId}");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // POST: api/email-processing/reject/{emailId}
        // Reject an email from processing (mark as ignored)
        [HttpPost("reject/{emailId}")]
        public async Task<IActionResult> RejectEmail(string emailId)
        {
            try
            {
                var userId = GetUserId();

                _logger.LogInformation($"Rejecting email {emailId} for user {userId}");

                var success = await _processingService.MarkEmailAsIgnoredAsync(emailId, userId);

                if (!success)
                {
                    return NotFound(new { error = "Email not found" });
                }

                return Ok(new { message = "Email marked as ignored" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to reject email {emailId}");
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }

    // Request models
    public class TestEmailRequest
    {
        public string Subject { get; set; } = "";
        public string From { get; set; } = "";
        public string FromEmail { get; set; } = "";
        public string Body { get; set; } = "";
    }

    public class ApprovalRequest
    {
        public EmailExtractedData? OverrideData { get; set; }
    }
}