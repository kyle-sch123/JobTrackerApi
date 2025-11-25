using System.Text.RegularExpressions;
using JobTrackerApi.Models;

namespace JobTrackerApi.Services
{
    /// <summary>
    /// Fast, deterministic rule-based email parser
    /// Extracts structured data using regex patterns and keyword matching
    /// </summary>
    public class RuleBasedEmailParser
    {
        private readonly ILogger<RuleBasedEmailParser> _logger;

        // Known job board domains with confidence weights
        private static readonly Dictionary<string, double> JobBoardDomains = new()
        {
            { "linkedin.com", 0.9 },
            { "indeed.com", 0.9 },
            { "glassdoor.com", 0.9 },
            { "pnet.co.za", 0.9 },
            { "bamboohr.com", 0.8 },
            { "workday.com", 0.8 },
            { "greenhouse.io", 0.8 },
            { "lever.co", 0.8 },
            { "broadbean.net", 0.8 }
        };

        // Known company domains
        private static readonly Dictionary<string, string> KnownCompanyDomains = new()
        {
            { "google.com", "Google" },
            { "microsoft.com", "Microsoft" },
            { "amazon.com", "Amazon" },
            { "meta.com", "Meta" },
            { "netflix.com", "Netflix" },
            { "apple.com", "Apple" },
            // Add more as needed
        };

        public RuleBasedEmailParser(ILogger<RuleBasedEmailParser> logger)
        {
            _logger = logger;
        }

        public EmailSignals ParseEmail(ProcessedEmail email)
        {
            var signals = new EmailSignals();
            var content = $"{email.Subject}\n{email.BodyPlainText ?? email.BodyHtml ?? email.Snippet}";

            _logger.LogInformation($"🔍 Rule-based parsing: {email.Subject}");

            // Run all detectors
            DetectPosition(email, signals);
            DetectCompany(email, signals);
            DetectStatus(email, signals);
            DetectInterviewDate(email, signals);
            DetectRecruiter(email, signals);
            DetectJobUrl(email, signals);
            DetectSalary(email, signals);

            // Calculate overall confidence
            CalculateOverallConfidence(signals);

            _logger.LogInformation(
                $"✅ Rule-based: Company={signals.CompanyName} ({signals.CompanyConfidence:F0}%), " +
                $"Position={signals.Position} ({signals.PositionConfidence:F0}%), " +
                $"Status={signals.ApplicationStatus} ({signals.StatusConfidence:F0}%), " +
                $"Overall={signals.OverallConfidence:F0}%"
            );

            return signals;
        }

        #region Position Detection

        private void DetectPosition(ProcessedEmail email, EmailSignals signals)
        {
            var patterns = new[]
            {
                // High confidence patterns (95%)
                new { Pattern = @"\|\s*([^|]+?)\s*\|(?:\s*JO-)", Confidence = 95.0, Source = "subject" },
                new { Pattern = @"application for\s+(?:the\s+)?([^.!,\n]+?)(?:\s+has been|\s+position)", Confidence = 90.0, Source = "body" },
                
                // Medium confidence patterns (75%)
                new { Pattern = @"applied (?:to|for)\s+(?:the\s+)?([^.!,\n]+?)(?:\s+role|\s+at)", Confidence = 75.0, Source = "body" },
                new { Pattern = @"your\s+(?:application|submission)\s+for\s+(?:the\s+)?([^.!,\n]+)", Confidence = 75.0, Source = "body" },
                
                // Job title patterns (80%)
                new { Pattern = @"((?:Senior|Junior|Lead|Principal|Staff)?\s*(?:Software|Frontend|Backend|Full Stack|Web|Mobile|Data|DevOps|Cloud)\s+(?:Engineer|Developer|Architect|Designer|Analyst))", Confidence = 80.0, Source = "content" },
                
                // Lower confidence patterns (60%)
                new { Pattern = @"position[:\s]+([A-Z][^.!,\n]{5,50})", Confidence = 60.0, Source = "body" },
                new { Pattern = @"role[:\s]+([A-Z][^.!,\n]{5,50})", Confidence = 60.0, Source = "body" }
            };

            foreach (var patternInfo in patterns)
            {
                var searchText = patternInfo.Source == "subject" ? email.Subject :
                                patternInfo.Source == "body" ? (email.BodyPlainText ?? "") :
                                $"{email.Subject}\n{email.BodyPlainText ?? email.BodyHtml ?? email.Snippet}";

                var match = Regex.Match(searchText, patternInfo.Pattern, RegexOptions.IgnoreCase);
                if (match.Success && match.Groups[1].Success)
                {
                    var position = match.Groups[1].Value.Trim();
                    position = CleanPosition(position);

                    if (IsValidPosition(position))
                    {
                        signals.Position = position;
                        signals.PositionConfidence = patternInfo.Confidence;
                        signals.Signals.Add(new DetectedSignal
                        {
                            SignalType = "position_pattern",
                            Field = "position",
                            Value = position,
                            Confidence = patternInfo.Confidence,
                            Source = patternInfo.Source,
                            Pattern = patternInfo.Pattern
                        });

                        _logger.LogDebug($"  ✓ Position: '{position}' (confidence: {patternInfo.Confidence}%)");
                        return; // Take first high-confidence match
                    }
                }
            }

            // No pattern matched
            signals.Position = null;
            signals.PositionConfidence = 0;
        }

        private bool IsValidPosition(string position)
        {
            return position.Length >= 5 &&
                   position.Length <= 100 &&
                   !position.StartsWith("JO-") &&
                   !Regex.IsMatch(position.ToLower(), @"^(the|this|that|requirements|experience|skills)$");
        }

        private string CleanPosition(string position)
        {
            // Remove trailing noise
            position = Regex.Replace(position, @"\s+(at|with|for|in)\s+\w+.*$", "", RegexOptions.IgnoreCase);
            position = Regex.Replace(position, @"\s+position\s*$", "", RegexOptions.IgnoreCase);
            position = Regex.Replace(position, @"\s+role\s*$", "", RegexOptions.IgnoreCase);
            return position.Trim();
        }

        #endregion

        #region Company Detection

        private void DetectCompany(ProcessedEmail email, EmailSignals signals)
        {
            // Strategy 1: Known company domain (95% confidence)
            var domain = email.FromEmail.Split('@').LastOrDefault()?.ToLower() ?? "";
            foreach (var (knownDomain, companyName) in KnownCompanyDomains)
            {
                if (domain.Contains(knownDomain))
                {
                    signals.CompanyName = companyName;
                    signals.CompanyConfidence = 95;
                    signals.Signals.Add(new DetectedSignal
                    {
                        SignalType = "known_domain",
                        Field = "company",
                        Value = companyName,
                        Confidence = 95,
                        Source = "email_domain"
                    });
                    _logger.LogDebug($"  ✓ Company from known domain: '{companyName}' (95%)");
                    return;
                }
            }

            // Strategy 2: Check if it's a job board (then extract from body)
            var isJobBoard = JobBoardDomains.Any(jb => domain.Contains(jb.Key));
            if (isJobBoard)
            {
                var bodyCompany = ExtractCompanyFromBody(email);
                if (!string.IsNullOrEmpty(bodyCompany))
                {
                    signals.CompanyName = bodyCompany;
                    signals.CompanyConfidence = 70; // Lower confidence from body extraction
                    signals.Signals.Add(new DetectedSignal
                    {
                        SignalType = "body_extraction",
                        Field = "company",
                        Value = bodyCompany,
                        Confidence = 70,
                        Source = "email_body"
                    });
                    _logger.LogDebug($"  ✓ Company from body: '{bodyCompany}' (70%)");
                    return;
                }
            }

            // Strategy 3: From "From" name (60% confidence)
            var fromName = email.From.Split('<')[0].Trim();
            fromName = Regex.Replace(fromName, @"\s+(Team|Careers|Recruiting|HR|Jobs|Talent)$", "", RegexOptions.IgnoreCase);
            if (!string.IsNullOrEmpty(fromName) && !fromName.Contains("@") && fromName.Length > 2)
            {
                signals.CompanyName = fromName;
                signals.CompanyConfidence = 60;
                signals.Signals.Add(new DetectedSignal
                {
                    SignalType = "from_name",
                    Field = "company",
                    Value = fromName,
                    Confidence = 60,
                    Source = "email_headers"
                });
                _logger.LogDebug($"  ✓ Company from sender name: '{fromName}' (60%)");
                return;
            }

            // Strategy 4: Domain-based fallback (40% confidence)
            var baseDomain = domain.Split('.')[0];
            if (!string.IsNullOrEmpty(baseDomain))
            {
                var companyGuess = char.ToUpper(baseDomain[0]) + baseDomain.Substring(1);
                signals.CompanyName = companyGuess;
                signals.CompanyConfidence = 40;
                signals.Signals.Add(new DetectedSignal
                {
                    SignalType = "domain_fallback",
                    Field = "company",
                    Value = companyGuess,
                    Confidence = 40,
                    Source = "email_domain"
                });
                _logger.LogDebug($"  ? Company guess from domain: '{companyGuess}' (40%)");
                return;
            }

            signals.CompanyName = null;
            signals.CompanyConfidence = 0;
        }

        private string? ExtractCompanyFromBody(ProcessedEmail email)
        {
            var body = email.BodyPlainText ?? email.BodyHtml ?? "";
            var patterns = new[]
            {
                @"on behalf of\s+([A-Z][A-Za-z\s&]{2,40})",
                @"from\s+([A-Z][A-Za-z\s&]{2,40})\s+(?:team|careers)",
                @"([A-Z][A-Za-z\s&]{2,40})\s+(?:team|is hiring|is looking)",
                @"opportunities? at\s+([A-Z][A-Za-z\s&]{2,40})"
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(body, pattern);
                if (match.Success && match.Groups[1].Success)
                {
                    var company = match.Groups[1].Value.Trim();
                    if (company.Length >= 2 && company.Length <= 50)
                    {
                        return CleanCompanyName(company);
                    }
                }
            }

            return null;
        }

        private string CleanCompanyName(string company)
        {
            company = Regex.Replace(company, @"\s+(Inc\.?|LLC|Ltd\.?|Corp\.?|Corporation|Limited)$", "", RegexOptions.IgnoreCase);
            return company.Trim();
        }

        #endregion

        #region Status Detection

        private void DetectStatus(ProcessedEmail email, EmailSignals signals)
        {
            var content = $"{email.Subject}\n{email.BodyPlainText ?? email.BodyHtml ?? email.Snippet}".ToLower();

            var statusPatterns = new[]
            {
                new { Status = "Rejected", Keywords = new[] { "unfortunately", "not moving forward", "other candidates", "not selected", "not proceeding", "regret to inform", "will not be" }, Confidence = 95.0 },
                new { Status = "Interview Scheduled", Keywords = new[] { "interview scheduled", "interview invitation", "schedule an interview", "phone screen", "video interview" }, Confidence = 95.0 },
                new { Status = "Offer", Keywords = new[] { "offer letter", "pleased to offer", "congratulations", "we'd like to offer", "accept this offer" }, Confidence = 95.0 },
                new { Status = "Applied", Keywords = new[] { "application received", "application submitted", "thank you for applying", "successfully sent", "application confirmed" }, Confidence = 90.0 },
                new { Status = "In Progress", Keywords = new[] { "under review", "reviewing your application", "considering your application" }, Confidence = 85.0 },
                new { Status = "Interview Scheduled", Keywords = new[] { "interview", "meeting", "speak with" }, Confidence = 70.0 },
                new { Status = "Applied", Keywords = new[] { "received", "submitted", "confirmation" }, Confidence = 60.0 }
            };

            foreach (var statusInfo in statusPatterns)
            {
                var matchCount = statusInfo.Keywords.Count(kw => content.Contains(kw));
                if (matchCount > 0)
                {
                    var confidence = statusInfo.Confidence * (matchCount / (double)statusInfo.Keywords.Length);
                    signals.ApplicationStatus = statusInfo.Status;
                    signals.StatusConfidence = confidence;
                    signals.Signals.Add(new DetectedSignal
                    {
                        SignalType = "keyword_match",
                        Field = "status",
                        Value = statusInfo.Status,
                        Confidence = confidence,
                        Source = "content",
                        Pattern = $"Matched {matchCount}/{statusInfo.Keywords.Length} keywords"
                    });

                    _logger.LogDebug($"  ✓ Status: '{statusInfo.Status}' (confidence: {confidence:F0}%)");
                    return; // Take first match
                }
            }

            signals.ApplicationStatus = "Applied"; // Default
            signals.StatusConfidence = 50;
        }

        #endregion

        #region Interview Date Detection

        private void DetectInterviewDate(ProcessedEmail email, EmailSignals signals)
        {
            var content = email.BodyPlainText ?? email.BodyHtml ?? email.Snippet ?? "";

            // Pattern: "on January 15th at 2pm", "January 15, 2025 at 14:00"
            var datePatterns = new[]
            {
                @"(?:on|for)\s+([A-Z][a-z]+\s+\d{1,2}(?:st|nd|rd|th)?(?:,?\s+\d{4})?)\s+at\s+(\d{1,2}(?::\d{2})?\s*(?:am|pm|AM|PM)?)",
                @"(\d{1,2}[/-]\d{1,2}[/-]\d{2,4})\s+at\s+(\d{1,2}(?::\d{2})?\s*(?:am|pm|AM|PM)?)",
                @"((?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday),?\s+[A-Z][a-z]+\s+\d{1,2})"
            };

            foreach (var pattern in datePatterns)
            {
                var match = Regex.Match(content, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    if (DateTime.TryParse(match.Groups[1].Value, out var interviewDate))
                    {
                        // Only accept future dates or recent past (within 7 days)
                        if (interviewDate >= DateTime.UtcNow.AddDays(-7) && interviewDate <= DateTime.UtcNow.AddYears(1))
                        {
                            signals.InterviewDate = interviewDate;
                            signals.InterviewDateConfidence = 85;
                            signals.Signals.Add(new DetectedSignal
                            {
                                SignalType = "date_pattern",
                                Field = "interviewDate",
                                Value = interviewDate.ToString("yyyy-MM-dd"),
                                Confidence = 85,
                                Source = "body",
                                Pattern = pattern
                            });

                            _logger.LogDebug($"  ✓ Interview date: {interviewDate:yyyy-MM-dd} (85%)");
                            return;
                        }
                    }
                }
            }

            signals.InterviewDate = null;
            signals.InterviewDateConfidence = 0;
        }

        #endregion

        #region Recruiter Detection

        private void DetectRecruiter(ProcessedEmail email, EmailSignals signals)
        {
            // Check if sender looks like a personal recruiter (not noreply/jobs/careers)
            var fromEmail = email.FromEmail.ToLower();
            var isPersonal = !fromEmail.Contains("noreply") &&
                            !fromEmail.Contains("jobs") &&
                            !fromEmail.Contains("careers") &&
                            !fromEmail.Contains("no-reply");

            if (isPersonal)
            {
                var fromName = email.From.Split('<')[0].Trim();
                if (!string.IsNullOrEmpty(fromName) && fromName.Length > 2 && fromName.Length < 50)
                {
                    signals.RecruiterName = fromName;
                    signals.RecruiterEmail = email.FromEmail;
                    signals.RecruiterConfidence = 80;
                    signals.Signals.Add(new DetectedSignal
                    {
                        SignalType = "personal_email",
                        Field = "recruiter",
                        Value = fromName,
                        Confidence = 80,
                        Source = "email_headers"
                    });

                    _logger.LogDebug($"  ✓ Recruiter: '{fromName}' (80%)");
                    return;
                }
            }

            // Extract from signature
            var body = email.BodyPlainText ?? email.BodyHtml ?? "";
            var signatureMatch = Regex.Match(body, @"(?:Regards?|Thanks?|Best),?\s*\n\s*([A-Z][a-z]+\s+[A-Z][a-z]+)", RegexOptions.IgnoreCase);
            if (signatureMatch.Success)
            {
                signals.RecruiterName = signatureMatch.Groups[1].Value.Trim();
                signals.RecruiterEmail = email.FromEmail;
                signals.RecruiterConfidence = 60;
                signals.Signals.Add(new DetectedSignal
                {
                    SignalType = "signature",
                    Field = "recruiter",
                    Value = signals.RecruiterName,
                    Confidence = 60,
                    Source = "signature"
                });

                _logger.LogDebug($"  ✓ Recruiter from signature: '{signals.RecruiterName}' (60%)");
                return;
            }

            signals.RecruiterName = null;
            signals.RecruiterEmail = null;
            signals.RecruiterConfidence = 0;
        }

        #endregion

        #region Job URL Detection

        private void DetectJobUrl(ProcessedEmail email, EmailSignals signals)
        {
            var content = email.BodyPlainText ?? email.BodyHtml ?? "";

            // Extract URLs
            var urlPattern = @"https?://[^\s<>""']+";
            var matches = Regex.Matches(content, urlPattern);

            foreach (Match match in matches)
            {
                var url = match.Value;
                // Check if URL looks like a job posting
                if (url.Contains("/job") || url.Contains("/career") || url.Contains("/position") ||
                    url.Contains("linkedin.com/jobs") || url.Contains("indeed.com/viewjob"))
                {
                    signals.JobUrl = url;
                    signals.Signals.Add(new DetectedSignal
                    {
                        SignalType = "url_pattern",
                        Field = "jobUrl",
                        Value = url,
                        Confidence = 75,
                        Source = "body"
                    });

                    _logger.LogDebug($"  ✓ Job URL found (75%)");
                    return;
                }
            }
        }

        #endregion

        #region Salary Detection

        private void DetectSalary(ProcessedEmail email, EmailSignals signals)
        {
            var content = email.BodyPlainText ?? email.BodyHtml ?? "";

            var salaryPatterns = new[]
            {
                @"\$\d{2,3}[,\d]*(?:\s*-\s*\$\d{2,3}[,\d]*)?(?:\s*(?:per|/)\s*(?:year|annum|month))?",
                @"R\d{3}[,\d]*(?:\s*-\s*R\d{3}[,\d]*)?(?:\s*(?:per|/)\s*(?:year|annum|month))?",
                @"£\d{2,3}[,\d]*(?:\s*-\s*£\d{2,3}[,\d]*)?(?:\s*(?:per|/)\s*(?:year|annum))?",
                @"€\d{2,3}[,\d]*(?:\s*-\s*€\d{2,3}[,\d]*)?(?:\s*(?:per|/)\s*(?:year|annum))?"
            };

            foreach (var pattern in salaryPatterns)
            {
                var match = Regex.Match(content, pattern);
                if (match.Success)
                {
                    signals.SalaryRange = match.Value;
                    signals.Signals.Add(new DetectedSignal
                    {
                        SignalType = "salary_pattern",
                        Field = "salary",
                        Value = match.Value,
                        Confidence = 80,
                        Source = "body"
                    });

                    _logger.LogDebug($"  ✓ Salary: {match.Value} (80%)");
                    return;
                }
            }
        }

        #endregion

        #region Confidence Calculation

        private void CalculateOverallConfidence(EmailSignals signals)
        {
            // Weighted average based on importance
            var weights = new Dictionary<string, double>
            {
                { "position", 0.35 },      // Most important
                { "company", 0.30 },       // Very important
                { "status", 0.25 },        // Important
                { "recruiter", 0.05 },     // Nice to have
                { "interviewDate", 0.05 }  // Nice to have
            };

            double weightedSum = 0;
            double totalWeight = 0;

            if (!string.IsNullOrEmpty(signals.Position))
            {
                weightedSum += signals.PositionConfidence * weights["position"];
                totalWeight += weights["position"];
            }

            if (!string.IsNullOrEmpty(signals.CompanyName))
            {
                weightedSum += signals.CompanyConfidence * weights["company"];
                totalWeight += weights["company"];
            }

            if (!string.IsNullOrEmpty(signals.ApplicationStatus))
            {
                weightedSum += signals.StatusConfidence * weights["status"];
                totalWeight += weights["status"];
            }

            if (!string.IsNullOrEmpty(signals.RecruiterName))
            {
                weightedSum += signals.RecruiterConfidence * weights["recruiter"];
                totalWeight += weights["recruiter"];
            }

            if (signals.InterviewDate.HasValue)
            {
                weightedSum += signals.InterviewDateConfidence * weights["interviewDate"];
                totalWeight += weights["interviewDate"];
            }

            signals.OverallConfidence = totalWeight > 0 ? weightedSum / totalWeight : 0;
        }

        #endregion
    }
}