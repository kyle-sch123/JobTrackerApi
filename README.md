# Job Tracker API

A .NET 9.0 Web API for tracking job applications with automated email synchronization and AI-powered email parsing capabilities.

## Overview

Job Tracker API is an ASP.NET Core application that helps users manage their job search by automatically syncing and parsing job-related emails from Gmail. The system uses a hybrid approach combining rule-based parsing with Claude AI to extract structured data from application confirmations, interview invitations, rejection notices, and offer letters.

## Features

- **Job Application Management**: Full CRUD operations for tracking job applications
- **Gmail Integration**: OAuth-based connection to automatically sync job-related emails
- **AI-Powered Email Parsing**: Hybrid parsing system using Claude AI and rule-based extraction
- **Intelligent Application Matching**: Automatically links emails to existing applications
- **Background Email Synchronization**: Scheduled sync via Hangfire
- **Firebase Authentication**: Secure API endpoints with Firebase Admin SDK
- **MongoDB Storage**: Persistent storage for all application data

## Prerequisites

- .NET 9.0 SDK
- MongoDB instance
- Firebase project (for authentication)
- Google Cloud project with Gmail API enabled
- Anthropic API key (for Claude AI parsing)

## Installation

1. Clone the repository
2. Create a `.env` file in the project root (see Configuration section)
3. Restore dependencies:
   ```bash
   dotnet restore
   ```
4. Build the project:
   ```bash
   dotnet build
   ```
5. Run the application:
   ```bash
   dotnet run
   ```

The API will start on `http://0.0.0.0:5000`.

## Configuration

Create a `.env` file in the project root with the following variables:

### Database Configuration
```env
ConnectionString=<your-mongodb-connection-string>
DatabaseName=<your-database-name>
JobApplicationCollectionName=<collection-name-for-jobs>
UserEmailConnectionCollectionName=<collection-name-for-email-connections>
EmailSyncHistoryCollectionName=<collection-name-for-sync-history>
ProcessedEmailCollectionName=<collection-name-for-processed-emails>
```

### Firebase Configuration
```env
FIREBASE_PROJECT_ID=<your-firebase-project-id>
FIREBASE_PRIVATE_KEY=<your-firebase-private-key>
FIREBASE_CLIENT_EMAIL=<your-firebase-client-email>
```

### Google OAuth Configuration
```env
GOOGLE_CLIENT_ID=<your-google-client-id>
GOOGLE_CLIENT_SECRET=<your-google-client-secret>
```

### Claude AI Configuration
```env
CLAUDE_API_KEY=<your-anthropic-api-key>
CLAUDE_MODEL=claude-3-5-sonnet-20241022
CLAUDE_MAX_TOKENS=1024
AI_CONFIDENCE_THRESHOLD_AUTO=80
AI_CONFIDENCE_THRESHOLD_REVIEW=50
```

### Email Sync Configuration
```env
EMAIL_SYNC_INTERVAL_MINUTES=15
FRONTEND_URL_DEV=<frontend-url-for-development>
FRONTEND_URL_PROD=<frontend-url-for-production>
```

## API Endpoints

### Job Applications (`/api/jobapplications`)

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/jobapplications` | Get all applications for authenticated user |
| GET | `/api/JobApplication/{id}` | Get specific application |
| POST | `/api/jobapplications` | Create new application |
| PUT | `/api/JobApplication/{id}` | Update application |
| DELETE | `/api/JobApplication/{id}` | Delete application |
| PATCH | `/api/JobApplication/{id}/status` | Update status only |

### Gmail Authentication (`/api/auth/gmail`)

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/connect` | Get Google OAuth consent URL |
| GET | `/callback` | OAuth callback handler |
| GET | `/status` | Check Gmail connection status |
| POST | `/disconnect` | Revoke Gmail access |

### Email Processing (`/api/email-processing`)

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/process-pending` | Process all pending emails |
| GET | `/review-queue` | Get emails requiring manual review |
| GET | `/stats` | Get processing statistics |
| POST | `/approve/{emailId}` | Approve email for processing |
| POST | `/reject/{emailId}` | Reject/ignore an email |
| POST | `/test-parse` | Test parser with custom content |

## Architecture

### Services

| Service | Description |
|---------|-------------|
| `JobApplicationService` | Core CRUD operations for job applications |
| `GmailAuthService` | Gmail OAuth token management |
| `GmailEmailService` | Fetches and filters emails from Gmail API |
| `EmailSyncService` | Orchestrates email synchronization |
| `ClaudeEmailParserService` | AI-powered email parsing using Claude |
| `RuleBasedEmailParser` | Pattern-based email parsing with regex |
| `HybridEmailParser` | Combines AI and rule-based parsing strategies |
| `JobRelatedEmailFilter` | Pre-filters emails before processing |
| `ApplicationMatchingService` | Links emails to existing applications |
| `EmailProcessingService` | Processes and stores parsed email data |

### Email Parsing Strategy

The system uses a three-tier confidence-based approach:

1. **Rule-Based Only** (High Confidence ≥80%): Fast regex parsing handles clear-cut cases
2. **LLM Refinement** (Medium Confidence 50-80%): AI refines specific uncertain fields
3. **Full LLM Parsing** (Low Confidence <50%): Complete AI analysis for complex emails

### Background Jobs

Hangfire manages background processing with MongoDB storage:

- **Email Sync Job**: Runs at configurable intervals (default: 15 minutes)
- Syncs emails from all connected Gmail accounts
- Processes new emails through the parsing pipeline

### Authentication

All API endpoints (except health checks and OAuth callbacks) are protected via Firebase token validation. The middleware extracts user identity from JWT claims and populates the request context.

**Public Paths:**
- `/health` - Health check
- `/swagger`, `/openapi` - API documentation
- `/hangfire` - Job dashboard (uses its own auth)
- `/api/auth/gmail/callback` - OAuth callback

## Data Models

### JobApplication

```csharp
public class JobApplication
{
    public string Id { get; set; }
    public string userId { get; set; }
    public string jobTitle { get; set; }
    public string company { get; set; }
    public string status { get; set; }  // Applied, Interview Scheduled, Rejected, Offer, In Progress
    public DateTime applicationDate { get; set; }
    public string notes { get; set; }
    
    // AI-extracted fields
    public string? RecruiterName { get; set; }
    public string? RecruiterEmail { get; set; }
    public DateTime? InterviewDate { get; set; }
    public string? InterviewType { get; set; }
    public string? SalaryRange { get; set; }
    
    // AI metadata
    public bool AutoCreated { get; set; }
    public double? AiConfidence { get; set; }
    public bool RequiresReview { get; set; }
}
```

### ProcessedEmail

```csharp
public class ProcessedEmail
{
    public string? Id { get; set; }
    public string UserId { get; set; }
    public string GmailMessageId { get; set; }
    public string Subject { get; set; }
    public string From { get; set; }
    public DateTime Date { get; set; }
    public string? BodyPlainText { get; set; }
    
    // Processing metadata
    public bool IsJobRelated { get; set; }
    public string? JobApplicationId { get; set; }
    public string ProcessingStatus { get; set; }  // pending, processed, failed, ignored
    public EmailExtractedData? ExtractedData { get; set; }
}
```

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| Anthropic | 10.1.2 | Claude AI API client |
| MongoDB.Driver | 3.5.0 | Database access |
| FirebaseAdmin | 3.4.0 | Authentication |
| Google.Apis.Gmail.v1 | 1.70.0.3833 | Gmail integration |
| Google.Apis.Auth | 1.72.0 | Google OAuth |
| Hangfire.AspNetCore | 1.8.22 | Background jobs |
| Hangfire.Mongo | 1.12.2 | Job storage |
| NSwag.AspNetCore | 14.5.0 | API documentation |
| DotNetEnv | 3.1.1 | Environment configuration |

## API Documentation

When running in Development mode, API documentation is available:
- **Swagger UI**: `/swagger`
- **OpenAPI spec**: `/openapi/v1.json`

## Development

### Local URLs

| Profile | URL |
|---------|-----|
| HTTP | `http://localhost:5160` |
| HTTPS | `https://localhost:7037` |

### Logging

- **Development**: Debug-level logging enabled
- **Production**: Information-level logging (configurable in `appsettings.json`)

### CORS

The API is configured with a permissive CORS policy for development. Consider restricting for production deployments.

## Supported Job Platforms

The parser recognizes emails from these platforms:
- LinkedIn, Indeed, Glassdoor, PNet
- ATS platforms: Greenhouse, Lever, Workday, BambooHR
- SmartRecruiters, Jobvite, Broadbean

## License

See LICENSE file for details.