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
}