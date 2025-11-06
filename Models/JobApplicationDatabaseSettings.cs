namespace JobTrackerApi.Models;

public class JobApplicationDatabaseSettings
{
    public string ConnectionString { get; set; } = null!;

    public string DatabaseName { get; set; } = null!;

    public string JobApplicationCollectionName { get; set; } = null!;
}