using JobTrackerApi.Models;

namespace JobTrackerApi.Services
{
    /// <summary>
    /// Lightweight service to determine if an email is job-related before expensive AI parsing
    /// </summary>
    public class JobRelatedEmailFilter
    {
        private readonly ILogger<JobRelatedEmailFilter> _logger;

        // Keywords that strongly indicate job-related emails
        private readonly string[] _jobKeywords = {
            "job", "position", "role", "application", "interview", "hiring", "recruit", "candidate",
            "offer", "salary", "compensation", "employment", "career", "vacancy", "opening",
            "recruiter", "hiring manager", "hr", "human resources", "talent", "staffing",
            "opportunity", "apply", "resume", "cv", "cover letter"
        };

        // Email domains commonly associated with job-related communications
        private readonly string[] _jobDomains = {
            "linkedin.com", "indeed.com", "monster.com", "dice.com", "careerbuilder.com",
            "glassdoor.com", "ziprecruiter.com", "recruiting", "hr.", "careers.",
            "jobs.", "hiring.", "recruit.", "talent.", "greenhouse.io", "lever.co",
            "workday.com", "icims.com", "applicant", "candidate"
        };

        // Subject prefixes that indicate job-related emails
        private readonly string[] _jobSubjectPrefixes = {
            "job application", "application for", "interview", "offer", "rejection",
            "thank you for applying", "your application", "position", "role",
            "hiring", "recruiting", "job opportunity", "career opportunity"
        };

        // Strong indicators of application RESPONSE emails
        private readonly string[] _applicationResponseIndicators = {
            "application received", "we received your application", "thank you for applying",
            "we have received your application", "your application was sent",
            "your application has been", "your application for", "application confirmation",
            "has been sent", "has been submitted", "successfully applied",
            "interview invitation", "schedule an interview", "phone screen", "video interview",
            "interview scheduled", "offer letter", "we'd like to offer", "pleased to offer",
            "regret to inform", "not moving forward", "we have decided", "assessment",
            "coding challenge"
        };

        // Indicators of job alerts/newsletters (false positives)
        private readonly string[] _nonApplicationIndicators = {
            "job alert", "advert alert", "jobs you may like", "jobs you might like",
            "recommended jobs", "job recommendations", "new jobs", "weekly jobs",
            "daily jobs", "job digest", "weekly job update", "explore new jobs",
            "career advice", "salary alert", "matching jobs", "saved search", "newsletter",
            "has a new post for you", "share their thoughts"
        };

        public JobRelatedEmailFilter(ILogger<JobRelatedEmailFilter> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Determines if an email is likely job-related using lightweight heuristics
        /// </summary>
        public bool IsJobRelated(ProcessedEmail email)
        {
            var subject = email.Subject ?? "";
            var from = email.From ?? "";
            var snippet = email.Snippet ?? "";
            var combined = $"{subject} {snippet} {from}".ToLowerInvariant();

            // Strong negative signals: alerts/digests/newsletters
            if (ContainsAny(combined, _nonApplicationIndicators) && !ContainsAny(combined, _applicationResponseIndicators))
            {
                _logger.LogDebug($"Email marked NOT job-related (alert/newsletter): {subject}");
                return false;
            }

            // Strong positive signals: application response
            if (ContainsAny(combined, _applicationResponseIndicators))
            {
                _logger.LogDebug($"Email marked job-related by response indicators: {subject}");
                return true;
            }

            var score = 0;

            // Check subject for job-related keywords
            if (ContainsJobKeywords(subject))
            {
                score += 1;
            }

            // A subject that names an application or interview is near-certainly
            // about the recipient's own candidacy (e.g. a reply like
            // "Re: Following Up on … Application" from a company domain), even when
            // the sender carries no other signal.
            if (ContainsStrongSubjectKeyword(subject))
            {
                score += 2;
            }

            // Check sender domain for job-related patterns
            if (IsJobRelatedDomain(email.FromEmail))
            {
                score += 2;
            }

            // Check sender name for job-related terms
            if (ContainsJobKeywords(from))
            {
                score += 1;
            }

            // Check snippet/body preview for job-related content (if available)
            if (!string.IsNullOrEmpty(snippet) && ContainsJobKeywords(snippet))
            {
                score += 1;
            }

            // Check if subject starts with job-related prefixes
            if (HasJobRelatedSubjectPrefix(subject))
            {
                score += 2;
            }

            var isJobRelated = score >= 2;
            _logger.LogDebug($"Email job-related score={score} result={isJobRelated}: {subject}");
            return isJobRelated;
        }

        // Deliberately narrow: "offer"/"position"/"role" appear in too much
        // marketing mail to be strong on their own.
        private static readonly string[] _strongSubjectKeywords = { "application", "interview" };

        private bool ContainsStrongSubjectKeyword(string? subject)
        {
            if (string.IsNullOrEmpty(subject))
                return false;

            var lowerSubject = subject.ToLowerInvariant();
            return _strongSubjectKeywords.Any(k => ContainsKeyword(lowerSubject, k));
        }

        private bool ContainsJobKeywords(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            var lowerText = text.ToLowerInvariant();

            // Check for exact keyword matches
            foreach (var keyword in _jobKeywords)
            {
                if (ContainsKeyword(lowerText, keyword))
                    return true;
            }

            return false;
        }

        private bool ContainsAny(string text, string[] phrases)
        {
            foreach (var phrase in phrases)
            {
                if (text.Contains(phrase))
                    return true;
            }

            return false;
        }

        private bool ContainsKeyword(string text, string keyword)
        {
            if (keyword.Contains(' '))
            {
                return text.Contains(keyword);
            }

            return System.Text.RegularExpressions.Regex.IsMatch(
                text,
                $@"\b{System.Text.RegularExpressions.Regex.Escape(keyword)}\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
        }

        private bool IsJobRelatedDomain(string? emailAddress)
        {
            if (string.IsNullOrEmpty(emailAddress))
                return false;

            var lowerEmail = emailAddress.ToLowerInvariant();

            // Check for job-related domains
            foreach (var domain in _jobDomains)
            {
                if (lowerEmail.Contains(domain))
                    return true;
            }

            return false;
        }

        private bool HasJobRelatedSubjectPrefix(string? subject)
        {
            if (string.IsNullOrEmpty(subject))
                return false;

            var lowerSubject = subject.ToLowerInvariant().Trim();

            foreach (var prefix in _jobSubjectPrefixes)
            {
                if (lowerSubject.StartsWith(prefix))
                    return true;
            }

            return false;
        }
    }
}
