using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace JobTrackerApi.Models
{
    public class UserEmailConnection
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonElement("userId")]
        public string UserId { get; set; } = null!; // Firebase UID

        [BsonElement("email")]
        public string Email { get; set; } = null!; // Gmail address

        [BsonElement("accessToken")]
        public string AccessToken { get; set; } = null!;

        [BsonElement("refreshToken")]
        public string RefreshToken { get; set; } = null!;

        [BsonElement("tokenExpiry")]
        public DateTime TokenExpiry { get; set; }

        [BsonElement("scopes")]
        public List<string> Scopes { get; set; } = new();

        [BsonElement("isActive")]
        public bool IsActive { get; set; } = true;

        [BsonElement("connectedAt")]
        public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;

        [BsonElement("lastSyncAt")]
        public DateTime? LastSyncAt { get; set; }

        [BsonElement("lastSyncStatus")]
        public string? LastSyncStatus { get; set; } // "success", "failed", "partial"

        [BsonElement("syncErrorMessage")]
        public string? SyncErrorMessage { get; set; }
    }
}