namespace JobTrackerApi.Models
{
    /// <summary>
    /// Confidence thresholds (0-100) that drive the processing decision.
    /// Bound once at startup from AI_CONFIDENCE_THRESHOLD_AUTO / _REVIEW and
    /// injected, so every service shares one source of truth instead of each
    /// re-reading environment variables on every call.
    /// </summary>
    public class ConfidenceThresholds
    {
        /// <summary>At or above this, an application is auto-created/updated.</summary>
        public double Auto { get; set; } = 70.0;

        /// <summary>At or above this (but below Auto), the email is flagged for review.</summary>
        public double Review { get; set; } = 40.0;
    }
}
