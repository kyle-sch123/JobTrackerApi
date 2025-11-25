namespace JobTrackerApi.Models
{
    /// <summary>
    /// Detected signals from rule-based parsing with confidence scores
    /// </summary>
    public class EmailSignals
    {
        // Extracted data
        public string? CompanyName { get; set; }
        public string? Position { get; set; }
        public string? ApplicationStatus { get; set; }
        public DateTime? InterviewDate { get; set; }
        public string? RecruiterName { get; set; }
        public string? RecruiterEmail { get; set; }
        public string? JobUrl { get; set; }
        public string? SalaryRange { get; set; }
        public string? InterviewType { get; set; }

        // Individual signal confidences (0-100)
        public double CompanyConfidence { get; set; }
        public double PositionConfidence { get; set; }
        public double StatusConfidence { get; set; }
        public double InterviewDateConfidence { get; set; }
        public double RecruiterConfidence { get; set; }

        // Overall confidence (weighted average)
        public double OverallConfidence { get; set; }

        // Detected signals (for transparency)
        public List<DetectedSignal> Signals { get; set; } = new();

        // Source of extraction
        public string ExtractionMethod { get; set; } = "rule-based"; // "rule-based", "llm-refined", "llm-full"
    }

    public class DetectedSignal
    {
        public string SignalType { get; set; } = null!; // "subject_pattern", "domain", "keyword", etc.
        public string Field { get; set; } = null!; // "company", "position", "status"
        public string Value { get; set; } = null!;
        public double Confidence { get; set; }
        public string Source { get; set; } = null!; // "subject", "body", "headers"
        public string? Pattern { get; set; } // Regex pattern used
    }

    public enum ParsingStrategy
    {
        RuleBasedOnly,      // High confidence, use rules
        LLMRefinement,      // Medium confidence, let LLM refine specific fields
        LLMFull             // Low confidence, full LLM parsing
    }
}