using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace JobTrackerApi.Models
{
    public class ProcessedEmail
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonElement("userId")]
        public string UserId { get; set; } = null!;

        [BsonElement("gmailMessageId")]
        public string GmailMessageId { get; set; } = null!; // Gmail's message ID

        [BsonElement("threadId")]
        public string? ThreadId { get; set; }

        [BsonElement("subject")]
        public string Subject { get; set; } = null!;

        [BsonElement("from")]
        public string From { get; set; } = null!;

        [BsonElement("fromEmail")]
        public string FromEmail { get; set; } = null!;

        [BsonElement("to")]
        public string? To { get; set; }

        [BsonElement("date")]
        public DateTime Date { get; set; }

        [BsonElement("snippet")]
        public string? Snippet { get; set; } // Gmail's short preview

        [BsonElement("bodyPlainText")]
        public string? BodyPlainText { get; set; }

        [BsonElement("bodyHtml")]
        public string? BodyHtml { get; set; }

        [BsonElement("labels")]
        public List<string> Labels { get; set; } = new();

        [BsonElement("isJobRelated")]
        public bool IsJobRelated { get; set; } = false;

        [BsonElement("jobApplicationId")]
        public string? JobApplicationId { get; set; } // Link to JobApplication if created

        [BsonElement("processedAt")]
        public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;

        [BsonElement("processingStatus")]
        public string ProcessingStatus { get; set; } = "pending"; // "pending", "processed", "failed", "ignored"

        [BsonElement("aiParsed")]
        public bool AiParsed { get; set; } = false; // Will be true in Phase 2

        [BsonElement("extractedData")]
        public EmailExtractedData? ExtractedData { get; set; }

        [BsonElement("extractionMethod")]
        public string? ExtractionMethod { get; set; }
    }

    public class EmailExtractedData
    {
        [BsonElement("companyName")]
        public string? CompanyName { get; set; }

        [BsonElement("position")]
        public string? Position { get; set; }

        [BsonElement("applicationStatus")]
        public string? ApplicationStatus { get; set; }

        [BsonElement("interviewDate")]
        public DateTime? InterviewDate { get; set; }

        [BsonElement("interviewType")]
        public string? InterviewType { get; set; }

        [BsonElement("recruiterName")]
        public string? RecruiterName { get; set; }

        [BsonElement("recruiterEmail")]
        public string? RecruiterEmail { get; set; }

        [BsonElement("jobUrl")]
        public string? JobUrl { get; set; }

        [BsonElement("salaryRange")]
        public string? SalaryRange { get; set; }

        [BsonElement("confidence")]
        public double Confidence { get; set; } = 0.0; // AI confidence score

        [BsonElement("extractionMethod")]
        public string ExtractionMethod { get; set; } = "unknown";

        public bool IsJobApplication { get; set; } = true;
        public string? Description { get; set; }
    }
}