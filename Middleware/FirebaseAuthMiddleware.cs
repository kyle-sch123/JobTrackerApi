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
                await _next(context);
                return;
            }

            try
            {
                var token = ExtractToken(context);

                if (string.IsNullOrEmpty(token))
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(new { error = "No authorization token provided" });
                    return;
                }

                // Verify the Firebase ID token
                var decodedToken = await FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(token);
                var uid = decodedToken.Uid;

                // Add user ID to HttpContext for use in controllers
                context.Items["UserId"] = uid;
                context.Items["FirebaseToken"] = decodedToken;

                // Optionally add claims
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.NameIdentifier, uid),
                    new Claim("firebase_uid", uid)
                };

                if (decodedToken.Claims.TryGetValue("email", out var email))
                {
                    claims.Add(new Claim(ClaimTypes.Email, email.ToString() ?? ""));
                }

                var identity = new ClaimsIdentity(claims, "Firebase");
                context.User = new ClaimsPrincipal(identity);

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
            var publicPaths = new[]
            {
                "/",
                "/health",
                "/openapi",
                "/swagger",
                "/hangfire", // Hangfire has its own auth
            };

            return publicPaths.Any(p => path.StartsWith(p));
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