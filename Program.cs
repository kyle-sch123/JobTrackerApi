using JobTrackerApi.Models;
using JobTrackerApi.Services;
using DotNetEnv;

// Load environment variables FIRST, before creating the builder
Env.Load();

var builder = WebApplication.CreateBuilder(args);

// Get environment variables
var connectionString = Environment.GetEnvironmentVariable("ConnectionString");
var databaseName = Environment.GetEnvironmentVariable("DatabaseName");
var jobCollectionName = Environment.GetEnvironmentVariable("JobApplicationCollectionName");
var userEmailConnectionCollectionName = Environment.GetEnvironmentVariable("UserEmailConnectionCollectionName");
var emailSyncHistoryCollectionName = Environment.GetEnvironmentVariable("EmailSyncHistoryCollectionName");
var processedEmailCollectionName = Environment.GetEnvironmentVariable("ProcessedEmailCollectionName");

// Firebase configuration
var firebaseProjectId = Environment.GetEnvironmentVariable("FIREBASE_PROJECT_ID");
var firebasePrivateKey = Environment.GetEnvironmentVariable("FIREBASE_PRIVATE_KEY");
var firebaseClientEmail = Environment.GetEnvironmentVariable("FIREBASE_CLIENT_EMAIL");

// Add them to configuration so they can be used like appsettings.json values
builder.Configuration["JobApplicationDatabase:ConnectionString"] = connectionString;
builder.Configuration["JobApplicationDatabase:DatabaseName"] = databaseName;
builder.Configuration["JobApplicationDatabase:JobApplicationCollectionName"] = jobCollectionName;
builder.Configuration["JobApplicationDatabase:UserEmailConnectionCollectionName"] = userEmailConnectionCollectionName;
builder.Configuration["JobApplicationDatabase:EmailSyncHistoryCollectionName"] = emailSyncHistoryCollectionName;
builder.Configuration["JobApplicationDatabase:ProcessedEmailCollectionName"] = processedEmailCollectionName;

// Initialize Firebase Admin SDK
if (!string.IsNullOrEmpty(firebaseProjectId) && 
    !string.IsNullOrEmpty(firebasePrivateKey) && 
    !string.IsNullOrEmpty(firebaseClientEmail))
{
    FirebaseApp.Create(new AppOptions
    {
        Credential = GoogleCredential.FromJson($@"{{
            ""type"": ""service_account"",
            ""project_id"": ""{firebaseProjectId}"",
            ""private_key"": ""{firebasePrivateKey.Replace("\\n", "\n")}"",
            ""client_email"": ""{firebaseClientEmail}""
        }}")
    });
}

// Add services to the container.
builder.Services.Configure<JobApplicationDatabaseSettings>(builder.Configuration.GetSection("JobApplicationDatabase"));

builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = null);

builder.Services.AddSingleton<JobApplicationService>();
builder.Services.AddSingleton<GmailAuthService>();
builder.Services.AddSingleton<GmailEmailService>();
builder.Services.AddSingleton<EmailSyncService>();

builder.Services.AddScoped<BackgroundEmailSyncJob>();

//Hangfire configuration
builder.Services.AddHangfire(config =>
{
    var mongoUrlBuilder = new MongoUrlBuilder(connectionString)
    {
        DatabaseName = databaseName
    };
    
    config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseMongoStorage(mongoUrlBuilder.ToMongoUrl(), new MongoStorageOptions
        {
            MigrationOptions = new MongoMigrationOptions
            {
                MigrationStrategy = new MigrateMongoMigrationStrategy(),
                BackupStrategy = new CollectionMongoBackupStrategy()
            },
            Prefix = "hangfire",
            CheckConnection = true
        });
});

builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 1; // Adjust based on your needs
});


// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.WebHost.UseUrls("http://0.0.0.0:5000");

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        policy => policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader());
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUi(options =>
    {
        options.DocumentPath = "/openapi/v1.json";
    });
}

app.UseHttpsRedirection();

app.UseCors("AllowAll");

app.UseFirebaseAuth();

app.UseAuthorization();

app.MapControllers();

// Schedule recurring job for email sync
var syncIntervalMinutes = int.Parse(Environment.GetEnvironmentVariable("EMAIL_SYNC_INTERVAL_MINUTES") ?? "15");
RecurringJob.AddOrUpdate<BackgroundEmailSyncJob>(
    "email-sync-job",
    job => job.ExecuteAsync(),
    $"*/{syncIntervalMinutes} * * * *" // Every X minutes
);

app.Run();

// Basic Hangfire dashboard authorization filter
public class HangfireAuthorizationFilter : Hangfire.Dashboard.IDashboardAuthorizationFilter
{
    private readonly string _username;
    private readonly string _password;

    public HangfireAuthorizationFilter(string username, string password)
    {
        _username = username;
        _password = password;
    }

    public bool Authorize(Hangfire.Dashboard.DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        var authHeader = httpContext.Request.Headers["Authorization"].FirstOrDefault();

        if (authHeader != null && authHeader.StartsWith("Basic "))
        {
            var encodedCredentials = authHeader.Substring("Basic ".Length).Trim();
            var credentials = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(encodedCredentials)
            ).Split(':');

            var username = credentials[0];
            var password = credentials[1];

            return username == _username && password == _password;
        }

        httpContext.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Hangfire Dashboard\"";
        httpContext.Response.StatusCode = 401;
        return false;
    }
}