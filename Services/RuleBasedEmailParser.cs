using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using JobTrackerApi.Models;

namespace JobTrackerApi.Services
{
    /// <summary>
    /// Fast, deterministic rule-based email parser.
    /// Extracts structured data using sender-specific templates, regex patterns
    /// and keyword matching. Designed to handle the vast majority of emails
    /// without needing the (paid) LLM parser at all.
    /// </summary>
    public class RuleBasedEmailParser
    {
        private readonly ILogger<RuleBasedEmailParser> _logger;

        #region Known sender templates

        /// <summary>
        /// A template describing how a specific job board / ATS / agency formats
        /// its emails. Application patterns positively identify an application
        /// response and capture "position" / "company" named groups; non-application
        /// patterns identify the sender's alert/digest/marketing mail.
        /// </summary>
        private sealed class KnownSenderTemplate
        {
            public required string Board { get; init; }
            public required string[] DomainMarkers { get; init; }
            public (string Pattern, string Status)[] ApplicationSubjectPatterns { get; init; } =
                Array.Empty<(string, string)>();
            public string[] PositionBodyPatterns { get; init; } = Array.Empty<string>();
            public string[] CompanyBodyPatterns { get; init; } = Array.Empty<string>();
            public string[] NonApplicationSubjectPatterns { get; init; } = Array.Empty<string>();
            // The employer's name is genuinely never present in this sender's
            // application confirmations (the board hides the advertiser).
            public bool EmployerUsuallyAbsent { get; init; }
            // Sender only blasts job ads/digests: anything not positively matched
            // as an application response is a non-application.
            public bool DefaultNonApplication { get; init; }
        }

        // Patterns are matched against the subject unless noted. Templates are
        // checked before all generic heuristics, so keep them precise: a false
        // template match would bypass the LLM safety net entirely.
        private static readonly KnownSenderTemplate[] KnownSenders =
        {
            // PNet (SA job board). Confirmed prod format:
            //   "Kyle Erik, your application for {POSITION} has been sent"
            // The employer is never named — applications go through the board.
            new()
            {
                Board = "PNet",
                DomainMarkers = new[] { "pnet.co.za" },
                ApplicationSubjectPatterns = new[]
                {
                    (@"your application for (?<position>.+?) (?:has been|was) (?:sent|submitted|received)",
                     ApplicationStatuses.Applied)
                },
                NonApplicationSubjectPatterns = new[]
                {
                    @"^advert alert", @"\bjob alert\b", @"\bnewsletter\b",
                    @"jobs? (?:for you|in your)", @"recommended jobs?", @"your profile"
                },
                EmployerUsuallyAbsent = true
            },

            // Placement Partner (SA recruitment-agency ATS). Confirmed prod format:
            //   "Your application has been received"
            new()
            {
                Board = "Placement Partner",
                DomainMarkers = new[] { "placementpartner" },
                ApplicationSubjectPatterns = new[]
                {
                    (@"your application (?:has been|was) received", ApplicationStatuses.Applied),
                    (@"^application received", ApplicationStatuses.Applied)
                },
                PositionBodyPatterns = new[]
                {
                    @"application for (?:the )?(?:position of )?(?<position>[^.,\r\n]{4,60}?)(?:\s+(?:has|was)\b|[.,\r\n])",
                    @"(?:position|vacancy)\s*[:\-]\s*(?<position>[^.,\r\n]{4,60})"
                },
                CompanyBodyPatterns = new[]
                {
                    @"on behalf of (?<company>[A-Z][\w&.\-' ]{2,50}?)[.,\r\n]"
                },
                EmployerUsuallyAbsent = true
            },

            // LinkedIn. Application confirmations name the employer in the subject;
            // everything else LinkedIn sends is alerts/social/network noise.
            new()
            {
                Board = "LinkedIn",
                DomainMarkers = new[] { "linkedin.com" },
                ApplicationSubjectPatterns = new[]
                {
                    (@"your application was sent to (?<company>.+?)\s*$", ApplicationStatuses.Applied),
                    (@"you(?:'ve| have)? applied to (?<position>.+?) at (?<company>.+?)\s*$", ApplicationStatuses.Applied),
                    (@"your application to (?<position>.+?) at (?<company>.+?)\s*$", ApplicationStatuses.Applied),
                    (@"your application was viewed by (?<company>.+?)\s*$", ApplicationStatuses.InProgress)
                },
                NonApplicationSubjectPatterns = new[]
                {
                    // Job alerts / postings
                    @"jobs? you may be interested in", @"jobs? for you", @"\d+ new jobs?",
                    @"your job alert", @"explore new jobs", @"new jobs? similar to",
                    @"jobs? in .+ are hiring", @"be an early applicant",
                    // Social / feed activity
                    @"shared? (?:a|an|their|his|her)\s+(?:post|article|update|thought)",
                    @"shares?\s+(?:their|his|her)\s+thoughts?", @"and others? share",
                    @"commented on", @"reacted to", @"liked? (?:your|a)",
                    @"trending", @"top voices", @"new post(?:s)? from", @"has a new post",
                    // Network / profile notifications
                    @"viewed your profile", @"appeared in \d+ search", @"people you may know",
                    @"wants? to connect", @"invitation", @"new connection",
                    @"congratulate ", @"work anniversary", @"started a new (?:position|job|role)",
                    @"add .+ to your network", @"your profile"
                }
            },

            // Indeed. "match.indeed.com" job-match digests are ads; application
            // confirmations follow "Indeed Application: {Position}".
            new()
            {
                Board = "Indeed",
                DomainMarkers = new[] { "indeed.com" },
                ApplicationSubjectPatterns = new[]
                {
                    (@"^indeed application[:\s]+(?<position>.+)$", ApplicationStatuses.Applied),
                    (@"application submitted\s*[:\-–]?\s*(?<position>[^-–]*?)(?:\s*[-–]\s*(?<company>.+))?$",
                     ApplicationStatuses.Applied),
                    (@"your application (?:was sent|has been submitted) to (?<company>.+?)\s*$",
                     ApplicationStatuses.Applied)
                },
                NonApplicationSubjectPatterns = new[]
                {
                    @"new jobs? similar to", @"jobs? (?:for you|waiting)", @"\bjob alert\b",
                    @"invited you to apply", @"be an early applicant", @"employers?\b",
                    @"jobs? matching", @"\d+ new jobs?"
                }
            },

            // JotForm — application forms hosted by employers; the confirmation
            // comes from JotForm's domain and rarely names the employer.
            new()
            {
                Board = "JotForm",
                DomainMarkers = new[] { "jotform.com" },
                ApplicationSubjectPatterns = new[]
                {
                    (@"(?:we(?:'ve| have)? )?received your (?:application|submission)", ApplicationStatuses.Applied),
                    (@"thank you for (?:applying|your application)", ApplicationStatuses.Applied),
                    (@"application", ApplicationStatuses.Applied)
                },
                EmployerUsuallyAbsent = true
            },

            // Hosted ATS platforms: the employer is named in subject or body.
            new()
            {
                Board = "ATS",
                DomainMarkers = new[]
                {
                    "greenhouse.io", "greenhouse-mail.io", "lever.co", "workday.com",
                    "myworkday", "icims.com", "smartrecruiters", "ashbyhq.com",
                    "teamtailor", "bamboohr.com", "workablemail.com", "recruitee.com",
                    "jobvite.com", "breezy.hr"
                },
                ApplicationSubjectPatterns = new[]
                {
                    (@"thank you for applying to (?<company>.+?)\s*[!.]?\s*$", ApplicationStatuses.Applied),
                    (@"your application (?:to|at|with) (?<company>.+?)\s*$", ApplicationStatuses.Applied),
                    (@"we(?:'ve| have) received your application(?:\s+(?:to|at|for)\s+(?<company>.+?))?\s*$",
                     ApplicationStatuses.Applied),
                    (@"application (?:received|confirmation)(?:\s*[-:–]\s*(?<position>.+))?$",
                     ApplicationStatuses.Applied)
                },
                PositionBodyPatterns = new[]
                {
                    @"(?:applying|application) (?:to|for) (?:the )?(?<position>[^.,\r\n]{4,60}?)(?:\s+(?:position|role|opening)\b|\s+at\b|[.,\r\n])",
                    @"(?:position|role)\s*[:\-]\s*(?<position>[^.,\r\n]{4,60})"
                },
                CompanyBodyPatterns = new[]
                {
                    @"thank you for (?:applying|your interest) (?:to|in|at) (?<company>[A-Z][\w&.\-' ]{1,50}?)[.,!\r\n]",
                    @"on behalf of (?<company>[A-Z][\w&.\-' ]{2,50}?)[.,\r\n]"
                },
                NonApplicationSubjectPatterns = new[]
                {
                    @"we are hiring", @"is hiring", @"join our", @"\bnewsletter\b", @"\bwebinar\b"
                }
            },

            // SA recruitment-ad blasters: these senders only ever send job adverts
            // and digests ("Advert Alert: …", "{Job title} @ {Company}"). Anything
            // not positively identified as an application response is an ad.
            new()
            {
                Board = "Job Ads",
                DomainMarkers = new[]
                {
                    "executiveplacements.com", "jobsbyemails.com", "gradlinc.co.za",
                    "careers24", "careerjunction", "jobmail.co.za"
                },
                ApplicationSubjectPatterns = new[]
                {
                    (@"your application", ApplicationStatuses.Applied),
                    (@"thank you for applying", ApplicationStatuses.Applied),
                    (@"application (?:received|successful)", ApplicationStatuses.Applied)
                },
                EmployerUsuallyAbsent = true,
                DefaultNonApplication = true
            }
        };

        #endregion

        // Known job board domains (used to avoid treating the board as the employer)
        private static readonly Dictionary<string, double> JobBoardDomains = new()
        {
            { "linkedin.com", 0.9 },
            { "indeed.com", 0.9 },
            { "glassdoor.com", 0.9 },
            { "pnet.co.za", 0.9 },
            { "placementpartner", 0.9 },
            { "executiveplacements.com", 0.9 },
            { "jobsbyemails.com", 0.9 },
            { "gradlinc.co.za", 0.9 },
            { "careers24", 0.9 },
            { "careerjunction", 0.9 },
            { "jotform.com", 0.9 },
            { "bamboohr.com", 0.8 },
            { "workday.com", 0.8 },
            { "myworkday", 0.8 },
            { "greenhouse.io", 0.8 },
            { "greenhouse-mail.io", 0.8 },
            { "lever.co", 0.8 },
            { "icims.com", 0.8 },
            { "smartrecruiters", 0.8 },
            { "ashbyhq.com", 0.8 },
            { "teamtailor", 0.8 },
            { "workablemail.com", 0.8 },
            { "recruitee.com", 0.8 },
            { "jobvite.com", 0.8 },
            { "breezy.hr", 0.8 },
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

            // Step 0: Known-sender templates decide authoritatively when they match.
            var template = FindKnownSenderTemplate(email);
            if (template != null && TryApplyKnownSenderTemplate(email, template, signals))
            {
                if (!signals.IsJobApplication)
                {
                    _logger.LogInformation($"🚫 {template.Board}: matched non-application template");
                    signals.OverallConfidence = 0;
                    return signals;
                }

                // Fill whatever the template didn't provide with generic detectors.
                if (string.IsNullOrEmpty(signals.Position))
                {
                    DetectPosition(email, signals);
                }
                // Deliberately no company detection when the board hides the
                // employer: the generic heuristics would grab the board itself
                // ("Pnet") as the company.
                if (string.IsNullOrEmpty(signals.CompanyName) && !signals.CompanyKnownAbsent)
                {
                    DetectCompany(email, signals);
                }
                DetectInterviewDate(email, signals);
                DetectRecruiter(email, signals);
                DetectJobUrl(email, signals);
                DetectSalary(email, signals);

                CalculateOverallConfidence(signals);

                _logger.LogInformation(
                    $"✅ Template [{template.Board}]: Company={signals.CompanyName} ({signals.CompanyConfidence:F0}%), " +
                    $"Position={signals.Position} ({signals.PositionConfidence:F0}%), " +
                    $"Status={signals.ApplicationStatus} ({signals.StatusConfidence:F0}%), " +
                    $"Overall={signals.OverallConfidence:F0}%"
                );

                return signals;
            }

            // Step 1: Check if this is a newsletter/job alert (not an actual application)
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

        #region Known sender template matching

        private static KnownSenderTemplate? FindKnownSenderTemplate(ProcessedEmail email)
        {
            var domain = email.FromEmail?.Split('@').LastOrDefault()?.ToLowerInvariant() ?? "";
            if (domain.Length == 0) return null;

            return KnownSenders.FirstOrDefault(t => t.DomainMarkers.Any(domain.Contains));
        }

        /// <summary>
        /// Returns true when the template made a decision (application response,
        /// non-application, or default-non-application). False falls through to
        /// the generic pipeline.
        /// </summary>
        private bool TryApplyKnownSenderTemplate(
            ProcessedEmail email, KnownSenderTemplate template, EmailSignals signals)
        {
            var subject = email.Subject ?? "";
            var body = GetBodyText(email);

            // 1) Application-response patterns win over everything.
            foreach (var (pattern, status) in template.ApplicationSubjectPatterns)
            {
                var match = Regex.Match(subject, pattern, RegexOptions.IgnoreCase);
                if (!match.Success) continue;

                signals.IsJobApplication = true;
                signals.FromRecognizedTemplate = true;
                signals.SourceJobBoard = template.Board;

                if (match.Groups["position"].Success &&
                    !string.IsNullOrWhiteSpace(match.Groups["position"].Value))
                {
                    var position = CleanPosition(match.Groups["position"].Value.Trim());
                    if (IsValidPosition(position))
                    {
                        signals.Position = position;
                        signals.PositionConfidence = 95;
                        AddSignal(signals, "template_subject", "position", position, 95, "subject", pattern);
                    }
                }

                if (match.Groups["company"].Success &&
                    !string.IsNullOrWhiteSpace(match.Groups["company"].Value))
                {
                    var company = CleanCompanyName(match.Groups["company"].Value.Trim());
                    if (company.Length is >= 2 and <= 60)
                    {
                        signals.CompanyName = company;
                        signals.CompanyConfidence = 95;
                        AddSignal(signals, "template_subject", "company", company, 95, "subject", pattern);
                    }
                }

                // Body patterns fill gaps the subject didn't cover.
                if (string.IsNullOrEmpty(signals.Position))
                {
                    foreach (var bodyPattern in template.PositionBodyPatterns)
                    {
                        var m = Regex.Match(body, bodyPattern, RegexOptions.IgnoreCase);
                        if (m.Success && m.Groups["position"].Success)
                        {
                            var position = CleanPosition(m.Groups["position"].Value.Trim());
                            if (IsValidPosition(position))
                            {
                                signals.Position = position;
                                signals.PositionConfidence = 85;
                                AddSignal(signals, "template_body", "position", position, 85, "body", bodyPattern);
                                break;
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(signals.CompanyName))
                {
                    foreach (var bodyPattern in template.CompanyBodyPatterns)
                    {
                        var m = Regex.Match(body, bodyPattern);
                        if (m.Success && m.Groups["company"].Success)
                        {
                            var company = CleanCompanyName(m.Groups["company"].Value.Trim());
                            if (company.Length is >= 2 and <= 60)
                            {
                                signals.CompanyName = company;
                                signals.CompanyConfidence = 80;
                                AddSignal(signals, "template_body", "company", company, 80, "body", bodyPattern);
                                break;
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(signals.CompanyName) && template.EmployerUsuallyAbsent)
                    {
                        signals.CompanyKnownAbsent = true;
                    }
                }

                // Status: the email content may reveal more than "applied" (e.g. a
                // rejection sent through the same board), so let the keyword
                // detector run and only fall back to the template's status.
                DetectStatus(email, signals);
                if (signals.StatusConfidence < 85)
                {
                    signals.ApplicationStatus = status;
                    signals.StatusConfidence = 95;
                    AddSignal(signals, "template_status", "status", status, 95, "subject", pattern);
                }

                return true;
            }

            // 2) Sender-specific alert/digest/marketing patterns.
            foreach (var pattern in template.NonApplicationSubjectPatterns)
            {
                if (Regex.IsMatch(subject, pattern, RegexOptions.IgnoreCase))
                {
                    signals.IsJobApplication = false;
                    AddSignal(signals, "template_non_application", "isJobApplication", "false", 95, "subject", pattern);
                    return true;
                }
            }

            // 3) Ad-blast senders: unmatched mail from them is an advert.
            if (template.DefaultNonApplication)
            {
                signals.IsJobApplication = false;
                AddSignal(signals, "template_default_non_application", "isJobApplication", "false", 90, "sender",
                    string.Join("|", template.DomainMarkers));
                return true;
            }

            return false;
        }

        private static void AddSignal(
            EmailSignals signals, string type, string field, string value,
            double confidence, string source, string? pattern = null)
        {
            signals.Signals.Add(new DetectedSignal
            {
                SignalType = type,
                Field = field,
                Value = value,
                Confidence = confidence,
                Source = source,
                Pattern = pattern
            });
        }

        #endregion

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
                @"advert alert",
                @"new opportunities",
                @"trending jobs?",
                @"top jobs? for",
                @"jobs? matching your (?:profile|search|preferences)",
                @"your job alert",
                @"career opportunities",
                @"is hiring",
                @"are hiring",
                @"now hiring",
                @"explore new jobs",
                @"new jobs? similar to",
                @"weekly job update",
                @"has a new post for you",
                @"invited? you to apply",
                @"be an early applicant",
                @"your profile is incomplete",
                @"who'?s viewed your profile"
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

            // If none of the negative patterns matched, it's likely a real application email
            signals.IsJobApplication = true;
        }

        #endregion

        #region Position Detection

        // Job-title grammar: (seniority)? (domain)+ (role), or (seniority) (role).
        private const string SeniorityWords =
            @"(?:Senior|Snr\.?|Sr\.?|Jr\.?|Junior|Lead|Principal|Staff|Graduate|Trainee|Intermediate|Entry[- ]?Level|Mid[- ]?Level|Head of|Chief)";
        private const string DomainWords =
            @"(?:Software|Full[- ]?Stack|Front[- ]?End|Back[- ]?End|Web|Mobile|iOS|Android|Data|DevOps|Cloud|Machine Learning|ML|AI|QA|Quality Assurance|Test|Automation|Security|Cyber ?Security|Platform|Site Reliability|Embedded|Systems?|Network|Database|Business Intelligence|BI|C#|\.NET|Java(?:Script)?|TypeScript|Python|Node(?:\.js)?|PHP|React|Angular|Application|Integration|Solutions?|IT|Support|Service Desk|Help ?Desk|Product|Project|Business)";
        private const string RoleWords =
            @"(?:Engineer|Developer|Programmer|Architect|Designer|Analyst|Scientist|Administrator|Consultant|Manager|Specialist|Technician|Intern|Agent|Officer|Lead)";
        private const string JobTitleGrammar =
            @"((?:" + SeniorityWords + @"\s+)(?:" + DomainWords + @"\s+){0,3}" + RoleWords +
            @"|(?:" + DomainWords + @"\s+){1,3}" + RoleWords + @")";

        private void DetectPosition(ProcessedEmail email, EmailSignals signals)
        {
            var bodyText = GetBodyText(email);
            var contentText = $"{email.Subject}\n{bodyText}";
            var patterns = new[]
            {
                // High confidence patterns (90-95%)
                new { Pattern = @"\|\s*([^|]+?)\s*\|(?:\s*JO-)", Confidence = 95.0, Source = "subject" },
                new { Pattern = @"your application for\s+(.+?)\s+(?:has been|was)\s+(?:sent|submitted|received)", Confidence = 95.0, Source = "subject" },
                new { Pattern = @"application for\s+(?:the\s+)?([^.!,\n]+?)(?:\s+has been|\s+position)", Confidence = 90.0, Source = "body" },
                new { Pattern = @"application for[:\s]+([^|,\n]+?)\s*(?:$|\s+at\s|\s+with\s|[-–|])", Confidence = 85.0, Source = "subject" },

                // Job title grammar (80%)
                new { Pattern = JobTitleGrammar, Confidence = 80.0, Source = "content" },

                // Medium confidence patterns (75%)
                new { Pattern = @"applied (?:to|for)\s+(?:the\s+)?([^.!,\n]+?)(?:\s+role|\s+at)", Confidence = 75.0, Source = "body" },
                new { Pattern = @"your\s+(?:application|submission)\s+for\s+(?:the\s+)?([^.!,\n]+)", Confidence = 75.0, Source = "body" },
                new { Pattern = @"(?:for|to)\s+the\s+([^.!,\n]{4,60}?)\s+(?:position|role|vacancy|post|opening)", Confidence = 75.0, Source = "body" },
                new { Pattern = @"position\s+of\s+([^.!,\n]{4,60})", Confidence = 75.0, Source = "body" },

                // Lower confidence patterns (60%)
                new { Pattern = @"position[:\s]+([A-Z][^.!,\n]{5,50})", Confidence = 60.0, Source = "body" },
                new { Pattern = @"role[:\s]+([A-Z][^.!,\n]{5,50})", Confidence = 60.0, Source = "body" },
                new { Pattern = @"vacancy[:\s]+([A-Z][^.!,\n]{5,50})", Confidence = 60.0, Source = "body" }
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
            return position.Length >= 4 &&
                   position.Length <= 100 &&
                   !position.StartsWith("JO-") &&
                   position.Any(char.IsLetter) &&
                   !Regex.IsMatch(position.ToLower(), @"^(the|this|that|your|requirements|experience|skills)$");
        }

        private string CleanPosition(string position)
        {
            // Remove trailing noise
            position = Regex.Replace(position, @"\s+(at|with|for|in)\s+\w+.*$", "", RegexOptions.IgnoreCase);
            position = Regex.Replace(position, @"\s+position\s*$", "", RegexOptions.IgnoreCase);
            position = Regex.Replace(position, @"\s+role\s*$", "", RegexOptions.IgnoreCase);
            position = position.Trim().Trim('"', '\'');

            // Normalize SHOUTING listings ("JUNIOR DEVELOPER" → "Junior Developer")
            if (position.Length >= 4 && position.Any(char.IsLetter) &&
                position == position.ToUpperInvariant())
            {
                position = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(position.ToLowerInvariant());
            }

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

            // Strategy 2: Check if it's a job board (then extract from subject/body)
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

                // Never fall back to sender identity for job boards — that's how
                // "Pnet" ends up recorded as the employer.
                signals.CompanyConfidence = 0;
                return;
            }

            // Strategy 3: Sender display name correlated with the sender domain
            // (e.g. "DotActiv" <hr@dotactiv.com>). Correlation makes the display
            // name trustworthy — an ATS or agency mailbox won't match the domain.
            var fromName = email.From.Split('<')[0].Trim().Trim('"');
            var cleanedFromName = Regex.Replace(
                fromName, @"\s+(Team|Careers|Recruiting|Recruitment|Talent|HR|Jobs|Hiring|People|Group)$",
                "", RegexOptions.IgnoreCase).Trim();

            var domainRoot = GetRegistrableDomainRoot(domain);
            var squashedName = new string(cleanedFromName.ToLowerInvariant()
                .Where(char.IsLetterOrDigit).ToArray());

            if (squashedName.Length >= 4 && domainRoot.Length >= 4 &&
                (squashedName == domainRoot ||
                 squashedName.StartsWith(domainRoot) ||
                 domainRoot.StartsWith(squashedName)))
            {
                signals.CompanyName = cleanedFromName;
                signals.CompanyConfidence = 78;
                signals.Signals.Add(new DetectedSignal
                {
                    SignalType = "display_name_domain_match",
                    Field = "company",
                    Value = cleanedFromName,
                    Confidence = 78,
                    Source = "email_headers"
                });
                _logger.LogDebug($"  ✓ Company from display name + domain: '{cleanedFromName}' (78%)");
                return;
            }

            // Strategy 3b: subject/body company mentions (works for direct employers too)
            var directSubjectCompany = ExtractCompanyFromSubject(email.Subject);
            if (!string.IsNullOrEmpty(directSubjectCompany))
            {
                signals.CompanyName = directSubjectCompany;
                signals.CompanyConfidence = 70;
                signals.Signals.Add(new DetectedSignal
                {
                    SignalType = "subject_extraction",
                    Field = "company",
                    Value = directSubjectCompany,
                    Confidence = 70,
                    Source = "subject"
                });
                return;
            }

            // Strategy 4: From "From" name alone (45% confidence)
            if (!string.IsNullOrEmpty(cleanedFromName) && !cleanedFromName.Contains("@") && cleanedFromName.Length > 2)
            {
                // Low confidence: the sender display name is often an ATS/job board
                // or team mailbox, not the actual employer. Keep it below the
                // rule-only threshold so the LLM gets consulted to confirm.
                signals.CompanyName = cleanedFromName;
                signals.CompanyConfidence = 45;
                signals.Signals.Add(new DetectedSignal
                {
                    SignalType = "from_name",
                    Field = "company",
                    Value = cleanedFromName,
                    Confidence = 45,
                    Source = "email_headers"
                });
                _logger.LogDebug($"  ? Company from sender name (low confidence): '{cleanedFromName}' (45%)");
                return;
            }

            if (GenericEmailDomains.Any(d => domain.EndsWith(d)))
            {
                signals.CompanyName = null;
                signals.CompanyConfidence = 0;
                return;
            }

            // Strategy 5: Domain-based fallback (25% confidence)
            var baseDomain = GetRegistrableDomainRoot(domain);
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

        /// <summary>
        /// "info.pnet.co.za" → "pnet"; "mail.alpaca.markets" → "alpaca";
        /// "dotactiv.com" → "dotactiv". Handles two-level ccTLDs like .co.za.
        /// </summary>
        private static string GetRegistrableDomainRoot(string domain)
        {
            var parts = domain.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return parts.FirstOrDefault() ?? "";

            var secondLevelMarkers = new[] { "co", "com", "org", "net", "ac", "gov", "edu" };
            if (parts.Length >= 3 &&
                parts[^1].Length == 2 &&
                secondLevelMarkers.Contains(parts[^2]))
            {
                return parts[^3];
            }

            return parts[^2];
        }

        private string? ExtractCompanyFromBody(string body)
        {
            var patterns = new[]
            {
                @"on behalf of\s+([A-Z][A-Za-z\s&]{2,40})",
                @"from\s+([A-Z][A-Za-z\s&]{2,40})\s+(?:team|careers)",
                @"([A-Z][A-Za-z\s&]{2,40})\s+(?:team|is hiring|is looking)",
                @"opportunities? at\s+([A-Z][A-Za-z\s&]{2,40})",
                @"join(?:ing)?\s+(?:the\s+)?([A-Z][A-Za-z\s&]{2,40}?)\s+team",
                @"[Tt]he\s+([A-Z][A-Za-z\s&]{2,40}?)\s+(?:Talent|Recruitment|Hiring|People)\s+Team",
                @"your\s+interest\s+in\s+(?:joining\s+)?([A-Z][A-Za-z\s&]{2,40})",
                @"(?:position|role|vacancy)\s+(?:at|with)\s+([A-Z][A-Za-z\s&]{2,40})"
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
                @"your\s+application\s+to\s+([A-Z][A-Za-z0-9\s&'\-]{2,60})",
                @"thank you for applying\s+(?:to|at)\s+([A-Z][A-Za-z0-9\s&'\-]{2,60})"
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
            company = company.Trim().TrimEnd('.', ',', '!');
            return company;
        }

        #endregion

        #region Status Detection

        // Ordered by decisiveness: a rejection phrase beats an interview phrase in
        // the same email ("we enjoyed interviewing you, unfortunately…").
        private static readonly (string Status, double BaseConfidence, string[] Phrases)[] StatusGroups =
        {
            (ApplicationStatuses.Rejected, 95, new[]
            {
                "unfortunately", "not moving forward", "other candidates", "not selected",
                "not proceeding", "regret to inform", "will not be progressing",
                "unsuccessful", "decided not to proceed", "position has been filled",
                "not be taking your application further", "pursue other applicants"
            }),
            (ApplicationStatuses.Offer, 95, new[]
            {
                "offer letter", "pleased to offer", "we'd like to offer", "we would like to offer",
                "offer of employment", "extend an offer", "accept this offer", "formal offer"
            }),
            (ApplicationStatuses.InterviewScheduled, 92, new[]
            {
                "interview scheduled", "interview invitation", "schedule an interview",
                "phone screen", "video interview", "invite you to interview",
                "interview has been", "confirm your interview"
            }),
            (ApplicationStatuses.InProgress, 85, new[]
            {
                "under review", "reviewing your application", "considering your application",
                "being considered", "shortlisted", "assessment", "coding challenge", "online test"
            }),
            (ApplicationStatuses.Applied, 90, new[]
            {
                "application received", "application submitted", "application has been sent",
                "application was sent", "thank you for applying", "successfully sent",
                "application confirmed", "we received your application",
                "we have received your application", "has been received",
                "successfully applied", "successfully submitted"
            }),
            // Weak fallbacks — only fire when nothing stronger matched.
            (ApplicationStatuses.InterviewScheduled, 65, new[] { "interview" }),
            (ApplicationStatuses.Applied, 55, new[] { "received", "submitted", "confirmation" })
        };

        private void DetectStatus(ProcessedEmail email, EmailSignals signals)
        {
            var content = $"{email.Subject}\n{GetBodyText(email)}".ToLower();

            foreach (var (status, baseConfidence, phrases) in StatusGroups)
            {
                var matchCount = phrases.Count(p => content.Contains(p));
                if (matchCount > 0)
                {
                    // One decisive phrase is enough for the base confidence; extra
                    // matches only nudge it up. (The old formula divided by the
                    // phrase-list length, so a lone "unfortunately" scored 14%.)
                    var confidence = Math.Min(98, baseConfidence + 2 * (matchCount - 1));
                    signals.ApplicationStatus = status;
                    signals.StatusConfidence = confidence;
                    signals.Signals.Add(new DetectedSignal
                    {
                        SignalType = "keyword_match",
                        Field = "status",
                        Value = status,
                        Confidence = confidence,
                        Source = "content",
                        Pattern = $"Matched {matchCount} phrase(s)"
                    });

                    _logger.LogDebug($"  ✓ Status: '{status}' (confidence: {confidence:F0}%)");
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

            // Pattern: "on January 15th at 2pm", "January 15, 2025 at 14:00",
            // "scheduled for 15 July 2026", "15/07/2026 at 10:00"
            var datePatterns = new[]
            {
                @"(?:on|for)\s+([A-Z][a-z]+\s+\d{1,2}(?:st|nd|rd|th)?(?:,?\s+\d{4})?)\s+at\s+(\d{1,2}(?::\d{2})?\s*(?:am|pm|AM|PM)?)",
                @"(?:on|for)\s+(\d{1,2}\s+[A-Z][a-z]+\s+\d{4})(?:\s+at\s+(\d{1,2}(?::\d{2})?\s*(?:am|pm|AM|PM)?))?",
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
                            !fromEmail.Contains("no-reply") &&
                            !fromEmail.Contains("donotreply") &&
                            !fromEmail.StartsWith("info@");

            if (isPersonal)
            {
                var fromName = email.From.Split('<')[0].Trim().Trim('"');
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
            if (signatureMatch.Success && LooksLikePersonName(signatureMatch.Groups[1].Value.Trim()))
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
                // Rand amounts, allowing SA-style thousands spacing: "R25 000 - R30 000 per month"
                @"R\s?\d{2,3}(?:[ ,]\d{3})*(?:\s*-\s*R\s?\d{2,3}(?:[ ,]\d{3})*)?(?:\s*(?:per|/)\s*(?:year|annum|month))?",
                @"R\d{2,3}[.,]?\d*k(?:\s*-\s*R?\d{2,3}[.,]?\d*k)?",
                @"A\$\d{2,3}[,\d]*(?:\s*-\s*A\$\d{2,3}[,\d]*)?(?:\s*(?:per|/)\s*(?:year|annum))?",
                @"(?:USD|EUR|GBP|ZAR)\s*\d{2,3}[,\d]*(?:\s*-\s*(?:USD|EUR|GBP|ZAR)\s*\d{2,3}[,\d]*)?(?:\s*(?:per|/)\s*(?:year|annum|month))?",
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

            // Penalize missing core fields to avoid false positives. This applies
            // even when the company is known-absent (job boards): the record is
            // still incomplete and should land in the review band, not auto-create.
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
            // Preserve line structure: signature and date patterns rely on newlines.
            html = Regex.Replace(html, @"<br\s*/?>|</(?:p|div|tr|li|h[1-6])>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "<[^>]+>", " ");
            html = WebUtility.HtmlDecode(html);
            html = Regex.Replace(html, @"[ \t]+", " ");
            html = Regex.Replace(html, @"\s*\n\s*", "\n");
            return html.Trim();
        }

        #endregion
    }
}
