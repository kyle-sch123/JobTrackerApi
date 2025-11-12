using JobTrackerApi.Models;
using JobTrackerApi.Services;
using DotNetEnv;

// Load environment variables FIRST, before creating the builder
Env.Load();

var builder = WebApplication.CreateBuilder(args);

// Now you can access them using Environment.GetEnvironmentVariable()
var connectionString = Environment.GetEnvironmentVariable("ConnectionString");
var databaseName = Environment.GetEnvironmentVariable("DatabaseName");
var jobCollectionName = Environment.GetEnvironmentVariable("JobApplicationCollectionName");





// Add them to configuration so they can be used like appsettings.json values
builder.Configuration["JobApplicationDatabase:ConnectionString"] = connectionString;
builder.Configuration["JobApplicationDatabase:DatabaseName"] = databaseName;
builder.Configuration["JobApplicationDatabase:JobApplicationCollectionName"] = jobCollectionName;

// Add services to the container.
builder.Services.Configure<JobApplicationDatabaseSettings>(builder.Configuration.GetSection("JobApplicationDatabase"));
builder.Services.AddControllers().AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = null);

builder.Services.AddSingleton<JobApplicationService>();

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

app.UseAuthorization();

app.MapControllers();

app.Run();
// ```

// **Key changes:**
// 1. Moved `Env.Load();` to the very top, before `var builder = WebApplication.CreateBuilder(args);`

// **Additional troubleshooting tips:**

// 1. **Verify your .env file exists** in the project root and has the correct format:
// ```
// ConnectionString=mongodb://localhost:27017
// DatabaseName=JobTrackerDB
// JobApplicationCollectionName=JobApplications