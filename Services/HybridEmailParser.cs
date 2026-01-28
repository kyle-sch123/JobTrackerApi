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
        private readonly ILogger<HybridEmailParser> _logger;

        // Confidence thresholds
        private const double HIGH_CONFIDENCE_THRESHOLD = 70.0;  // Use rule-based only
        private const double MEDIUM_CONFIDENCE_THRESHOLD = 40.0; // LLM refines specific fields
        // Below 40% = Full LLM parsing

        public HybridEmailParser(
            RuleBasedEmailParser ruleParser,
            ClaudeEmailParserService llmParser,
            ILogger<HybridEmailParser> logger)
        {
            _ruleParser = ruleParser;
            _llmParser = llmParser;
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
                    // Medium confidence - let LLM refine weak fields only
                    finalResult = await RefineWithLLMAsync(email, signals);
                    finalResult.ExtractionMethod = "hybrid-refined";
                    _logger.LogInformation("🔄 Used LLM to refine specific fields (medium confidence)");
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
            var missingCoreFields = string.IsNullOrEmpty(signals.CompanyName) || string.IsNullOrEmpty(signals.Position);
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

        private async Task<EmailExtractedData> RefineWithLLMAsync(ProcessedEmail email, EmailSignals signals)
        {
            // Identify which fields need LLM refinement
            var needsRefinement = new List<string>();

            if (signals.PositionConfidence < 60) needsRefinement.Add("position");
            if (signals.CompanyConfidence < 60) needsRefinement.Add("company");
            if (signals.StatusConfidence < 60) needsRefinement.Add("status");

            if (needsRefinement.Count == 0)
            {
                _logger.LogInformation("No fields need refinement; using rule-based result.");
                return ConvertSignalsToExtractedData(signals);
            }

            _logger.LogInformation($"🔍 LLM refining fields: {string.Join(", ", needsRefinement)}");

            // Build targeted prompt for LLM to fill gaps
            var refinementPrompt = BuildRefinementPrompt(email, signals, needsRefinement);

            try
            {
                // Call LLM with focused prompt
                var llmResult = await _llmParser.ParseEmailWithPromptAsync(email, refinementPrompt);

                // Merge: Keep high-confidence rule-based fields, use LLM for weak ones
                var merged = MergeResults(signals, llmResult);

                return merged;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LLM refinement failed, using rule-based only");
                return ConvertSignalsToExtractedData(signals);
            }
        }

        private string BuildRefinementPrompt(ProcessedEmail email, EmailSignals signals, List<string> needsRefinement)
        {
            var body = email.BodyPlainText ?? email.BodyHtml ?? email.Snippet ?? "";
            if (body.Length > 3000) body = body.Substring(0, 3000);

            var refinementInstructions = new List<string>();
            if (needsRefinement.Contains("position"))
                refinementInstructions.Add("- Extract the exact job position/title");
            if (needsRefinement.Contains("company"))
                refinementInstructions.Add("- Extract the company name from signature or context");
            if (needsRefinement.Contains("status"))
                refinementInstructions.Add("- Determine the application status (Applied/Rejected/Interview/Offer)");

            return $@"The rule-based parser extracted some data but needs help with these fields:
{string.Join("\n", refinementInstructions)}

We already have (if confidence was high enough):
- Company: {signals.CompanyName ?? "unknown"} (confidence: {signals.CompanyConfidence:F0}%)
- Position: {signals.Position ?? "unknown"} (confidence: {signals.PositionConfidence:F0}%)
- Status: {signals.ApplicationStatus ?? "unknown"} (confidence: {signals.StatusConfidence:F0}%)

Email:
Subject: {email.Subject}
From: {email.From}
Body: {body}

Return JSON with ONLY the fields that need refinement. If a field already has high confidence, don't include it.
{{
  ""companyName"": ""string or null"",
  ""position"": ""string or null"",
  ""applicationStatus"": ""string or null"",
  ""confidence"": 0-100
}}";
        }

        private EmailExtractedData MergeResults(EmailSignals ruleBasedSignals, EmailExtractedData llmResult)
        {
            // Priority: Keep high-confidence rule-based, use LLM for low-confidence
            var merged = new EmailExtractedData();

            // Company: Use rule-based if confidence > 60%, otherwise LLM
            if (ruleBasedSignals.CompanyConfidence >= 60)
            {
                merged.CompanyName = ruleBasedSignals.CompanyName;
                merged.Confidence += ruleBasedSignals.CompanyConfidence * 0.30; // 30% weight
            }
            else if (!string.IsNullOrEmpty(llmResult.CompanyName))
            {
                merged.CompanyName = llmResult.CompanyName;
                merged.Confidence += llmResult.Confidence * 0.30;
            }
            else
            {
                merged.CompanyName = ruleBasedSignals.CompanyName ?? "Unknown Company";
                merged.Confidence += 30; // Low confidence fallback
            }

            // Position: Same logic
            if (ruleBasedSignals.PositionConfidence >= 60)
            {
                merged.Position = ruleBasedSignals.Position;
                merged.Confidence += ruleBasedSignals.PositionConfidence * 0.35; // 35% weight
            }
            else if (!string.IsNullOrEmpty(llmResult.Position))
            {
                merged.Position = llmResult.Position;
                merged.Confidence += llmResult.Confidence * 0.35;
            }
            else
            {
                merged.Position = ruleBasedSignals.Position ?? "Position Not Specified";
                merged.Confidence += 30;
            }

            // Status: Same logic
            if (ruleBasedSignals.StatusConfidence >= 60)
            {
                merged.ApplicationStatus = ruleBasedSignals.ApplicationStatus;
                merged.Confidence += ruleBasedSignals.StatusConfidence * 0.25; // 25% weight
            }
            else if (!string.IsNullOrEmpty(llmResult.ApplicationStatus))
            {
                merged.ApplicationStatus = llmResult.ApplicationStatus;
                merged.Confidence += llmResult.Confidence * 0.25;
            }
            else
            {
                merged.ApplicationStatus = ruleBasedSignals.ApplicationStatus ?? "Applied";
                merged.Confidence += 40;
            }

            // Always use rule-based for these (high accuracy from patterns)
            merged.InterviewDate = ruleBasedSignals.InterviewDate;
            merged.RecruiterName = ruleBasedSignals.RecruiterName;
            merged.RecruiterEmail = ruleBasedSignals.RecruiterEmail;
            merged.JobUrl = ruleBasedSignals.JobUrl;
            merged.SalaryRange = ruleBasedSignals.SalaryRange;
            merged.InterviewType = ruleBasedSignals.InterviewType;

            // Add small bonus for recruiter info
            if (!string.IsNullOrEmpty(merged.RecruiterName))
                merged.Confidence += 5;
            if (merged.InterviewDate.HasValue)
                merged.Confidence += 5;

            // Clamp to 0-100
            merged.Confidence = Math.Clamp(merged.Confidence, 0, 100);

            // Preserve IsJobApplication from LLM result (LLM has better judgment on this)
            merged.IsJobApplication = llmResult.IsJobApplication;

            return merged;
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
                IsJobApplication = signals.IsJobApplication
            };
        }

        public bool ShouldAutoProcess(double confidence)
        {
            var threshold = double.Parse(
                Environment.GetEnvironmentVariable("AI_CONFIDENCE_THRESHOLD_AUTO") ?? "70"
            );
            return confidence >= threshold;
        }

        public bool RequiresReview(double confidence)
        {
            var autoThreshold = double.Parse(
                Environment.GetEnvironmentVariable("AI_CONFIDENCE_THRESHOLD_AUTO") ?? "70"
            );
            var reviewThreshold = double.Parse(
                Environment.GetEnvironmentVariable("AI_CONFIDENCE_THRESHOLD_REVIEW") ?? "40"
            );

            return confidence >= reviewThreshold && confidence < autoThreshold;
        }
    }
}
