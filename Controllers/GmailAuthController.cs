using JobTrackerApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace JobTrackerApi.Controllers
{
    [ApiController]
    [Route("api/auth/gmail")]
    public class GmailAuthController : BaseController
    {
        private readonly GmailAuthService _authService;
        private readonly ILogger<GmailAuthController> _logger;
        private readonly IConfiguration _configuration;

        public GmailAuthController(
            GmailAuthService authService,
            ILogger<GmailAuthController> logger,
            IConfiguration configuration)
        {
            _authService = authService;
            _logger = logger;
            _configuration = configuration;
        }

        // GET: api/auth/gmail/connect
        // Returns the OAuth URL to redirect user to Google consent screen
        [HttpGet("connect")]
        public IActionResult Connect()
        {
            try
            {
                var userId = GetUserId();
                
                // Generate a state token for CSRF protection
                var state = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
                
                // Store state in a way that can be verified later (you might want to use Redis or similar)
                // For now, we'll just pass it through and verify the userId in callback
                
                var authUrl = _authService.GetAuthorizationUrl(userId, state);
                
                return Ok(new { authUrl, state });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate Gmail auth URL");
                return StatusCode(500, new { error = "Failed to initiate Gmail connection" });
            }
        }

        // GET: api/auth/gmail/callback
        // Google redirects here after user grants permission
        [HttpGet("callback")]
        public async Task<IActionResult> Callback([FromQuery] string code, [FromQuery] string state)
        {
            try
            {
                if (string.IsNullOrEmpty(code))
                {
                    return BadRequest(new { error = "Authorization code is required" });
                }

                // In production, verify the state token here for CSRF protection
                
                // Extract userId from state or require it as a parameter
                // For now, we'll expect it to be passed from the frontend
                var userId = Request.Query["userId"].ToString();
                
                if (string.IsNullOrEmpty(userId))
                {
                    return BadRequest(new { error = "User ID is required" });
                }

                var connection = await _authService.ExchangeCodeForTokensAsync(code, userId);

                // Get frontend URL for redirect
                var isDevelopment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";
                var frontendUrl = isDevelopment
                    ? Environment.GetEnvironmentVariable("FRONTEND_URL_DEV")
                    : Environment.GetEnvironmentVariable("FRONTEND_URL_PROD");

                // Redirect back to frontend with success
                return Redirect($"{frontendUrl}/settings?gmail=connected");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle Gmail OAuth callback");
                
                var isDevelopment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";
                var frontendUrl = isDevelopment
                    ? Environment.GetEnvironmentVariable("FRONTEND_URL_DEV")
                    : Environment.GetEnvironmentVariable("FRONTEND_URL_PROD");

                return Redirect($"{frontendUrl}/settings?gmail=error");
            }
        }

        // GET: api/auth/gmail/status
        // Check if user has Gmail connected
        [HttpGet("status")]
        public async Task<IActionResult> GetStatus()
        {
            try
            {
                var userId = GetUserId();
                var connection = await _authService.GetConnectionAsync(userId);

                if (connection == null)
                {
                    return Ok(new { connected = false });
                }

                return Ok(new
                {
                    connected = true,
                    email = connection.Email,
                    connectedAt = connection.ConnectedAt,
                    lastSyncAt = connection.LastSyncAt,
                    lastSyncStatus = connection.LastSyncStatus
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get Gmail connection status");
                return StatusCode(500, new { error = "Failed to get connection status" });
            }
        }

        // POST: api/auth/gmail/disconnect
        // Revoke Gmail access
        [HttpPost("disconnect")]
        public async Task<IActionResult> Disconnect()
        {
            try
            {
                var userId = GetUserId();
                var success = await _authService.DisconnectAsync(userId);

                if (!success)
                {
                    return NotFound(new { error = "No active Gmail connection found" });
                }

                return Ok(new { message = "Gmail disconnected successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to disconnect Gmail");
                return StatusCode(500, new { error = "Failed to disconnect Gmail" });
            }
        }
    }
}