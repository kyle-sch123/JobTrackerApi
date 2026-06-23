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

        private static readonly string[] GenericEmailDomains = new[]
        {
            "gmail.com",
            "yahoo.com",
            "outlook.com",
            "hotmail.com",
            "live.com",
            "icloud.com"
        };

        public RuleBasedEmailParser(ILogger<RuleBasedEmailParser> logger)
        {
            _logger = logger;
        }

        public EmailSignals ParseEmail(ProcessedEmail email)
        {
            var signals = new EmailSignals();

            _logger.LogInformation($"🔍 Rule-based parsing: {email.Subject}");

            // Step 0: Check if this is a newsletter/job alert (not an actual application)
            DetectNonApplicationEmail(email, signals);

            // If it's not a job application, skip extraction and return early with low confidence
            if (!signals.IsJobApplication)
            {
                _logger.LogInformation($"🚫 Detected as non-application email (newsletter/alert/posting)");
                signals.OverallConfidence = 0;
                return signals;
            }

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

        #region Non-Application Detection

        /// <summary>
        /// Detects if an email is NOT an actual job application response
        /// (e.g., newsletters, job alerts, job postings, marketing emails)
        /// </summary>
        private void DetectNonApplicationEmail(ProcessedEmail email, EmailSignals signals)
        {
            var subject = email.Subject?.ToLower() ?? "";
            var body = GetBodyText(email).ToLower();
            var content = $"{subject} {body}";
            var fromEmail = email.FromEmail?.ToLower() ?? "";

            // Newsletter/alert indicators in subject
            var newsletterSubjectPatterns = new[]
            {
                @"new jobs? (?:matching|for you|alert|posted)",
                @"jobs? you might (?:like|be interested)",
                @"\d+ new jobs?",
                @"weekly job (?:digest|alert|update)",
                @"daily job (?:digest|alert|update)",
                @"job recommendations?",
                @"jobs? in your area",
                @"jobs? based on your",
                @"job alert:",
                @"new opportunities",
                @"trending jobs?",
                @"top jobs? for",
                @"jobs? matching your (?:profile|search|preferences)",
                @"your job alert",
                @"career opportunities",
                @"is hiring",
                @"are hiring",
                @"now hiring"
            };

            foreach (var pattern in newsletterSubjectPatterns)
            {
                if (Regex.IsMatch(subject, pattern, RegexOptions.IgnoreCase))
                {
                    signals.IsJobApplication = false;
                    signals.Signals.Add(new DetectedSignal
                    {
                        SignalType = "newsletter_subject",
                        Field = "isJobApplication",
                        Value = "false",
                        Confidence = 95,
                        Source = "subject",
                        Pattern = pattern
                    });
                    _logger.LogDebug($"  ✗ Newsletter detected via subject pattern: {pattern}");
                    return;
                }
            }

            // Newsletter/alert indicators in body
            var newsletterBodyPatterns = new[]
            {
                @"(?:here are|check out|see) (?:the |some )?(?:new )?jobs?",
                @"jobs? that match your",
                @"based on your (?:profile|search|job alert)",
                @"unsubscribe from (?:these )?(?:job )?alerts?",
                @"manage your job alerts?",
                @"update your job preferences",
                @"you're receiving this (?:email )?because you (?:signed up|subscribed)",
                @"view (?:all |more )?jobs?",
                @"see all \d+ jobs?",
                @"browse (?:more |all )?jobs?"
            };

            foreach (var pattern in newsletterBodyPatterns)
            {
                if (Regex.IsMatch(body, pattern, RegexOptions.IgnoreCase))
                {
                    signals.IsJobApplication = false;
                    signals.Signals.Add(new DetectedSignal
                    {
                        SignalType = "newsletter_body",
                        Field = "isJobApplication",
                        Value = "false",
                        Confidence = 90,
                        Source = "body",
                        Pattern = pattern
                    });
                    _logger.LogDebug($"  ✗ Newsletter detected via body pattern: {pattern}");
                    return;
                }
            }

            // Known newsletter/alert sender patterns
            var newsletterSenderPatterns = new[]
            {
                @"jobalert",
                @"job-alert",
                @"jobs@",
                @"careers@",
                @"notifications@",
                @"alerts@",
                @"newsletter@",
                @"noreply.*linkedin",
                @"messages-noreply@linkedin",
                @"jobmail"
            };

            foreach (var pattern in newsletterSenderPatterns)
            {
                if (Regex.IsMatch(fromEmail, pattern, RegexOptions.IgnoreCase))
                {
                    // Additional check: if it's from a noreply AND has newsletter content, flag it
                    var hasNewsletterContent = content.Contains("new job") ||
                                               content.Contains("jobs for you") ||
                                               content.Contains("job alert") ||
                                               content.Contains("matching your") ||
                                               content.Contains("recommended job");

                    if (hasNewsletterContent)
                    {
                        signals.IsJobApplication = false;
                        signals.Signals.Add(new DetectedSignal
                        {
                            SignalType = "newsletter_sender",
                            Field = "isJobApplication",
                            Value = "false",
                            Confidence = 85,
                            Source = "sender",
                            Pattern = pattern
                        });
                        _logger.LogDebug($"  ✗ Newsletter detected via sender + content: {pattern}");
                        return;
                    }
                }
            }

            // LinkedIn non-application emails. LinkedIn itself only ever sends job
            // alerts, social/feed notifications, and network digests — never an actual
            // application response (those come from the employer/ATS). Real recruiter
            // outreach is InMail/messaging, which these patterns deliberately avoid.
            if (fromEmail.Contains("linkedin"))
            {
                var linkedInNonApplicationPatterns = new[]
                {
                    // Job alerts / postings
                    @"jobs? you may be interested in",
                    @"jobs? for you",
                    @"\d+ new jobs?",
                    @"jobs? in .+ are hiring",
                    @"your job alert for",

                    // Social / feed activity (the "share their thoughts" digest, etc.)
                    @"shared? (?:a|an|their|his|her)\s+(?:post|article|update|thought)",
                    @"and others?\s+(?:share|shared|are)",
                    @"shares?\s+(?:their|his|her)\s+thoughts?",
                    @"commented on",
                    @"reacted to",
                    @"liked? (?:your|a)",
                    @"trending (?:in your network|post)",
                    @"top voices",
                    @"new post(?:s)? from",

                    // Network / profile notifications
                    @"viewed your profile",
                    @"appeared in \d+ search",
                    @"people you may know",
                    @"wants? to connect",
                    @"invitation to connect",
                    @"new connection",
                    @"congratulate ",
                    @"work anniversary",
                    @"started a new (?:position|job|role)"
                };

                foreach (var pattern in linkedInNonApplicationPatterns)
                {
                    if (Regex.IsMatch(content, pattern, RegexOptions.IgnoreCase))
                    {
                        signals.IsJobApplication = false;
                        signals.Signals.Add(new DetectedSignal
                        {
                            SignalType = "linkedin_non_application",
                            Field = "isJobApplication",
                            Value = "false",
                            Confidence = 95,
                            Source = "content",
                            Pattern = pattern
                        });
                        _logger.LogDebug($"  ✗ LinkedIn non-application email detected: {pattern}");
                        return;
                    }
                }
            }

            // If none of the negative patterns matched, it's likely a real application email
            signals.IsJobApplication = true;
        }

        #endregion

        #region Position Detection

        private void DetectPosition(ProcessedEmail email, EmailSignals signals)
        {
            var bodyText = GetBodyText(email);
            var contentText = $"{email.Subject}\n{bodyText}";
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
                                patternInfo.Source == "body" ? bodyText :
                                contentText;

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
            var bodyText = GetBodyText(email);

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
                var subjectCompany = ExtractCompanyFromSubject(email.Subject);
                if (!string.IsNullOrEmpty(subjectCompany))
                {
                    signals.CompanyName = subjectCompany;
                    signals.CompanyConfidence = 70;
                    signals.Signals.Add(new DetectedSignal
                    {
                        SignalType = "subject_extraction",
                        Field = "company",
                        Value = subjectCompany,
                        Confidence = 70,
                        Source = "subject"
                    });
                    _logger.LogDebug($"Company from subject: '{subjectCompany}' (70%)");
                    return;
                }

                var bodyCompany = ExtractCompanyFromBody(bodyText);
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

            if (isJobBoard && string.IsNullOrEmpty(signals.CompanyName))
            {
                signals.CompanyConfidence = 0;
                return;
            }

            // Strategy 3: From "From" name (60% confidence)
            var fromName = email.From.Split('<')[0].Trim();
            fromName = Regex.Replace(fromName, @"\s+(Team|Careers|Recruiting|HR|Jobs|Talent)$", "", RegexOptions.IgnoreCase);
            if (!string.IsNullOrEmpty(fromName) && !fromName.Contains("@") && fromName.Length > 2)
            {
                // Low confidence: the sender display name is often an ATS/job board
                // or team mailbox, not the actual employer. Keep it below the
                // rule-only threshold so the LLM gets consulted to confirm.
                signals.CompanyName = fromName;
                signals.CompanyConfidence = 45;
                signals.Signals.Add(new DetectedSignal
                {
                    SignalType = "from_name",
                    Field = "company",
                    Value = fromName,
                    Confidence = 45,
                    Source = "email_headers"
                });
                _logger.LogDebug($"  ? Company from sender name (low confidence): '{fromName}' (45%)");
                return;
            }

            if (GenericEmailDomains.Any(d => domain.EndsWith(d)))
            {
                signals.CompanyName = null;
                signals.CompanyConfidence = 0;
                return;
            }

            // Strategy 4: Domain-based fallback (40% confidence)
            var baseDomain = domain.Split('.')[0];
            if (!string.IsNullOrEmpty(baseDomain))
            {
                var companyGuess = char.ToUpper(baseDomain[0]) + baseDomain.Substring(1);
                signals.CompanyName = companyGuess;
                signals.CompanyConfidence = 25;
                signals.Signals.Add(new DetectedSignal
                {
                    SignalType = "domain_fallback",
                    Field = "company",
                    Value = companyGuess,
                    Confidence = 25,
                    Source = "email_domain"
                });
                _logger.LogDebug($"  ? Company guess from domain (low confidence): '{companyGuess}' (25%)");
                return;
            }

            signals.CompanyName = null;
            signals.CompanyConfidence = 0;
        }

        private string? ExtractCompanyFromBody(string body)
        {
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

        private string? ExtractCompanyFromSubject(string subject)
        {
            if (string.IsNullOrWhiteSpace(subject)) return null;

            var patterns = new[]
            {
                @"application\s+(?:to|with|at)\s+([A-Z][A-Za-z0-9\s&'\-]{2,60})",
                @"interview\s+(?:with|at)\s+([A-Z][A-Za-z0-9\s&'\-]{2,60})",
                @"offer\s+from\s+([A-Z][A-Za-z0-9\s&'\-]{2,60})",
                @"rejection\s+from\s+([A-Z][A-Za-z0-9\s&'\-]{2,60})",
                @"your\s+application\s+to\s+([A-Z][A-Za-z0-9\s&'\-]{2,60})"
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(subject, pattern, RegexOptions.IgnoreCase);
                if (match.Success && match.Groups[1].Success)
                {
                    var company = match.Groups[1].Value.Trim();
                    if (company.Length >= 2 && company.Length <= 60)
                    {
                        return CleanCompanyName(company);
                    }
                }
            }

            return null;
        }

        private string CleanCompanyName(string company)
        {
            // Only clean up trailing punctuation and whitespace, preserve company suffixes (Corp, Inc, etc.)
            company = company.Trim().TrimEnd('.', ',');
            return company;
        }

        #endregion

        #region Status Detection

        private void DetectStatus(ProcessedEmail email, EmailSignals signals)
        {
            var content = $"{email.Subject}\n{GetBodyText(email)}".ToLower();

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

            signals.ApplicationStatus = null;
            signals.StatusConfidence = 0;
        }

        #endregion

        #region Interview Date Detection

        private void DetectInterviewDate(ProcessedEmail email, EmailSignals signals)
        {
            var content = GetBodyText(email);

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
                // Only treat the sender as the contact person if the display name
                // actually looks like a human name — not a team mailbox, ATS, or org.
                if (LooksLikePersonName(fromName))
                {
                    signals.RecruiterName = fromName;
                    signals.RecruiterEmail = email.FromEmail;
                    signals.RecruiterConfidence = 70;
                    signals.Signals.Add(new DetectedSignal
                    {
                        SignalType = "personal_email",
                        Field = "recruiter",
                        Value = fromName,
                        Confidence = 70,
                        Source = "email_headers"
                    });

                    _logger.LogDebug($"  ✓ Recruiter: '{fromName}' (70%)");
                    return;
                }
            }

            // Extract from signature
            var body = GetBodyText(email);
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

        // Heuristic: does a sender display name look like an actual person (e.g.
        // "Jane Smith") rather than a team mailbox, role, ATS, or company?
        private static bool LooksLikePersonName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            name = name.Trim();
            if (name.Length < 3 || name.Length > 50) return false;

            var lowered = name.ToLowerInvariant();
            string[] nonPersonTokens =
            {
                "team", "careers", "recruit", "recruiting", "hr", "talent", "jobs",
                "hiring", "no-reply", "noreply", "notification", "support", "info",
                "hello", "people", "office", "admin", "staffing", "agency", "group",
                "solutions", "technologies", "consulting", "inc", "llc", "ltd", "gmbh"
            };
            if (nonPersonTokens.Any(t => lowered.Contains(t))) return false;

            // Expect 2-3 capitalized alphabetic words (allow hyphen/apostrophe).
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts.Length > 3) return false;

            return parts.All(p =>
                p.Length >= 2 &&
                char.IsUpper(p[0]) &&
                p.All(c => char.IsLetter(c) || c == '-' || c == '\''));
        }

        #endregion

        #region Job URL Detection

        private void DetectJobUrl(ProcessedEmail email, EmailSignals signals)
        {
            var content = !string.IsNullOrWhiteSpace(email.BodyPlainText)
                ? email.BodyPlainText
                : (email.BodyHtml ?? "");

            if (string.IsNullOrWhiteSpace(content))
            {
                content = email.Snippet ?? "";
            }

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
            var content = GetBodyText(email);

            var salaryPatterns = new[]
            {
                @"\$\d{2,3}[,\d]*(?:\s*-\s*\$\d{2,3}[,\d]*)?(?:\s*(?:per|/)\s*(?:year|annum|month))?",
                @"R\d{3}[,\d]*(?:\s*-\s*R\d{3}[,\d]*)?(?:\s*(?:per|/)\s*(?:year|annum|month))?",
                @"A\$\d{2,3}[,\d]*(?:\s*-\s*A\$\d{2,3}[,\d]*)?(?:\s*(?:per|/)\s*(?:year|annum))?",
                @"(?:USD|EUR|GBP)\s*\d{2,3}[,\d]*(?:\s*-\s*(?:USD|EUR|GBP)\s*\d{2,3}[,\d]*)?(?:\s*(?:per|/)\s*(?:year|annum))?",
                @"A\$\d{2,3}[,\d]*(?:\s*-\s*A\$\d{2,3}[,\d]*)?(?:\s*(?:per|/)\s*(?:year|annum))?",
                @"(?:USD|EUR|GBP)\s*\d{2,3}[,\d]*(?:\s*-\s*(?:USD|EUR|GBP)\s*\d{2,3}[,\d]*)?(?:\s*(?:per|/)\s*(?:year|annum))?",
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

            var overallConfidence = totalWeight > 0 ? weightedSum / totalWeight : 0;

            // Penalize missing core fields to avoid false positives
            if (string.IsNullOrEmpty(signals.CompanyName))
            {
                overallConfidence = Math.Max(0, overallConfidence - 20);
            }

            if (string.IsNullOrEmpty(signals.Position))
            {
                overallConfidence = Math.Max(0, overallConfidence - 20);
            }

            signals.OverallConfidence = overallConfidence;
        }

        private string GetBodyText(ProcessedEmail email)
        {
            if (!string.IsNullOrWhiteSpace(email.BodyPlainText))
            {
                return email.BodyPlainText;
            }

            if (!string.IsNullOrWhiteSpace(email.BodyHtml))
            {
                return StripHtml(email.BodyHtml);
            }

            return email.Snippet ?? "";
        }

        private string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";

            html = Regex.Replace(html, "<style[^>]*>.*?</style>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "<script[^>]*>.*?</script>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "<[^>]+>", " ");
            html = Regex.Replace(html, @"\s+", " ");
            return html.Trim();
        }

        #endregion
    }
}
