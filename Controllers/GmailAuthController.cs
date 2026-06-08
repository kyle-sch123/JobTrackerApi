using JobTrackerApi.Services;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace JobTrackerApi.Controllers
{
    [ApiController]
    [Route("api/auth/gmail")]
    public class GmailAuthController : BaseController
    {
        private const string StateCachePrefix = "gmail_oauth_state:";

        private readonly GmailAuthService _authService;
        private readonly ILogger<GmailAuthController> _logger;
        private readonly IConfiguration _configuration;
        private readonly IMemoryCache _cache;

        public GmailAuthController(
            GmailAuthService authService,
            ILogger<GmailAuthController> logger,
            IConfiguration configuration,
            IMemoryCache cache)
        {
            _authService = authService;
            _logger = logger;
            _configuration = configuration;
            _cache = cache;
        }

        // GET: api/auth/gmail/connect
        // Returns the OAuth URL to redirect user to Google consent screen
        [HttpGet("connect")]
        public IActionResult Connect()
        {
            try
            {
                var userId = GetUserId();
                if (string.IsNullOrEmpty(userId))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }
                
                // Generate a state token for CSRF protection and stash state→userId
                // so the callback can verify the round-trip (10-minute window).
                var state = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
                _cache.Set(StateCachePrefix + state, userId, TimeSpan.FromMinutes(10));

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

                // state is Base64(JSON { userId, state }) as produced by GetAuthorizationUrl.
                // Decode both the embedded userId and the inner CSRF state token.
                string? decodedUserId = null;
                string? innerState = null;
                try
                {
                    var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(state ?? ""));
                    var doc = JsonSerializer.Deserialize<JsonElement>(decoded);
                    if (doc.ValueKind == JsonValueKind.Object)
                    {
                        if (doc.TryGetProperty("userId", out var uidProp)) decodedUserId = uidProp.GetString();
                        if (doc.TryGetProperty("state", out var stateProp)) innerState = stateProp.GetString();
                    }
                }
                catch
                {
                    // ignore decoding errors and fall through to validation
                }

                // Prefer userId from query (frontend may pass it), else from state.
                var userId = Request.Query["userId"].ToString();
                if (string.IsNullOrEmpty(userId))
                {
                    userId = decodedUserId;
                }

                if (string.IsNullOrEmpty(userId))
                {
                    return BadRequest(new { error = "User ID is required" });
                }

                // CSRF: the inner state must match the value we stashed during Connect
                // and be bound to this same user.
                if (string.IsNullOrEmpty(innerState) ||
                    !_cache.TryGetValue(StateCachePrefix + innerState, out string? expectedUserId) ||
                    expectedUserId != userId)
                {
                    _logger.LogWarning("OAuth state verification failed for user {UserId}", userId);

                    var feUrl = GetFrontendUrl();
                    return Redirect($"{feUrl}/settings?gmail=error");
                }

                _cache.Remove(StateCachePrefix + innerState);

                var connection = await _authService.ExchangeCodeForTokensAsync(code, userId);

                // Redirect back to frontend with success
                return Redirect($"{GetFrontendUrl()}/settings?gmail=connected");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle Gmail OAuth callback");
                return Redirect($"{GetFrontendUrl()}/settings?gmail=error");
            }
        }

        private static string? GetFrontendUrl()
        {
            var isDevelopment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";
            return isDevelopment
                ? Environment.GetEnvironmentVariable("FRONTEND_URL_DEV")
                : Environment.GetEnvironmentVariable("FRONTEND_URL_PROD");
        }

        // GET: api/auth/gmail/status
        // Check if user has Gmail connected
        [HttpGet("status")]
        public async Task<IActionResult> GetStatus()
        {
            try
            {
                var userId = GetUserId();
                if (string.IsNullOrEmpty(userId))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

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
                if (string.IsNullOrEmpty(userId))
                {
                    return Unauthorized(new { error = "User not authenticated" });
                }

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