using System.Text.RegularExpressions;
using JobTrackerApi.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace JobTrackerApi.Services
{
    public class ApplicationMatchingService
    {
        private readonly IMongoCollection<JobApplication> _jobApplicationCollection;
        private readonly ILogger<ApplicationMatchingService> _logger;

        // Descriptor / legal tokens stripped before comparing company names, so
        // "Rito Tech", "RITO Technologies", and "Rito Technologies Ltd" all reduce
        // to the same base ("rito"). Kept deliberately broad — see NormalizeCompany.
        private static readonly HashSet<string> CompanyNoiseTokens = new(StringComparer.Ordinal)
        {
            "inc", "llc", "ltd", "limited", "corp", "corporation", "co", "company",
            "plc", "gmbh", "sarl", "bv", "pty", "group", "holdings", "technologies",
            "technology", "tech", "solutions", "software", "systems", "labs", "lab",
            "consulting", "services", "global", "international", "worldwide", "digital",
            "studios", "studio", "the", "team"
        };

        // Free-mail / job-board / ATS domains that do NOT identify an employer, so
        // a recruiter address on one of these is not treated as a same-employer signal.
        private static readonly HashSet<string> NonEmployerDomains = new(StringComparer.OrdinalIgnoreCase)
        {
            "gmail.com", "yahoo.com", "outlook.com", "hotmail.com", "live.com", "icloud.com",
            "linkedin.com", "indeed.com", "glassdoor.com", "ziprecruiter.com", "monster.com",
            "careerbuilder.com", "dice.com", "pnet.co.za", "greenhouse.io", "lever.co",
            "workday.com", "myworkdayjobs.com", "icims.com", "smartrecruiters.com",
            "bamboohr.com", "jobvite.com", "broadbean.net"
        };

        // Public-suffix-ish second-level labels, so co.za / co.uk / com.au collapse to
        // the registrable label one further left (rito-tech.co.za -> "rito-tech").
        private static readonly HashSet<string> SecondLevelDomainLabels = new(StringComparer.Ordinal)
        {
            "co", "com", "org", "net", "gov", "edu", "ac"
        };

        public ApplicationMatchingService(
            IOptions<JobApplicationDatabaseSettings> dbSettings,
            ILogger<ApplicationMatchingService> logger)
        {
            _logger = logger;

            var settings = dbSettings.Value;
            var mongoClient = new MongoClient(settings.ConnectionString);
            var mongoDatabase = mongoClient.GetDatabase(settings.DatabaseName);
            _jobApplicationCollection = mongoDatabase.GetCollection<JobApplication>(
                settings.JobApplicationCollectionName
            );
        }

        // Find an existing application this email belongs to, or null to create a new one.
        // Matching uses three signals, strongest first:
        //   1. Gmail thread id  — a reply/follow-up in the same thread is the same application.
        //   2. employer + position (normalized) — same role at the same company.
        //   3. employer only, within 90 days — for status-update emails on an existing app.
        // "Employer" matches on a normalized company name OR a shared recruiter email domain,
        // which is what bridges inconsistent extractions like "Rito Tech" vs "RITO Technologies".
        public async Task<JobApplication?> FindMatchingApplicationAsync(
            ProcessedEmail email,
            EmailExtractedData extractedData)
        {
            var userId = email.UserId;
            var company = extractedData.CompanyName?.Trim() ?? "";

            var hasUsableCompany =
                !string.IsNullOrEmpty(company) &&
                !company.Equals("Unknown Company", StringComparison.OrdinalIgnoreCase) &&
                !company.StartsWith("Recruitment Agency", StringComparison.OrdinalIgnoreCase);

            var threadId = email.ThreadId;
            var recruiterDomain = EmailDomainBase(extractedData.RecruiterEmail ?? email.FromEmail);

            // No usable signal at all — don't guess a match.
            if (!hasUsableCompany && string.IsNullOrEmpty(threadId) && string.IsNullOrEmpty(recruiterDomain))
            {
                return null;
            }

            // Load this user's applications once (indexed by userId; per-user volume is small,
            // so in-memory normalized comparison is cheaper and clearer than a Mongo regex
            // that can't express the normalization).
            var apps = await _jobApplicationCollection
                .Find(x => x.userId == userId)
                .SortByDescending(x => x.applicationDate)
                .ToListAsync();

            if (apps.Count == 0)
            {
                return null;
            }

            // 1. Thread match — strongest signal.
            if (!string.IsNullOrEmpty(threadId))
            {
                var byThread = apps.FirstOrDefault(a =>
                    a.ThreadIds != null && a.ThreadIds.Contains(threadId));
                if (byThread != null)
                {
                    _logger.LogInformation(
                        "Matched application {Id} by Gmail thread {ThreadId}", byThread.Id, threadId);
                    return byThread;
                }
            }

            var normCompany = NormalizeCompany(company);
            var normPosition = NormalizePosition(extractedData.Position);

            bool SameEmployer(JobApplication a)
            {
                if (!string.IsNullOrEmpty(normCompany))
                {
                    if (NormalizeCompany(a.company) == normCompany) return true;
                    if (NormalizeCompany(a.OriginalCompany) == normCompany) return true;
                }

                if (!string.IsNullOrEmpty(recruiterDomain))
                {
                    var aDomain = EmailDomainBase(a.RecruiterEmail);
                    if (!string.IsNullOrEmpty(aDomain) && aDomain == recruiterDomain) return true;
                }

                return false;
            }

            bool SamePosition(JobApplication a)
            {
                if (string.IsNullOrEmpty(normPosition)) return false;
                return PositionsMatch(normPosition, NormalizePosition(a.jobTitle)) ||
                       PositionsMatch(normPosition, NormalizePosition(a.OriginalPosition));
            }

            // 2. Same employer AND same position.
            var exact = apps.FirstOrDefault(a => SameEmployer(a) && SamePosition(a));
            if (exact != null)
            {
                _logger.LogInformation(
                    "Matched application {Id} by employer + position for {Company} - {Position}",
                    exact.Id, company, extractedData.Position);
                return exact;
            }

            // 3. Same employer within 90 days (most recent) — for status updates.
            var cutoff = DateTime.UtcNow.AddDays(-90);
            var fuzzy = apps.FirstOrDefault(a => a.applicationDate >= cutoff && SameEmployer(a));
            if (fuzzy != null)
            {
                _logger.LogInformation(
                    "Matched application {Id} by employer (recent) for {Company}", fuzzy.Id, company);
                return fuzzy;
            }

            _logger.LogInformation(
                "No matching application for {Company} - {Position}", company, extractedData.Position);
            return null;
        }

        // Reduce a company name to a comparable base: lowercase, drop parentheticals
        // ("(via LinkedIn)"), split on non-alphanumerics, and strip legal/descriptor
        // tokens (Inc, Ltd, Technologies, Tech, Solutions, ...). If stripping leaves
        // nothing (e.g. a company literally named "Tech Solutions"), fall back to the
        // full token set so distinct names don't all collapse to an empty key.
        private static string NormalizeCompany(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";

            var lower = name.ToLowerInvariant();
            lower = Regex.Replace(lower, @"\(.*?\)", " ");

            var allTokens = Regex.Split(lower, @"[^a-z0-9]+")
                .Where(t => t.Length > 0)
                .ToList();
            if (allTokens.Count == 0) return "";

            var significant = allTokens.Where(t => !CompanyNoiseTokens.Contains(t)).ToList();
            var chosen = significant.Count > 0 ? significant : allTokens;
            return string.Concat(chosen);
        }

        // Lowercase, expand sr./jr., strip punctuation, concatenate. Seniority is kept
        // so "Junior Developer" and "Senior Developer" do NOT collapse together.
        private static string NormalizePosition(string? position)
        {
            if (string.IsNullOrWhiteSpace(position)) return "";

            var lower = position.ToLowerInvariant();
            var tokens = Regex.Split(lower, @"[^a-z0-9]+")
                .Where(t => t.Length > 0)
                .Select(t => t switch
                {
                    "sr" => "senior",
                    "jr" => "junior",
                    _ => t
                });

            return string.Concat(tokens);
        }

        private static bool PositionsMatch(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (a == b) return true;
            // Containment either direction, but only for non-trivial strings so a tiny
            // token doesn't match everything.
            if (a.Length >= 5 && b.Length >= 5 && (a.Contains(b) || b.Contains(a))) return true;
            return false;
        }

        // Registrable base label of an email's domain, or "" for free-mail / job-board /
        // ATS domains (which don't identify an employer). rito-tech.com and
        // rito-tech.co.za both yield "rito-tech".
        private static string EmailDomainBase(string? email)
        {
            if (string.IsNullOrWhiteSpace(email)) return "";

            var at = email.LastIndexOf('@');
            if (at < 0 || at == email.Length - 1) return "";

            var domain = email[(at + 1)..].ToLowerInvariant().Trim();
            if (NonEmployerDomains.Any(d => domain == d || domain.EndsWith("." + d))) return "";

            var labels = domain.Split('.').Where(l => l.Length > 0).ToList();
            if (labels.Count < 2) return "";

            var baseIdx = labels.Count - 2;
            if (labels.Count >= 3 && SecondLevelDomainLabels.Contains(labels[labels.Count - 2]))
            {
                baseIdx = labels.Count - 3;
            }

            return labels[baseIdx];
        }

        // Check if we should create a new application or update an existing one.
        public bool ShouldCreateNew(
            JobApplication? existingApp,
            EmailExtractedData extractedData,
            ProcessedEmail email)
        {
            // No existing application found
            if (existingApp == null)
            {
                return true;
            }

            // Same Gmail thread = same application — always update, never duplicate.
            if (existingApp.ThreadIds != null &&
                !string.IsNullOrEmpty(email.ThreadId) &&
                existingApp.ThreadIds.Contains(email.ThreadId))
            {
                return false;
            }

            // Email is about rejection/interview/offer - update existing
            if (extractedData.ApplicationStatus == "Rejected" ||
                extractedData.ApplicationStatus == "Interview Scheduled" ||
                extractedData.ApplicationStatus == "Offer")
            {
                return false;
            }

            // If existing application and new application confirmation - might be duplicate
            // Check if email date is within 7 days of application date
            var daysDifference = Math.Abs((email.Date - existingApp.applicationDate).TotalDays);
            if (daysDifference <= 7 && extractedData.ApplicationStatus == "Applied")
            {
                return false; // Likely a duplicate confirmation email
            }

            // Different enough - create new application
            return true;
        }
    }
}
