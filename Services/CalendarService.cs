using Google.Apis.Calendar.v3.Data;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using JobTrackerApi.Models;
// Alias to avoid a name clash with this file's own CalendarService class.
using GoogleCalendarService = Google.Apis.Calendar.v3.CalendarService;

namespace JobTrackerApi.Services
{
    // One-way sync (app -> Google Calendar) for scheduled interviews. Every call
    // checks the user's granted scopes first and no-ops if Calendar access hasn't
    // been granted yet, so scheduling an interview always succeeds locally even
    // for users who haven't (re)connected Calendar.
    public class CalendarService
    {
        private const string CalendarId = "primary";

        private readonly GmailAuthService _authService;
        private readonly ILogger<CalendarService> _logger;

        public CalendarService(GmailAuthService authService, ILogger<CalendarService> logger)
        {
            _authService = authService;
            _logger = logger;
        }

        // Creates the interview's calendar event if none exists yet, or updates it
        // in place if `app.CalendarEventId` is already set. Returns the resulting
        // event id, or null if the user hasn't granted Calendar access (or has no
        // interview date to sync).
        public async Task<string?> SyncInterviewEventAsync(string userId, JobApplication app)
        {
            if (app.InterviewDate == null)
            {
                return null;
            }

            var connection = await _authService.GetConnectionAsync(userId);
            if (connection == null || !connection.Scopes.Contains(GmailAuthService.CalendarEventsScope))
            {
                return null;
            }

            try
            {
                var accessToken = await _authService.RefreshAccessTokenAsync(userId);
                var service = CreateCalendarService(accessToken);

                var start = DateTime.SpecifyKind(app.InterviewDate.Value, DateTimeKind.Utc);
                var calendarEvent = new Event
                {
                    Summary = $"Interview: {app.company} — {app.jobTitle}",
                    Location = app.InterviewLocation,
                    Description = BuildDescription(app),
                    Start = new EventDateTime { DateTimeDateTimeOffset = start, TimeZone = "UTC" },
                    End = new EventDateTime { DateTimeDateTimeOffset = start.AddHours(1), TimeZone = "UTC" }
                };

                if (!string.IsNullOrEmpty(app.CalendarEventId))
                {
                    try
                    {
                        var updated = await service.Events
                            .Update(calendarEvent, CalendarId, app.CalendarEventId)
                            .ExecuteAsync();
                        return updated.Id;
                    }
                    catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound
                        || ex.HttpStatusCode == System.Net.HttpStatusCode.Gone)
                    {
                        // The event was removed on Google's side — fall through and recreate it.
                    }
                }

                var created = await service.Events.Insert(calendarEvent, CalendarId).ExecuteAsync();
                return created.Id;
            }
            catch (Exception ex)
            {
                // Calendar sync is best-effort: the interview date itself always
                // saves regardless, so log and let the caller keep the prior event id.
                _logger.LogWarning(ex, "Failed to sync interview calendar event for user {UserId}", userId);
                return app.CalendarEventId;
            }
        }

        public async Task DeleteInterviewEventAsync(string userId, string eventId)
        {
            if (string.IsNullOrEmpty(eventId))
            {
                return;
            }

            var connection = await _authService.GetConnectionAsync(userId);
            if (connection == null || !connection.Scopes.Contains(GmailAuthService.CalendarEventsScope))
            {
                return;
            }

            try
            {
                var accessToken = await _authService.RefreshAccessTokenAsync(userId);
                var service = CreateCalendarService(accessToken);
                await service.Events.Delete(CalendarId, eventId).ExecuteAsync();
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound
                || ex.HttpStatusCode == System.Net.HttpStatusCode.Gone)
            {
                // Already gone — nothing to do.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete interview calendar event for user {UserId}", userId);
            }
        }

        private static string BuildDescription(JobApplication app)
        {
            var lines = new List<string> { $"{app.jobTitle} at {app.company}" };

            if (!string.IsNullOrWhiteSpace(app.RecruiterName))
            {
                lines.Add($"Recruiter: {app.RecruiterName}");
            }
            if (!string.IsNullOrWhiteSpace(app.RecruiterEmail))
            {
                lines.Add($"Recruiter email: {app.RecruiterEmail}");
            }
            if (!string.IsNullOrWhiteSpace(app.notes))
            {
                lines.Add($"Notes: {app.notes}");
            }

            lines.Add("Synced from Job Trackr.");
            return string.Join("\n", lines);
        }

        private static GoogleCalendarService CreateCalendarService(string accessToken)
        {
            var credential = GoogleCredential.FromAccessToken(accessToken);

            return new GoogleCalendarService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "JobTracker"
            });
        }
    }
}
