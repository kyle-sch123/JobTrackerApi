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
        private readonly HybridEmailParser _hybridParser;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IWebHostEnvironment _environment;
        private readonly ILogger<EmailProcessingController> _logger;

        public EmailProcessingController(
            EmailProcessingService processingService,
            HybridEmailParser hybridParser,
            IHttpClientFactory httpClientFactory,
            IWebHostEnvironment environment,
            ILogger<EmailProcessingController> logger)
        {
            _processingService = processingService;
            _hybridParser = hybridParser;
            _httpClientFactory = httpClientFactory;
            _environment = environment;
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

                // Use the SAME hybrid parser the real pipeline uses, so the reported
                // decision matches what processing would actually do.
                var extracted = await _hybridParser.ParseEmailAsync(testEmail);

                var shouldAutoProcess = _hybridParser.ShouldAutoProcess(extracted.Confidence);
                var requiresReview = _hybridParser.RequiresReview(extracted.Confidence);

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
            // Diagnostic endpoint only — never expose in production.
            if (!_environment.IsDevelopment())
            {
                return NotFound();
            }

            try
            {
                var apiKey = Environment.GetEnvironmentVariable("CLAUDE_API_KEY");
                if (string.IsNullOrEmpty(apiKey))
                {
                    return Ok(new { error = "CLAUDE_API_KEY not found in environment" });
                }

                // Reuse a pooled HttpClient from the factory (avoids socket exhaustion).
                var httpClient = _httpClientFactory.CreateClient();
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
                    response = responseContent
                });
            }
            catch (Exception ex)
            {
                return Ok(new { error = ex.Message });
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

                // Approve from the stored/overridden extraction (no re-parse).
                var result = await _processingService.ApproveEmailAsync(email);

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

        // POST: api/email-processing/reprocess/{emailId}
        // Force reprocess an email that was incorrectly skipped
        [HttpPost("reprocess/{emailId}")]
        public async Task<IActionResult> ReprocessEmail(string emailId)
        {
            try
            {
                var userId = GetUserId();

                _logger.LogInformation($"Force reprocessing email {emailId} for user {userId}");

                var email = await _processingService.GetEmailByIdAsync(emailId, userId);
                if (email == null)
                {
                    return NotFound(new { error = "Email not found or does not belong to user" });
                }

                // Force process bypasses the job-related filter
                var result = await _processingService.ProcessEmailWithHybridAsync(email, forceProcess: true);

                return Ok(new
                {
                    message = "Email reprocessed",
                    email = new
                    {
                        id = email.Id,
                        subject = email.Subject,
                        from = email.From,
                        previousStatus = email.ProcessingStatus
                    },
                    result
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to reprocess email {emailId}");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // POST: api/email-processing/reprocess-by-gmail-id/{gmailMessageId}
        // Force reprocess an email by Gmail message ID
        [HttpPost("reprocess-by-gmail-id/{gmailMessageId}")]
        public async Task<IActionResult> ReprocessEmailByGmailId(string gmailMessageId)
        {
            try
            {
                var userId = GetUserId();

                _logger.LogInformation($"Force reprocessing email by Gmail ID {gmailMessageId} for user {userId}");

                var email = await _processingService.GetEmailByGmailIdAsync(gmailMessageId, userId);
                if (email == null)
                {
                    return NotFound(new { error = "Email not found or does not belong to user" });
                }

                // Force process bypasses the job-related filter
                var result = await _processingService.ProcessEmailWithHybridAsync(email, forceProcess: true);

                return Ok(new
                {
                    message = "Email reprocessed",
                    email = new
                    {
                        id = email.Id,
                        gmailMessageId = email.GmailMessageId,
                        subject = email.Subject,
                        from = email.From,
                        previousStatus = email.ProcessingStatus
                    },
                    result
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to reprocess email by Gmail ID {gmailMessageId}");
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