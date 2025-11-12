using JobTrackerApi.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace JobTrackerApi.Services
{
    public class JobApplicationService
    {
        private readonly IMongoCollection<JobApplication> _jobApplicationCollection;

        public JobApplicationService(IOptions<JobApplicationDatabaseSettings> dbSettings)
        {
            var settings = dbSettings.Value;

            // Use values loaded from .env (via Program.cs)
            var mongoClient = new MongoClient(settings.ConnectionString);
            var mongoDatabase = mongoClient.GetDatabase(settings.DatabaseName);

            _jobApplicationCollection = mongoDatabase.GetCollection<JobApplication>(
                settings.JobApplicationCollectionName
            );
        }

        // Get all job applications (sorted by date)
        public async Task<List<JobApplication>> GetAsync() =>
            await _jobApplicationCollection
                .Find(_ => true)
                .SortByDescending(x => x.applicationDate)
                .ToListAsync();

        // Get all job applications for a specific user (sorted by date)
        public async Task<List<JobApplication>> GetByUserAsync(string userId) =>
            await _jobApplicationCollection
                .Find(x => x.userId == userId)
                .SortByDescending(x => x.applicationDate)
                .ToListAsync();

        // Get specific job application by ID
        public async Task<JobApplication?> GetAsync(string id) =>
            await _jobApplicationCollection
                .Find(x => x.Id == id)
                .FirstOrDefaultAsync();

        // Create new job application with auto-incrementing jobNumber
        public async Task CreateAsync(JobApplication newJobApplication)
        {
            var lastApplication = await _jobApplicationCollection
                .Find(x => x.userId == newJobApplication.userId)
                .SortByDescending(x => x.jobNumber)
                .FirstOrDefaultAsync();

            newJobApplication.jobNumber = lastApplication != null
                ? lastApplication.jobNumber + 1
                : 1;

            await _jobApplicationCollection.InsertOneAsync(newJobApplication);
        }

        // Update job application
        public async Task UpdateAsync(string id, JobApplication updatedJobApplication) =>
            await _jobApplicationCollection.ReplaceOneAsync(
                x => x.Id == id,
                updatedJobApplication
            );

        // Update status only (more efficient)
        public async Task UpdateStatusAsync(string id, string status, bool autoStatusUpdated = false)
        {
            var update = Builders<JobApplication>.Update
                .Set(x => x.status, status)
                .Set(x => x.autoStatusUpdated, autoStatusUpdated);

            await _jobApplicationCollection.UpdateOneAsync(x => x.Id == id, update);
        }

        // Remove specific job application
        public async Task RemoveAsync(string id) =>
            await _jobApplicationCollection.DeleteOneAsync(x => x.Id == id);

        // Remove all job applications for a user (returns count)
        public async Task<long> RemoveAllForUserAsync(string userId)
        {
            var result = await _jobApplicationCollection.DeleteManyAsync(x => x.userId == userId);
            return result.DeletedCount;
        }
    }
}
