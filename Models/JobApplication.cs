using System.ComponentModel.DataAnnotations;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace JobTrackerApi.Models;

public class JobApplication
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = string.Empty;

    [BsonElement("jobNumber")]
    public int jobNumber { get; set; }

    [BsonElement("userId")]
    [Required(ErrorMessage = "User ID is required")]
    public string userId { get; set; } = null!;

    [BsonElement("jobTitle")]
    [Required(ErrorMessage = "Job title is required")]
    public string jobTitle { get; set; } = string.Empty;

    [BsonElement("company")]
    [Required(ErrorMessage = "Company name is required")]
    public string company { get; set; } = string.Empty;

    [BsonElement("status")]
    [Required(ErrorMessage = "Status is required")]
    public string status { get; set; } = "Applied";

    [BsonElement("applicationDate")]
    [Required(ErrorMessage = "Application date is required")]
    public DateTime applicationDate { get; set; } = DateTime.UtcNow;

    [BsonElement("notes")]
    public string notes { get; set; } = string.Empty;

    [BsonElement("autoStatusUpdated")]
    public bool autoStatusUpdated { get; set; } = false;

    // ===== NEW AI-POWERED FIELDS =====
    [BsonElement("recruiterName")]
    public string? RecruiterName { get; set; }

    [BsonElement("recruiterEmail")]
    public string? RecruiterEmail { get; set; }

    [BsonElement("recruiterPhone")]
    public string? RecruiterPhone { get; set; }

    [BsonElement("interviewDate")]
    public DateTime? InterviewDate { get; set; }

    [BsonElement("interviewType")]
    public string? InterviewType { get; set; } // "phone", "video", "onsite"

    [BsonElement("salaryRange")]
    public string? SalaryRange { get; set; }

    [BsonElement("emailIds")]
    public List<string>? EmailIds { get; set; } // Links to ProcessedEmail IDs

    [BsonElement("autoCreated")]
    public bool AutoCreated { get; set; } = false; // Was this created by AI?

    [BsonElement("originalCompany")]
    public string? OriginalCompany { get; set; } // Original AI-extracted company name (used for matching)

    [BsonElement("originalPosition")]
    public string? OriginalPosition { get; set; } // Original AI-extracted position (used for matching)

    [BsonElement("aiConfidence")]
    public double? AiConfidence { get; set; } // 0-100 confidence score

    [BsonElement("requiresReview")]
    public bool RequiresReview { get; set; } = false; // Needs user verification

    [BsonElement("reviewedAt")]
    public DateTime? ReviewedAt { get; set; } // When user reviewed AI-created app

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}