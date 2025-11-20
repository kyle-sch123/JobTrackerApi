using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace JobTrackerApi.Controllers
{
    public class BaseController : ControllerBase
    {
        // Helper to get the current user's Firebase UID from the request
        protected string? GetUserId()
        {
            var user = HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true)
                return null;

            // prefer common claims: "user_id", "sub", or NameIdentifier
            return user.FindFirst("user_id")?.Value
                ?? user.FindFirst("sub")?.Value
                ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        }

        // Optional: Get user email from Firebase token
        protected string? GetUserEmail()
        {
            return User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;
        }
    }
}
