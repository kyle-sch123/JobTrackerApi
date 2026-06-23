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

var firebaseConfigured = !string.IsNullOrEmpty(firebaseProjectId) &&
    !string.IsNullOrEmpty(firebasePrivateKey) &&
    !string.IsNullOrEmpty(firebaseClientEmail);
var firebaseInitialized = false;

// Add them to configuration so they can be used like appsettings.json values
builder.Configuration["JobApplicationDatabase:ConnectionString"] = connectionString;
builder.Configuration["JobApplicationDatabase:DatabaseName"] = databaseName;
builder.Configuration["JobApplicationDatabase:JobApplicationCollectionName"] = jobCollectionName;
builder.Configuration["JobApplicationDatabase:UserEmailConnectionCollectionName"] = userEmailConnectionCollectionName;
builder.Configuration["JobApplicationDatabase:EmailSyncHistoryCollectionName"] = emailSyncHistoryCollectionName;
builder.Configuration["JobApplicationDatabase:ProcessedEmailCollectionName"] = processedEmailCollectionName;

// Initialize Firebase Admin SDK safely
if (firebaseConfigured)
{
    var cleanedPrivateKey = firebasePrivateKey.Replace("\\n", "\n");

    // Trim a single pair of surrounding quotes if the env value was quoted.
    // (The old code stripped EVERY quote, which silently corrupts any key whose
    // value wasn't wrapped in quotes.)
    if (cleanedPrivateKey.Length >= 2 &&
        cleanedPrivateKey.StartsWith("\"") && cleanedPrivateKey.EndsWith("\""))
    {
        cleanedPrivateKey = cleanedPrivateKey[1..^1];
    }

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

    }
    firebaseInitialized = FirebaseApp.DefaultInstance != null;
}


// Configure services
builder.Services.Configure<JobApplicationDatabaseSettings>(builder.Configuration.GetSection("JobApplicationDatabase"));

builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = null);

// IHttpClientFactory for pooled HttpClient instances (avoids socket exhaustion)
builder.Services.AddHttpClient();

// Named client for the Anthropic Messages API, pre-configured with auth headers.
builder.Services.AddHttpClient("claude", client =>
{
    var claudeApiKey = Environment.GetEnvironmentVariable("CLAUDE_API_KEY");
    if (!string.IsNullOrEmpty(claudeApiKey))
    {
        client.DefaultRequestHeaders.Add("x-api-key", claudeApiKey);
    }
    client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
});

// Centralised confidence thresholds, bound once from env and injected everywhere
// instead of each service re-reading environment variables on every call.
var autoThreshold = double.Parse(Environment.GetEnvironmentVariable("AI_CONFIDENCE_THRESHOLD_AUTO") ?? "70");
var reviewThreshold = double.Parse(Environment.GetEnvironmentVariable("AI_CONFIDENCE_THRESHOLD_REVIEW") ?? "40");
builder.Services.AddSingleton(new ConfidenceThresholds { Auto = autoThreshold, Review = reviewThreshold });

// Data protection (encrypts Gmail OAuth tokens at rest) and an in-memory cache
// (holds OAuth state tokens for CSRF verification during the consent flow).
builder.Services.AddDataProtection();
builder.Services.AddMemoryCache();

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
builder.Services.AddSingleton<JobRelatedEmailFilter>();

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

// Bind to ASPNETCORE_URLS when provided (e.g. the container sets http://+:8080),
// otherwise fall back to the local-dev default.
var aspnetUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
builder.WebHost.UseUrls(
    !string.IsNullOrWhiteSpace(aspnetUrls) ? aspnetUrls : "http://0.0.0.0:5000"
);

// Restrict CORS to the configured frontend origins. Falls back to AllowAnyOrigin
// only when no origins are configured (local dev convenience).
var allowedOrigins = new[]
{
    Environment.GetEnvironmentVariable("FRONTEND_URL_DEV"),
    Environment.GetEnvironmentVariable("FRONTEND_URL_PROD")
}
.Where(o => !string.IsNullOrWhiteSpace(o))
.Select(o => o!)
.ToArray();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
        else
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
    });
});

var app = builder.Build();

if (!firebaseConfigured)
{
    app.Logger.LogWarning("Firebase Admin not initialized (missing environment variables).");
}
else if (firebaseInitialized)
{
    app.Logger.LogInformation("Firebase Admin initialized.");
}

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

app.UseCors("AllowFrontend");

app.UseFirebaseAuth();

app.UseAuthorization();

app.MapControllers();

// Lightweight, unauthenticated liveness probe for the load balancer / uptime
// checks. Auth is skipped for "/health*" in FirebaseAuthMiddleware.
app.MapGet("/healthz", () => Results.Ok(new
{
    status = "ok",
    timestamp = DateTime.UtcNow
}));

// Ensure MongoDB indexes exist for the hot query paths (idempotent; safe to run
// every startup). Without these, lookups by (user, message id) / (user, status)
// fall back to full collection scans.
try
{
    var indexClient = new MongoClient(connectionString);
    var indexDb = indexClient.GetDatabase(databaseName);

    var processedEmails = indexDb.GetCollection<ProcessedEmail>(processedEmailCollectionName);
    await processedEmails.Indexes.CreateManyAsync(new[]
    {
        new CreateIndexModel<ProcessedEmail>(
            Builders<ProcessedEmail>.IndexKeys
                .Ascending(e => e.UserId)
                .Ascending(e => e.GmailMessageId),
            new CreateIndexOptions { Unique = true, Name = "ux_user_gmailMessageId" }),
        new CreateIndexModel<ProcessedEmail>(
            Builders<ProcessedEmail>.IndexKeys
                .Ascending(e => e.UserId)
                .Ascending(e => e.ProcessingStatus),
            new CreateIndexOptions { Name = "ix_user_processingStatus" })
    });

    var connections = indexDb.GetCollection<UserEmailConnection>(userEmailConnectionCollectionName);
    await connections.Indexes.CreateOneAsync(
        new CreateIndexModel<UserEmailConnection>(
            Builders<UserEmailConnection>.IndexKeys
                .Ascending(c => c.UserId)
                .Ascending(c => c.IsActive),
            new CreateIndexOptions { Name = "ix_user_isActive" }));

    var jobApplications = indexDb.GetCollection<JobApplication>(jobCollectionName);
    await jobApplications.Indexes.CreateOneAsync(
        new CreateIndexModel<JobApplication>(
            Builders<JobApplication>.IndexKeys.Ascending(j => j.userId),
            new CreateIndexOptions { Name = "ix_userId" }));

    app.Logger.LogInformation("MongoDB indexes ensured.");
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Failed to ensure MongoDB indexes (continuing startup).");
}

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
