# JobTrackerApi — Backend

ASP.NET Core 9 / C# 13 Web API that ingests Gmail messages, classifies and
parses job-related emails with a hybrid rule-based + Claude pipeline, and
maintains a per-user list of job applications in MongoDB.

> Known issues and gaps are tracked in [`./Issues.md`](./Issues.md).
> This README documents what the code **does** today, not what we wish it did.

---

## Stack

| Concern | Choice |
|---|---|
| Runtime | .NET 9, ASP.NET Core (`Microsoft.NET.Sdk.Web`) |
| Persistence | MongoDB (`MongoDB.Driver` 3.5.0) |
| Auth | Firebase Admin (`FirebaseAdmin` 3.4.0) via custom middleware |
| Gmail | `Google.Apis.Gmail.v1` + `Google.Apis.Auth` (OAuth 2.0) |
| AI parsing | Anthropic Messages API (Claude), called over HTTP |
| Background jobs | Hangfire on Mongo storage (`Hangfire.Mongo` 1.12.2) |
| API docs | OpenAPI + NSwag Swagger UI (development only) |
| Config | `DotNetEnv` (`.env` loaded at startup) |

Listens on `http://0.0.0.0:5000` by default (`Program.cs`). CORS is set to
`AllowAnyOrigin/Method/Header`.

---

## Project layout

```
JobTrackerApi/
├── Program.cs                        # Startup, DI, middleware, Hangfire wiring
├── JobTrackerApi.csproj              # Package references
├── JobTrackerApi.http                # Sample HTTP requests
├── appsettings.json                  # Mostly empty — values come from env vars
├── appsettings.Development.json
├── Dockerfile                        # SDK build → aspnet runtime
├── docs/Controllers-api.md           # (legacy) API reference
├── CLAUDE.local.md                   # Claude-Code notes (informational)
│
├── Controllers/
│   ├── BaseController.cs             # GetUserId / GetUserEmail helpers
│   ├── JobApplicationController.cs   # /api/jobapplications + /api/JobApplication
│   ├── GmailAuthController.cs        # /api/auth/gmail/{connect,callback,status,disconnect}
│   └── EmailProcessingController.cs  # /api/email-processing/*
│
├── Middleware/
│   └── FirebaseAuthMiddleware.cs     # Verifies Bearer ID tokens, populates ClaimsPrincipal
│
├── Jobs/
│   └── BackgroundEmailSyncJob.cs     # Hangfire recurring job → EmailSyncService.SyncAllUsersAsync
│
├── Models/
│   ├── JobApplication.cs             # Job application + AI-enrichment fields
│   ├── ProcessedEmail.cs             # Stored Gmail message + EmailExtractedData
│   ├── UserEmailConnection.cs        # Per-user Gmail OAuth tokens (plaintext)
│   ├── EmailSyncHistory.cs           # Per-sync-run audit record
│   ├── EmailSignals.cs               # Rule-based parser output + ParsingStrategy enum
│   └── JobApplicationDatabaseSettings.cs
│
└── Services/
    ├── JobApplicationService.cs      # Mongo CRUD; auto-increments jobNumber per user
    ├── GmailAuthService.cs           # OAuth URL, token exchange/refresh, disconnect
    ├── GmailEmailService.cs          # Fetch Gmail, parse MIME, persist ProcessedEmail
    ├── JobRelatedEmailFilter.cs      # Lightweight is-it-job-related pre-filter
    ├── EmailSyncService.cs           # Scheduled sync path (LLM-only — see "Two pipelines")
    ├── EmailProcessingService.cs     # Controller-driven path (hybrid + matching)
    ├── ApplicationMatchingService.cs # Find/create-vs-update + company/title variations
    ├── RuleBasedEmailParser.cs       # Regex-based extraction → EmailSignals
    ├── ClaudeEmailParserService.cs   # Direct HTTP calls to Anthropic Messages API
    ├── HybridEmailParser.cs          # Routes between rule-based / LLM-refine / LLM-full
    └── EmailParserService.cs         # ⚠ Unused / orphan, kept for now
```

DI: every service is registered as `Singleton` in `Program.cs`;
`BackgroundEmailSyncJob` is `Scoped` (Hangfire creates a scope per run).

---

## How email processing works (real behaviour)

There are **two parallel pipelines** today, and they don't agree. See
[`Issues.md`](../../Issues.md) for the "merge them" recommendation.

### Pipeline A — Hangfire scheduled sync (`EmailSyncService`)

```
recurring job "email-sync-job"      ← cron */N * * * *  (EMAIL_SYNC_INTERVAL_MINUTES)
        │
        ▼
BackgroundEmailSyncJob.ExecuteAsync
        │
        ▼
EmailSyncService.SyncAllUsersAsync              ← iterates active connections
        │  (1s delay between users)
        ▼
EmailSyncService.SyncUserEmailsAsync(uid)
        │
        ├── GmailEmailService.FetchNewEmailsAsync   → Mongo: processed_emails
        │
        └── for each new email:
              ├── JobRelatedEmailFilter.IsJobRelated
              │     ─ if false: mark "skipped_not_job_related"
              │
              └── ClaudeEmailParserService.ParseEmailAsync     ← LLM directly, no hybrid
                    └── crude match on company OR title vs existing apps
                          ├── match  → conservative field merge + UpdateAsync
                          └── no match → CreateAsync (status from LLM, else "Applied")
```

### Pipeline B — Controller-driven (`EmailProcessingService`)

```
POST /api/email-processing/process-pending
        │
        ▼
EmailProcessingService.ProcessPendingEmailsAsync(uid)
        │     ─ Mongo filter: IsJobRelated && !AiParsed && status=="pending"
        │     ─ 500 ms delay between emails
        ▼
EmailProcessingService.ProcessEmailWithHybridAsync(email, forceProcess?)
        │
        ├── JobRelatedEmailFilter.IsJobRelated      (unless forced)
        │     ─ if false: "skipped_not_job_related"
        │
        ├── HybridEmailParser.ParseEmailAsync
        │     ├── RuleBasedEmailParser → EmailSignals
        │     │     └── DetectNonApplicationEmail (newsletter/alert/posting guard)
        │     ├── DetermineParsingStrategy
        │     │     ├── ≥ 70 % overall + core fields present  → RuleBasedOnly
        │     │     ├── ≥ 40 %                                → LLMRefinement
        │     │     └── < 40 % or core fields missing         → LLMFull
        │     └── method tag: rule-based | hybrid-refined | llm-full | rule-based-rejected
        │
        ├── if !IsJobApplication: "skipped_not_job_application"
        │
        └── route by confidence (HybridEmailParser.ShouldAutoProcess / RequiresReview):
              ├── auto  → AutoProcessApplication
              │             ├── ApplicationMatchingService.FindMatchingApplicationAsync
              │             │     ├── exact (company variations × position variations)
              │             │     └── fuzzy (same company within 90 days)
              │             ├── ShouldCreateNew? (skip if Rejected/Interview/Offer;
              │             │                    skip if "Applied" within 7 days of existing)
              │             ├── create new JobApplication  OR
              │             └── update existing (status promoted via ShouldUpdateStatus)
              ├── review queue   → status "requires_review"
              └── low confidence → status "ignored"
```

`/test-single/{id}` and `/approve/{id}` (with optional `OverrideData`) also
flow through `ProcessEmailWithHybridAsync`.

### Confidence thresholds

`HybridEmailParser` (the parser the controller pipeline uses) defaults to:

| Threshold | Env var | Default |
|---|---|---|
| Auto-process | `AI_CONFIDENCE_THRESHOLD_AUTO` | `70` |
| Review queue floor | `AI_CONFIDENCE_THRESHOLD_REVIEW` | `40` |

`ClaudeEmailParserService` has its own (unused-in-the-real-pipeline)
defaults of `80` / `50`. The discrepancy is tracked in `Issues.md`.

### Processing-status state machine on `ProcessedEmail`

```
"pending"                     ← seeded by GmailEmailService on first fetch
   ├─→ "skipped_not_job_related"     (filter rejected)
   ├─→ "skipped_not_job_application" (LLM/rules said newsletter/posting)
   ├─→ "processed"                   (linked to a JobApplication)
   ├─→ "requires_review"             (medium confidence — needs user)
   ├─→ "ignored"                     (low confidence OR user-rejected)
   └─→ "failed"                      (exception during processing)
```

---

## HTTP API

All endpoints require `Authorization: Bearer <firebase-id-token>` unless
listed under "Public paths" below. The middleware extracts the uid from the
verified token; controllers read it via `BaseController.GetUserId()`.

### Public paths (skipped by Firebase middleware)

- `/` (exact)
- `/health`, `/openapi`, `/swagger` (prefix)
- `/hangfire` (prefix — note: dashboard is not actually mapped)
- `/api/auth/gmail/callback` (Google redirects here without a token)

### Job applications — `JobApplicationController`

Routes are a mix of `[Route("api/[controller]")]` (= `api/JobApplication`)
and explicit `[Route("/api/jobapplications")]` overrides:

| Method | Path | Notes |
|---|---|---|
| GET | `/api/jobapplications` | Returns the caller's applications. ⚠ Falls back to all-users if uid is null. |
| GET | `/api/JobApplication/{id}` | 404 if missing, 403 if not owned. |
| POST | `/api/jobapplications` | `userId` overwritten with caller; `jobNumber` auto-incremented. |
| PUT | `/api/JobApplication/{id}` | Preserves `Id`, `userId`, `jobNumber`. ⚠ No ownership check. |
| DELETE | `/api/JobApplication/{id}` | ⚠ No ownership check. |
| DELETE | `/api/JobApplication/user/{userId}` | Bulk delete. ⚠ Does not verify caller == `userId`. |
| PATCH | `/api/JobApplication/{id}/status` | Status + `autoStatusUpdated`. ⚠ No ownership check. |

`StatusUpdateRequest = { Status: string; AutoStatusUpdated: bool }`.

### Gmail OAuth — `GmailAuthController` (`/api/auth/gmail`)

| Method | Path | Notes |
|---|---|---|
| GET | `/connect` | Returns `{ authUrl, state }`. `state` = base64(JSON `{userId, state}`); the URL is hand-built (scope = `gmail.readonly`, `access_type=offline`, `prompt=consent`). |
| GET | `/callback` | Public. Exchanges `?code` → tokens via `GoogleAuthorizationCodeFlow`. Resolves `userId` from `?userId` or by decoding `?state`. Redirects to `${FRONTEND_URL_{DEV,PROD}}/settings?gmail={connected,error}`. |
| GET | `/status` | `{ connected: false }` or `{ connected: true, email, connectedAt, lastSyncAt, lastSyncStatus }`. |
| POST | `/disconnect` | Sets `IsActive=false`. ⚠ Does **not** call Google's revoke endpoint. |

### Email processing — `EmailProcessingController` (`/api/email-processing`)

| Method | Path | Notes |
|---|---|---|
| POST | `/process-pending` | Runs Pipeline B over the caller's `pending && job-related && !AiParsed` emails. |
| POST | `/test-parse` | Body = `TestEmailRequest`. ⚠ Calls Claude directly, **not** the hybrid parser. |
| POST | `/test-single/{emailId}` | Runs hybrid on one stored email. |
| GET | `/review-queue` | Emails with status `requires_review`. |
| GET | `/test-claude` | Diagnostic ping to Anthropic. ⚠ Response includes first 20 chars of `CLAUDE_API_KEY`. |
| GET | `/stats` | Per-user rollup; counts in process from a full collection scan. |
| POST | `/approve/{emailId}` | Optional `{ overrideData }`. Force-processes regardless of confidence. |
| POST | `/reprocess/{emailId}` | Force re-parse by Mongo id (undocumented in the old README). |
| POST | `/reprocess-by-gmail-id/{gmailMessageId}` | Force re-parse by Gmail message id. |
| POST | `/reject/{emailId}` | Marks `processingStatus = "ignored"`. |

DTOs:

```csharp
class TestEmailRequest  { string Subject; string From; string FromEmail; string Body; }
class ApprovalRequest   { EmailExtractedData? OverrideData; }
```

---

## Data model (Mongo)

Five collections (names come from env vars):

| Collection | Model | Used by |
|---|---|---|
| `JobApplicationCollectionName` | `JobApplication` | `JobApplicationService`, `ApplicationMatchingService` |
| `ProcessedEmailCollectionName` | `ProcessedEmail` | `GmailEmailService`, `EmailProcessingService` |
| `UserEmailConnectionCollectionName` | `UserEmailConnection` | `GmailAuthService`, `EmailSyncService` |
| `EmailSyncHistoryCollectionName` | `EmailSyncHistory` | `EmailSyncService` |
| (Hangfire) | internal | Hangfire (prefix `hangfire`) |

There are **no indexes created by application code**; dedup relies on
`Find().AnyAsync()` rather than unique constraints.

### `JobApplication` (selected fields)

Core: `Id`, `jobNumber`, `userId`, `jobTitle`, `company`, `status`,
`applicationDate`, `notes`, `autoStatusUpdated`, `createdAt`, `updatedAt`.

AI-enrichment: `RecruiterName`, `RecruiterEmail`, `RecruiterPhone`,
`InterviewDate`, `InterviewType`, `SalaryRange`, `EmailIds[]`,
`AutoCreated`, `OriginalCompany`, `OriginalPosition`, `AiConfidence`,
`RequiresReview`, `ReviewedAt`.

`OriginalCompany`/`OriginalPosition` capture what the AI first extracted —
they're used by `ApplicationMatchingService` so later emails still match
even after the user renames the application.

### `ProcessedEmail`

`Id`, `userId`, `gmailMessageId`, `threadId`, `subject`, `from`, `fromEmail`,
`to`, `date`, `snippet`, `bodyPlainText`, `bodyHtml`, `labels[]`,
`isJobRelated`, `jobApplicationId?`, `processedAt`, `processingStatus`,
`aiParsed`, `extractedData: EmailExtractedData?`, `extractionMethod?`.

`EmailExtractedData`: `companyName`, `position`, `applicationStatus`,
`interviewDate`, `interviewType`, `recruiterName`, `recruiterEmail`,
`jobUrl`, `salaryRange`, `confidence` (0-100), `extractionMethod`,
`isJobApplication`, `description`.

JSON serialization: `Program.cs` sets `PropertyNamingPolicy = null` so field
names go over the wire **exactly as declared** in C# (mixed camelCase /
PascalCase — the frontend needs to match).

---

## Authentication

`Middleware/FirebaseAuthMiddleware.cs`:

1. If path is on the public list → pass through.
2. Pull `Bearer …` from `Authorization`. Strip CR/LF/spaces (paste hygiene).
3. Reject 401 if the cleaned token isn't a 3-segment JWT (`dotCount != 2`).
4. `FirebaseAuth.DefaultInstance.VerifyIdTokenAsync(token)`.
5. On success, populate
   - `HttpContext.Items["UserId"]` = uid
   - `HttpContext.Items["FirebaseToken"]` = decoded token
   - `HttpContext.User` = `ClaimsPrincipal` with `NameIdentifier`,
     `firebase_uid`, and (if present in the token) `Email` claims.

`BaseController.GetUserId()` reads `user_id` → `sub` → `NameIdentifier` in
that order. In practice only `NameIdentifier` is set, so that's what wins.

Diagnostic logging is verbose (token length / dot count / prefix / suffix
on every request). Demote in production.

---

## Environment variables

Loaded from `.env` (via `DotNetEnv`) at startup and pushed into the
`IConfiguration` "JobApplicationDatabase:*" keys.

### Database

```env
ConnectionString=mongodb://...
DatabaseName=JobTracker
JobApplicationCollectionName=job_applications
UserEmailConnectionCollectionName=user_email_connections
EmailSyncHistoryCollectionName=email_sync_history
ProcessedEmailCollectionName=processed_emails
```

### Firebase Admin (server-side token verification)

```env
FIREBASE_PROJECT_ID=...
FIREBASE_PRIVATE_KEY="-----BEGIN PRIVATE KEY-----\n...\n-----END PRIVATE KEY-----\n"
FIREBASE_CLIENT_EMAIL=firebase-adminsdk-xxxxx@<project>.iam.gserviceaccount.com
```

`Program.cs` does `Replace("\\n", "\n").Replace("\"","")` on the private
key before composing the credentials JSON. Wrap the value in quotes in
your shell so the literal `\n`s arrive intact; remove surrounding quotes
yourself if your tooling adds them. (See `Issues.md` for the brittleness.)

### Google OAuth (Gmail)

```env
GOOGLE_CLIENT_ID=...
GOOGLE_CLIENT_SECRET=...
GOOGLE_REDIRECT_URI_DEV=http://localhost:5000/api/auth/gmail/callback
GOOGLE_REDIRECT_URI_PROD=https://your-api.example.com/api/auth/gmail/callback
```

### Frontend redirects (post-OAuth)

```env
ASPNETCORE_ENVIRONMENT=Development   # picks DEV vs PROD branches
FRONTEND_URL_DEV=http://localhost:3000
FRONTEND_URL_PROD=https://your-frontend.example.com
```

### Claude / Anthropic

```env
CLAUDE_API_KEY=sk-ant-...
CLAUDE_MODEL=claude-3-5-sonnet-20241022   # default if unset
CLAUDE_MAX_TOKENS=1024                    # default if unset
AI_CONFIDENCE_THRESHOLD_AUTO=70           # default 70 in HybridEmailParser
AI_CONFIDENCE_THRESHOLD_REVIEW=40         # default 40 in HybridEmailParser
```

### Background sync

```env
EMAIL_SYNC_INTERVAL_MINUTES=15            # cron is "*/N * * * *"
```

---

## Running

```bash
# from backend/JobTrackerApi
dotnet restore
dotnet build
dotnet run           # http://0.0.0.0:5000
# or
dotnet watch run
```

In Development, OpenAPI is available at `/openapi/v1.json` and Swagger UI at
`/swagger`.

### Docker

```bash
# from backend/JobTrackerApi
docker build -t job-tracker-api .
docker run --rm -p 8080:8080 \
  -e ASPNETCORE_URLS=http://+:8080 \
  --env-file .env \
  job-tracker-api
```

⚠ The Dockerfile `EXPOSE`s 8080 but `Program.cs` hard-codes
`UseUrls("http://0.0.0.0:5000")`. Override with `ASPNETCORE_URLS` as above,
or change the code. See `Issues.md`.

---

## Background jobs

```csharp
// Program.cs
recurringJobManager.AddOrUpdate<BackgroundEmailSyncJob>(
    "email-sync-job",
    job => job.ExecuteAsync(),
    $"*/{syncIntervalMinutes} * * * *"
);
```

- Worker count: 1 (`AddHangfireServer(o => o.WorkerCount = 1)`).
- Storage: same Mongo instance, collection prefix `hangfire`.
- Backup strategy: `CollectionMongoBackupStrategy` (snapshots collections
  during Hangfire migrations).
- Hangfire dashboard: **not** currently mounted (the auth filter exists
  but `app.UseHangfireDashboard(...)` is never called).

---

## Adding / changing things

- **New rule-based extraction pattern**: edit `Services/RuleBasedEmailParser.cs`
  in the relevant `DetectXxx` region; each pattern carries its own
  confidence.
- **New non-application heuristic**: add to `DetectNonApplicationEmail`
  (newsletter / alert / "is hiring" guard).
- **Tweak Claude prompt**: `ClaudeEmailParserService.BuildPrompt`. The
  system prompt is inlined in `ParseEmailAsync`.
- **Tweak confidence routing**: `HybridEmailParser.DetermineParsingStrategy`
  + `HybridEmailParser.ShouldAutoProcess/RequiresReview`.
- **Tweak duplicate-detection**: `ApplicationMatchingService`
  (`_companyVariations`-style helpers, `ShouldCreateNew`).

---

## Known issues

See [`./Issues.md`](./Issues.md) — covers ownership-check gaps on
JobApplication mutations, the two-pipeline divergence, plaintext OAuth
tokens, the unmounted Hangfire dashboard, threshold mismatches, missing
Mongo indexes, dead code (`EmailParserService.cs`), and more.
