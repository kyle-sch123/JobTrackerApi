namespace JobTrackerApi.Models
{
    /// <summary>
    /// Canonical job-application status values used across the AI pipeline
    /// (parser, matcher, processing service). One source of truth so the
    /// status set and its promotion ordering can't drift between services.
    /// </summary>
    public static class ApplicationStatuses
    {
        public const string Applied = "Applied";
        public const string InProgress = "In Progress";
        public const string InterviewScheduled = "Interview Scheduled";
        public const string Offer = "Offer";
        public const string Rejected = "Rejected";
        public const string Accepted = "Accepted";
        public const string Declined = "Declined";

        /// <summary>All statuses the AI pipeline may assign.</summary>
        public static readonly string[] All =
        {
            Applied, InProgress, InterviewScheduled, Offer, Rejected, Accepted, Declined
        };

        /// <summary>
        /// Promotion ordering. An incoming status only overwrites the current
        /// one if it ranks higher (Rejected is always allowed to win).
        /// </summary>
        public static readonly IReadOnlyDictionary<string, int> Hierarchy =
            new Dictionary<string, int>
            {
                { Applied, 1 },
                { InProgress, 2 },
                { InterviewScheduled, 3 },
                { Offer, 4 },
                { Rejected, 5 },
                { Accepted, 6 },
                { Declined, 6 }
            };
    }
}
