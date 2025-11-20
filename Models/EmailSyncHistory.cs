using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace JobTrackerApi.Models
{
    public class EmailSyncHistory
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonElement("userId")]
        public string UserId { get; set; } = null!;

        [BsonElement("syncStartedAt")]
        public DateTime SyncStartedAt { get; set; } = DateTime.UtcNow;

        [BsonElement("syncCompletedAt")]
        public DateTime? SyncCompletedAt { get; set; }

        [BsonElement("status")]
        public string Status { get; set; } = "running"; // "running", "completed", "failed"

        [BsonElement("emailsProcessed")]
        public int EmailsProcessed { get; set; } = 0;

        [BsonElement("emailsSkipped")]
        public int EmailsSkipped { get; set; } = 0;

        [BsonElement("emailsFailed")]
        public int EmailsFailed { get; set; } = 0;

        [BsonElement("emailsParsed")]
        public int EmailsParsed { get; set; } = 0;

        [BsonElement("errors")]
        public List<string> Errors { get; set; } = new();

        [BsonElement("triggerType")]
        public string TriggerType { get; set; } = "scheduled"; // "scheduled", "manual"
    }
}