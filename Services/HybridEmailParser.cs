using JobTrackerApi.Models;

namespace JobTrackerApi.Services
{
    /// <summary>
    /// Hybrid parser that uses rule-based extraction first, falls back to LLM only when needed
    /// </summary>
    public class HybridEmailParser
    {
        private readonly RuleBasedEmailParser _ruleParser;
        private readonly ClaudeEmailParserService _llmParser;
        private readonly ConfidenceThresholds _thresholds;
        private readonly ILogger<HybridEmailParser> _logger;

        // Parsing-strategy thresholds: which extraction method to use.
        // (Distinct from the processing thresholds in ConfidenceThresholds,
        // which decide auto-process vs review vs ignore.)
        private const double HIGH_CONFIDENCE_THRESHOLD = 70.0;  // Use rule-based only
        private const double MEDIUM_CONFIDENCE_THRESHOLD = 40.0; // LLM refines specific fields
        // Below 40% = Full LLM parsing

        public HybridEmailParser(
            RuleBasedEmailParser ruleParser,
            ClaudeEmailParserService llmParser,
            ConfidenceThresholds thresholds,
            ILogger<HybridEmailParser> logger)
        {
            _ruleParser = ruleParser;
            _llmParser = llmParser;
            _thresholds = thresholds;
            _logger = logger;
        }

        public async Task<EmailExtractedData> ParseEmailAsync(ProcessedEmail email)
        {
            _logger.LogInformation($"🔄 HYBRID PARSING: {email.Subject}");

            // Step 1: Try rule-based extraction (fast, free, deterministic)
            var signals = _ruleParser.ParseEmail(email);

            // Step 2: Check if rule-based detected this as NOT a job application
            if (!signals.IsJobApplication)
            {
                _logger.LogInformation($"🚫 Rule-based detected non-application email (newsletter/alert/posting)");
                return new EmailExtractedData
                {
                    IsJobApplication = false,
                    Confidence = 0,
                    ExtractionMethod = "rule-based-rejected"
                };
            }

            var strategy = DetermineParsingStrategy(signals);

            _logger.LogInformation(
                $"📊 Rule-based confidence: {signals.OverallConfidence:F1}% → Strategy: {strategy}"
            );

            EmailExtractedData finalResult;

            switch (strategy)
            {
                case ParsingStrategy.RuleBasedOnly:
                    // High confidence - use rule-based result directly
                    finalResult = ConvertSignalsToExtractedData(signals);
                    finalResult.ExtractionMethod = "rule-based";
                    _logger.LogInformation("✅ Using rule-based extraction (high confidence)");
                    break;

                case ParsingStrategy.LLMRefinement:
                    // Medium confidence - run the full structured LLM parse (more
                    // accurate than merging weak rule-based fields with a partial LLM
                    // pass, and it applies the same classification + extraction rules).
                    finalResult = await _llmParser.ParseEmailAsync(email);
                    finalResult.ExtractionMethod = "hybrid-refined";
                    _logger.LogInformation("🔄 Used full LLM parsing (medium confidence)");
                    break;

                case ParsingStrategy.LLMFull:
                    // Low confidence - full LLM parsing
                    finalResult = await _llmParser.ParseEmailAsync(email);
                    finalResult.ExtractionMethod = "llm-full";
                    _logger.LogInformation("🤖 Used full LLM parsing (low confidence)");
                    break;

                default:
                    finalResult = ConvertSignalsToExtractedData(signals);
                    finalResult.ExtractionMethod = "rule-based-fallback";
                    break;
            }

            // Log final result
            _logger.LogInformation(
                $"✅ FINAL: Company='{finalResult.CompanyName}', Position='{finalResult.Position}', " +
                $"Status={finalResult.ApplicationStatus}, Confidence={finalResult.Confidence:F1}%, " +
                $"Method={finalResult.ExtractionMethod}, IsJobApplication={finalResult.IsJobApplication}"
            );

            return finalResult;
        }

        private ParsingStrategy DetermineParsingStrategy(EmailSignals signals)
        {
            // A recognized job-board/ATS template is authoritative: the rule parser
            // already extracted everything that sender's emails contain, so an LLM
            // pass can only spend money rediscovering (or hallucinating) the same
            // fields. This includes PNet-style confirmations where the employer
            // name is genuinely absent — no parser can extract what isn't there.
            if (signals.FromRecognizedTemplate)
            {
                return ParsingStrategy.RuleBasedOnly;
            }

            var missingCoreFields =
                (string.IsNullOrEmpty(signals.CompanyName) && !signals.CompanyKnownAbsent) ||
                string.IsNullOrEmpty(signals.Position);
            if (missingCoreFields)
            {
                return signals.OverallConfidence >= MEDIUM_CONFIDENCE_THRESHOLD
                    ? ParsingStrategy.LLMRefinement
                    : ParsingStrategy.LLMFull;
            }

            if (signals.OverallConfidence >= HIGH_CONFIDENCE_THRESHOLD)
            {
                return ParsingStrategy.RuleBasedOnly;
            }
            else if (signals.OverallConfidence >= MEDIUM_CONFIDENCE_THRESHOLD)
            {
                return ParsingStrategy.LLMRefinement;
            }
            else
            {
                return ParsingStrategy.LLMFull;
            }
        }

        private EmailExtractedData ConvertSignalsToExtractedData(EmailSignals signals)
        {
            return new EmailExtractedData
            {
                CompanyName = signals.CompanyName ?? "Unknown Company",
                Position = signals.Position ?? "Position Not Specified",
                ApplicationStatus = signals.ApplicationStatus ?? "Applied",
                InterviewDate = signals.InterviewDate,
                RecruiterName = signals.RecruiterName,
                RecruiterEmail = signals.RecruiterEmail,
                JobUrl = signals.JobUrl,
                SalaryRange = signals.SalaryRange,
                InterviewType = signals.InterviewType,
                Confidence = signals.OverallConfidence,
                ExtractionMethod = "rule-based",
                IsJobApplication = signals.IsJobApplication,
                SourceJobBoard = signals.SourceJobBoard,
                EmailType = "application_response"
            };
        }

        public bool ShouldAutoProcess(double confidence)
        {
            return confidence >= _thresholds.Auto;
        }

        public bool RequiresReview(double confidence)
        {
            return confidence >= _thresholds.Review && confidence < _thresholds.Auto;
        }
    }
}
