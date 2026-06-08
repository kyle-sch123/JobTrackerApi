using FirebaseAdmin.Auth;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace JobTrackerApi.Middleware
{
    public class FirebaseAuthMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<FirebaseAuthMiddleware> _logger;

        public FirebaseAuthMiddleware(RequestDelegate next, ILogger<FirebaseAuthMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Skip authentication for certain paths
            var path = context.Request.Path.Value?.ToLower() ?? "";
            if (ShouldSkipAuth(path))
            {
                _logger.LogDebug("Skipping Firebase auth for path: {Path}", path);
                await _next(context);
                return;
            }

            try
            {
                var token = ExtractToken(context);

                _logger.LogDebug("Authorization header present: {HasHeader}", !string.IsNullOrEmpty(context.Request.Headers["Authorization"]));
                _logger.LogDebug("Token extracted: {HasToken}", !string.IsNullOrEmpty(token));

                // Normalize token: remove whitespace/newlines that often get introduced when copying tokens
                if (!string.IsNullOrEmpty(token))
                {
                    var rawToken = token;
                    var cleanedToken = token.Replace("\r", "").Replace("\n", "").Replace(" ", "");
                    if (cleanedToken != rawToken)
                    {
                        _logger.LogDebug("Cleaned token whitespace (was malformed by copy/paste). New length={Length}", cleanedToken.Length);
                        token = cleanedToken;
                    }

                    var dotCount = token.Count(c => c == '.');
                    var tokenLen = token.Length;
                    var hasWhitespace = token.Any(char.IsWhiteSpace);
                    var prefix = tokenLen > 8 ? token.Substring(0, 8) : token;
                    var suffix = tokenLen > 8 ? token.Substring(tokenLen - 8, 8) : token;

                    _logger.LogDebug("Token diagnostics: Length={Length}, Dots={DotCount}, HasWhitespace={HasWhitespace}, Prefix={Prefix}..., Suffix=...{Suffix}",
                        tokenLen, dotCount, hasWhitespace, prefix, suffix);

                    if (dotCount != 2)
                    {
                        _logger.LogWarning("Token appears malformed (unexpected number of '.' segments). Rejecting request.");
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsJsonAsync(new { error = "Invalid token format" });
                        return;
                    }
                }

                if (string.IsNullOrEmpty(token))
                {
                    _logger.LogWarning("No authorization token provided for request {Method} {Path}", context.Request.Method, context.Request.Path);
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(new { error = "No authorization token provided" });
                    return;
                }

                // Verify the Firebase ID token
                FirebaseToken decodedToken;
                try
                {
                    decodedToken = await FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(token);
                }
                catch (FormatException fex)
                {
                    _logger.LogWarning(fex, "Token format error during verification");
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(new { error = "Invalid token format" });
                    return;
                }
                catch (FirebaseAuthException faex)
                {
                    _logger.LogWarning(faex, "Firebase authentication failed during verification");
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(new { error = "Invalid or expired token" });
                    return;
                }
                var uid = decodedToken.Uid;

                _logger.LogDebug("Firebase token verified for uid: {Uid}", uid);
                _logger.LogDebug("Decoded token claims count: {Count}", decodedToken.Claims?.Count ?? 0);

                // Add user ID to HttpContext for use in controllers
                context.Items["UserId"] = uid;
                context.Items["FirebaseToken"] = decodedToken;

                // Optionally add claims
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.NameIdentifier, uid),
                    new Claim("firebase_uid", uid)
                };

                if (decodedToken.Claims != null && decodedToken.Claims.TryGetValue("email", out var email))
                {
                    claims.Add(new Claim(ClaimTypes.Email, email?.ToString() ?? ""));
                }

                var identity = new ClaimsIdentity(claims, "Firebase");
                context.User = new ClaimsPrincipal(identity);

                _logger.LogDebug("HttpContext.User populated. IsAuthenticated={IsAuthenticated}, NameIdentifier={NameId}",
                    context.User?.Identity?.IsAuthenticated, context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value);

                await _next(context);
            }
            catch (FirebaseAuthException ex)
            {
                _logger.LogWarning(ex, "Firebase authentication failed");
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "Invalid or expired token" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Authentication error");
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new { error = "Authentication failed" });
            }
        }

        private string? ExtractToken(HttpContext context)
        {
            var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
            if (authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
            {
                return authHeader.Substring("Bearer ".Length).Trim();
            }
            return null;
        }

        private bool ShouldSkipAuth(string path)
        {
            // Paths that don't require authentication
            // Keep root (/) as exact-match only; other entries are prefix matches.
            if (path == "/") return true;

            var publicPrefixPaths = new[]
            {
                "/health",
                "/openapi",
                "/swagger",
                "/hangfire", // Hangfire has its own auth
                "/api/auth/gmail/callback" // Google redirects here without Authorization header
            };

            return publicPrefixPaths.Any(p => path.StartsWith(p));
        }
    }

    // Extension method to easily add middleware
    public static class FirebaseAuthMiddlewareExtensions
    {
        public static IApplicationBuilder UseFirebaseAuth(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<FirebaseAuthMiddleware>();
        }
    }
}