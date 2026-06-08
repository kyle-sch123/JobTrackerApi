using System.Text.RegularExpressions;
using JobTrackerApi.Models;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace JobTrackerApi.Services
{
    public class ApplicationMatchingService
    {
        private readonly IMongoCollection<JobApplication> _jobApplicationCollection;
        private readonly ILogger<ApplicationMatchingService> _logger;

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

        // Find existing application or return null
        public async Task<JobApplication?> FindMatchingApplicationAsync(
            string userId, 
            EmailExtractedData extractedData)
        {
            if (string.IsNullOrEmpty(extractedData.CompanyName))
            {
                return null;
            }
            
            var companyName = extractedData.CompanyName.Trim();
            if (companyName.Equals("Unknown Company", StringComparison.OrdinalIgnoreCase) ||
                companyName.StartsWith("Recruitment Agency", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Strategy 1: Exact match on company and position
            var exactMatch = await FindExactMatch(userId, extractedData);
            if (exactMatch != null)
            {
                _logger.LogInformation(
                    $"Found exact match for {extractedData.CompanyName} - {extractedData.Position}"
                );
                return exactMatch;
            }

            // Strategy 2: Fuzzy match on company name only (for status updates)
            var fuzzyMatch = await FindFuzzyMatch(userId, extractedData);
            if (fuzzyMatch != null)
            {
                _logger.LogInformation(
                    $"Found fuzzy match for {extractedData.CompanyName}"
                );
                return fuzzyMatch;
            }

            _logger.LogInformation(
                $"No matching application found for {extractedData.CompanyName} - {extractedData.Position}"
            );
            return null;
        }

        private async Task<JobApplication?> FindExactMatch(
            string userId,
            EmailExtractedData extractedData)
        {
            var companyVariations = GenerateCompanyVariations(extractedData.CompanyName ?? "");
            var positionVariations = GeneratePositionVariations(extractedData.Position ?? "");

            // Narrow the query to candidates whose current OR original company
            // matches a variation, rather than loading the user's whole history.
            var filter = BuildUserCompanyFilter(userId, companyVariations);
            var applications = await _jobApplicationCollection.Find(filter).ToListAsync();

            // Company already matched in the query; only the position needs checking.
            return applications.FirstOrDefault(app =>
                positionVariations.Any(variation =>
                    app.jobTitle.Contains(variation, StringComparison.OrdinalIgnoreCase) ||
                    variation.Contains(app.jobTitle, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(app.OriginalPosition) &&
                 positionVariations.Any(variation =>
                    app.OriginalPosition.Contains(variation, StringComparison.OrdinalIgnoreCase) ||
                    variation.Contains(app.OriginalPosition, StringComparison.OrdinalIgnoreCase)))
            );
        }

        private async Task<JobApplication?> FindFuzzyMatch(
            string userId,
            EmailExtractedData extractedData)
        {
            var companyVariations = GenerateCompanyVariations(extractedData.CompanyName ?? "");

            // Most recent application to this company within the last 90 days.
            var cutoffDate = DateTime.UtcNow.AddDays(-90);

            var filter = BuildUserCompanyFilter(userId, companyVariations, cutoffDate);
            var applications = await _jobApplicationCollection
                .Find(filter)
                .SortByDescending(x => x.applicationDate)
                .ToListAsync();

            return applications.FirstOrDefault();
        }

        // Builds a Mongo filter: userId AND (company OR originalCompany matches any
        // variation, case-insensitive exact), optionally constrained to a date.
        private static FilterDefinition<JobApplication> BuildUserCompanyFilter(
            string userId,
            List<string> companyVariations,
            DateTime? since = null)
        {
            var f = Builders<JobApplication>.Filter;

            var companyClauses = new List<FilterDefinition<JobApplication>>();
            foreach (var variation in companyVariations)
            {
                if (string.IsNullOrWhiteSpace(variation)) continue;

                var rx = new BsonRegularExpression($"^{Regex.Escape(variation)}$", "i");
                companyClauses.Add(f.Regex(x => x.company, rx));
                companyClauses.Add(f.Regex(x => x.OriginalCompany, rx));
            }

            var filter = f.Eq(x => x.userId, userId);

            if (companyClauses.Count > 0)
            {
                filter = f.And(filter, f.Or(companyClauses));
            }

            if (since.HasValue)
            {
                filter = f.And(filter, f.Gte(x => x.applicationDate, since.Value));
            }

            return filter;
        }

        private List<string> GenerateCompanyVariations(string companyName)
        {
            var variations = new List<string> { companyName };

            // Common variations
            var commonSuffixes = new[] { " Inc", " Inc.", " LLC", " Ltd", " Ltd.", " Corporation", " Corp", " Corp." };
            var baseName = companyName;

            // Remove suffixes to get base name
            foreach (var suffix in commonSuffixes)
            {
                if (baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    baseName = baseName.Substring(0, baseName.Length - suffix.Length).Trim();
                    variations.Add(baseName);
                }
            }

            // Add variations with suffixes
            foreach (var suffix in commonSuffixes)
            {
                variations.Add(baseName + suffix);
            }

            // Common company name mappings
            var mappings = new Dictionary<string, string[]>
            {
                { "Google", new[] { "Alphabet", "Google LLC" } },
                { "Facebook", new[] { "Meta", "Meta Platforms" } },
                { "Amazon", new[] { "Amazon.com", "AWS" } },
                { "Microsoft", new[] { "MSFT" } }
            };

            foreach (var mapping in mappings)
            {
                if (baseName.Equals(mapping.Key, StringComparison.OrdinalIgnoreCase))
                {
                    variations.AddRange(mapping.Value);
                }
                else if (mapping.Value.Any(v => v.Equals(baseName, StringComparison.OrdinalIgnoreCase)))
                {
                    variations.Add(mapping.Key);
                    variations.AddRange(mapping.Value);
                }
            }

            return variations.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private List<string> GeneratePositionVariations(string position)
        {
            var variations = new List<string> { position };

            // Common title variations
            var titleMappings = new Dictionary<string, string[]>
            {
                { "Software Engineer", new[] { "SWE", "Software Developer", "Engineer" } },
                { "Senior Software Engineer", new[] { "Senior SWE", "Sr. Software Engineer", "Senior Engineer" } },
                { "Full Stack", new[] { "Fullstack", "Full-Stack" } },
                { "Front End", new[] { "Frontend", "Front-End" } },
                { "Back End", new[] { "Backend", "Back-End" } }
            };

            foreach (var mapping in titleMappings)
            {
                if (position.Contains(mapping.Key, StringComparison.OrdinalIgnoreCase))
                {
                    variations.AddRange(mapping.Value);
                }
                else if (mapping.Value.Any(v => position.Contains(v, StringComparison.OrdinalIgnoreCase)))
                {
                    variations.Add(mapping.Key);
                    variations.AddRange(mapping.Value);
                }
            }

            return variations.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Check if we should create a new application or update existing one
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

            // Email is about rejection/interview - update existing
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
