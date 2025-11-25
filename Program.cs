using JobTrackerApi.Models;
using JobTrackerApi.Services;
using DotNetEnv;

using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;

using Hangfire;
using Hangfire.Mongo;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;

using JobTrackerApi.Jobs; // for BackgroundEmailSyncJob
using JobTrackerApi.Middleware;
using Hangfire.Dashboard;
using MongoDB.Driver; // for UseFirebaseAuth middleware

//Loading da environment vars first
Env.Load();

var builder = WebApplication.CreateBuilder(args);

// Enable more verbose logging when running in Development to surface diagnostics
if (builder.Environment.IsDevelopment())
{
    builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug);
}

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

// Initialize Firebase Admin SDK safely
if (!string.IsNullOrEmpty(firebaseProjectId) &&
    !string.IsNullOrEmpty(firebasePrivateKey) &&
    !string.IsNullOrEmpty(firebaseClientEmail))
{
    var cleanedPrivateKey = firebasePrivateKey
        .Replace("\\n", "\n")
        .Replace("\"", "");

    var json = $@"{{
        ""type"": ""service_account"",
        ""project_id"": ""{firebaseProjectId}"",
        ""private_key"": ""{cleanedPrivateKey}"",
        ""client_email"": ""{firebaseClientEmail}""
    }}";

    // Use the static CredentialFactory (do NOT instantiate it)
    var specificCredential = Google.Apis.Auth.OAuth2.CredentialFactory
        .FromJson<Google.Apis.Auth.OAuth2.ServiceAccountCredential>(json);
    var credential = specificCredential.ToGoogleCredential();

    if (FirebaseApp.DefaultInstance == null)
    {
        FirebaseApp.Create(new AppOptions
        {
            Credential = credential
        });

        Console.WriteLine("🔥 Firebase Admin initialized using CredentialFactory.");
    }
}
else
{
    Console.WriteLine("❌ Firebase Admin NOT initialized — missing environment vars");
}


// Configure services
builder.Services.Configure<JobApplicationDatabaseSettings>(builder.Configuration.GetSection("JobApplicationDatabase"));

builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = null);

//Existing Services
builder.Services.AddSingleton<JobApplicationService>();

//Gmail integration services
builder.Services.AddSingleton<GmailAuthService>();
builder.Services.AddSingleton<GmailEmailService>();
builder.Services.AddSingleton<EmailSyncService>();

// Add AI processing services
builder.Services.AddSingleton<ClaudeEmailParserService>();
builder.Services.AddSingleton<ApplicationMatchingService>();
builder.Services.AddSingleton<EmailProcessingService>();
builder.Services.AddSingleton<RuleBasedEmailParser>();
builder.Services.AddSingleton<HybridEmailParser>();

builder.Services.AddScoped<BackgroundEmailSyncJob>();

//Hangfire configuration
builder.Services.AddHangfire(config =>
{
    config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseMongoStorage(
            connectionString,       // ✔ string
            databaseName,           // ✔ string
            new MongoStorageOptions // ✔ options as 3rd parameter
            {
                MigrationOptions = new MongoMigrationOptions
                {
                    MigrationStrategy = new MigrateMongoMigrationStrategy(),
                    BackupStrategy = new CollectionMongoBackupStrategy()
                },
                Prefix = "hangfire",
                CheckConnection = true
            }
        );
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

// app.UseHttpsRedirection();

app.UseCors("AllowAll");

app.UseFirebaseAuth();

app.UseAuthorization();

app.MapControllers();

var syncIntervalMinutes = int.Parse(
    Environment.GetEnvironmentVariable("EMAIL_SYNC_INTERVAL_MINUTES") ?? "15"
);

// Resolve Hangfire job manager from DI
var recurringJobManager = app.Services.GetRequiredService<IRecurringJobManager>();

recurringJobManager.AddOrUpdate<BackgroundEmailSyncJob>(
    "email-sync-job",
    job => job.ExecuteAsync(),
    $"*/{syncIntervalMinutes} * * * *"
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