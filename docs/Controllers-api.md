Based on the provided controller files, I can generate comprehensive API documentation. Let me document the public API surface for this Job Tracker API module.

# JobTrackerApi.Controllers - API Documentation

## API Overview

This module provides a RESTful API for a job application tracking system with Gmail integration. The API allows users to:

- Manage job applications (CRUD operations)
- Connect Gmail accounts via OAuth 2.0
- Automatically process job-related emails using AI (Claude) parsing
- Review and approve/reject parsed email data

All endpoints require user authentication via Firebase JWT tokens. User identity is extracted from token claims (`user_id`, `sub`, or `NameIdentifier`).

---

## Base Controller

### `BaseController`

**Namespace:** `JobTrackerApi.Controllers`

Abstract base controller providing authentication helper methods for all derived controllers.

#### Protected Methods

| Method | Signature | Description |
|--------|-----------|-------------|
| `GetUserId` | `string? GetUserId()` | Extracts the current user's Firebase UID from the HTTP context. Returns `null` if not authenticated. Checks claims in order: `user_id`, `sub`, `NameIdentifier`. |
| `GetUserEmail` | `string? GetUserEmail()` | Extracts the user's email address from the `ClaimTypes.Email` claim. Returns `null` if not found. |

---

## Job Application API

**Base Route:** `/api/jobapplications` and `/api/JobApplication`

**Controller:** `JobApplicationController`

### Endpoints

#### GET `/api/jobapplications`

Retrieves all job applications for the authenticated user.

**Response:** `200 OK`
```json
[
  {
    "id": "507f1f77bcf86cd799439011",
    "userId": "firebase-uid",
    "company": "Acme Corp",
    "position": "Software Engineer",
    "status": "Applied",
    "applicationDate": "2024-01-15T10:30:00Z",
    "jobNumber": 1,
    "autoStatusUpdated": false
  }
]
```

---

#### GET `/api/JobApplication/{id}`

Retrieves a specific job application by ID.

**Parameters:**
| Name | Type | Location | Description |
|------|------|----------|-------------|
| `id` | `string` | path | MongoDB ObjectId (24 characters) |

**Responses:**
| Status | Description |
|--------|-------------|
| `200 OK` | Returns the job application |
| `403 Forbidden` | Application belongs to a different user |
| `404 Not Found` | Application not found |

---

#### POST `/api/jobapplications`

Creates a new job application.

**Request Body:** `JobApplication`
```json
{
  "company": "Acme Corp",
  "position": "Software Engineer",
  "status": "Applied",
  "applicationDate": "2024-01-15T10:30:00Z"
}
```

**Notes:**
- `userId` is automatically set from authentication context
- `applicationDate` defaults to `DateTime.UtcNow` if not provided

**Responses:**
| Status | Description |
|--------|-------------|
| `201 Created` | Returns created application with `Location` header |
| `400 Bad Request` | Invalid model state or missing user ID |

---

#### PUT `/api/JobApplication/{id}`

Updates an existing job application.

**Parameters:**
| Name | Type | Location | Description |
|------|------|----------|-------------|
| `id` | `string` | path | MongoDB ObjectId (24 characters) |

**Request Body:** `JobApplication` (full object)

**Notes:**
- `Id`, `userId`, and `jobNumber` are preserved from the existing record

**Responses:**
| Status | Description |
|--------|-------------|
| `200 OK` | Returns updated application |
| `400 Bad Request` | Invalid model state |
| `404 Not Found` | Application not found |

---

#### DELETE `/api/JobApplication/{id}`

Deletes a specific job application.

**Parameters:**
| Name | Type | Location | Description |
|------|------|----------|-------------|
| `id` | `string` | path | MongoDB ObjectId (24 characters) |

**Responses:**
| Status | Description |
|--------|-------------|
| `200 OK` | `{ "message": "Job application deleted successfully" }` |
| `404 Not Found` | Application not found |

---

#### DELETE `/api/JobApplication/user/{userId}`

Deletes all job applications for a specific user.

**Parameters:**
| Name | Type | Location | Description |
|------|------|----------|-------------|
| `userId` | `string` | path | Firebase user ID |

**Responses:**
| Status | Description |
|--------|-------------|
| `200 OK` | `{ "message": "Deleted N job application(s) for user", "count": N }` |
| `400 Bad Request` | User ID is required |

---

#### PATCH `/api/JobApplication/{id}/status`

Updates only the status of a job application.

**Parameters:**
| Name | Type | Location | Description |
|------|------|----------|-------------|
| `id` | `string` | path | MongoDB ObjectId (24 characters) |

**Request Body:** `StatusUpdateRequest`
```json
{
  "status": "Interview",
  "autoStatusUpdated": true
}
```

**Responses:**
| Status | Description |
|--------|-------------|
| `200 OK` | Returns updated application |
| `404 Not Found` | Application not found |

---

## Gmail Authentication API

**Base Route:** `/api/auth/gmail`

**Controller:** `GmailAuthController`

### Endpoints

#### GET `/api/auth/gmail/connect`

Initiates Gmail OAuth 2.0 flow. Returns the Google consent screen URL.

**Responses:**
| Status | Description |
|--------|-------------|
| `200 OK` | `{ "authUrl": "https://accounts.google.com/...", "state": "base64-state-token" }` |
| `401 Unauthorized` | User not authenticated |
| `500 Internal Server Error` | Failed to generate auth URL |

**Usage Example:**
```javascript
const response = await fetch('/api/auth/gmail/connect', {
  headers: { 'Authorization': 'Bearer <firebase-token>' }
});
const { authUrl } = await response.json();
window.location.href = authUrl; // Redirect to Google consent
```

---

#### GET `/api/auth/gmail/callback`

OAuth callback endpoint. Google redirects here after user grants permission.

**Parameters:**
| Name | Type | Location | Description |
|------|------|----------|-------------|
| `code` | `string` | query | Authorization code from Google |
| `state` | `string` | query | Base64-encoded state containing user info |
| `userId` | `string` | query | (Optional) User ID passed by frontend |

**Behavior:**
- Exchanges authorization code for tokens
- Stores Gmail connection in database
- Redirects to frontend settings page with status

**Redirects:**
| Scenario | Redirect URL |
|----------|-------------|
| Success | `{FRONTEND_URL}/settings?gmail=connected` |
| Error | `{FRONTEND_URL}/settings?gmail=error` |

---

#### GET `/api/auth/gmail/status`

Checks if the authenticated user has Gmail connected.

**Responses:**

**Not Connected:**
```json
{ "connected": false }
```

**Connected:**
```json
{
  "connected": true,
  "email": "user@gmail.com",
  "connectedAt": "2024-01-15T10:30:00Z",
  "lastSyncAt": "2024-01-16T08:00:00Z",
  "lastSyncStatus": "success"
}
```

| Status | Description |
|--------|-------------|
| `200 OK` | Connection status |
| `401 Unauthorized` | User not authenticated |
| `500 Internal Server Error` | Failed to get status |

---

#### POST `/api/auth/gmail/disconnect`

Revokes Gmail access and removes the connection.

**Responses:**
| Status | Description |
|--------|-------------|
| `200 OK` | `{ "message": "Gmail disconnected successfully" }` |
| `401 Unauthorized` | User not authenticated |
| `404 Not Found` | No active connection found |
| `500 Internal Server Error` | Failed to disconnect |

---

## Email Processing API

**Base Route:** `/api/email-processing`

**Controller:** `EmailProcessingController`

### Endpoints

#### POST `/api/email-processing/process-pending`

Processes all pending job-related emails for the authenticated user using AI parsing.

**Response:** `200 OK`
```json
{
  "totalProcessed": 10,
  "autoProcessed": 6,
  "requiresReview": 3,
  "lowConfidence": 1,
  "applicationsCreated": 4,
  "applicationsUpdated": 2,
  "results": [
    {
      "action": "auto_processed",
      "jobApplicationId": "507f1f77bcf86cd799439011",
      "message": "Created new application for Software Engineer at Acme"
    }
  ]
}
```

| Status | Description |
|--------|-------------|
| `200 OK` | Processing summary with results |
| `500 Internal Server Error` | Processing failed |

---

#### POST `/api/email-processing/test-parse`

Tests the AI parser with custom email content (debugging/development endpoint).

**Request Body:** `TestEmailRequest`
```json
{
  "subject": "Interview Invitation - Software Engineer",
  "from": "Jane Recruiter",
  "fromEmail": "jane@acme.com",
  "body": "We would like to invite you to an interview..."
}
```

**Response:** `200 OK`
```json
{
  "input": {
    "subject": "Interview Invitation - Software Engineer",
    "from": "Jane Recruiter",
    "fromEmail": "jane@acme.com",
    "bodyPreview": "We would like to invite you to an interview..."
  },
  "extracted": {
    "companyName": "Acme Corp",
    "position": "Software Engineer",
    "applicationStatus": "Interview",
    "interviewDate": "2024-01-20T14:00:00Z",
    "recruiterName": "Jane Recruiter",
    "recruiterEmail": "jane@acme.com",
    "jobUrl": null,
    "salaryRange": null,
    "interviewType": "Phone Screen",
    "confidence": 85
  },
  "processing": {
    "shouldAutoProcess": true,
    "requiresReview": false,
    "action": "Would auto-create/update application"
  }
}
```

---

#### POST `/api/email-processing/test-single/{emailId}`

Tests processing a single email from the database.

**Parameters:**
| Name | Type | Location | Description |
|------|------|----------|-------------|
| `emailId` | `string` | path | Email document ID |

**Response:** `200 OK`
```json
{
  "email": {
    "id": "email-id",
    "subject": "Your Application to Acme",
    "from": "HR Department",
    "fromEmail": "hr@acme.com",
    "date": "2024-01-15T10:30:00Z",
    "isJobRelated": true,
    "processingStatus": "pending"
  },
  "processingResult": { /* ProcessingResult object */ },
  "extractedData": { /* EmailExtractedData object */ }
}
```

| Status | Description |
|--------|-------------|
| `200 OK` | Test result |
| `404 Not Found` | Email not found or doesn't belong to user |
| `500 Internal Server Error` | Processing failed |

---

#### GET `/api/email-processing/review-queue`

Gets emails that require manual review (medium confidence scores).

**Response:** `200 OK`
```json
{
  "count": 3,
  "emails": [
    {
      "id": "email-id",
      "subject": "RE: Your Application",
      "from": "Unknown Sender",
      "date": "2024-01-15T10:30:00Z",
      "extractedData": { /* EmailExtractedData */ },
      "processingStatus": "requires_review"
    }
  ]
}
```

---

#### GET `/api/email-processing/test-claude`

Diagnostic endpoint to test Claude API connectivity.

**Response:** `200 OK`
```json
{
  "status": "OK",
  "apiKeyPresent": true,
  "apiKeyPrefix": "sk-ant-api03-...",
  "response": "{ \"content\": [...] }"
}
```

**Note:** This endpoint exposes partial API key information and should be disabled or protected in production.

---

#### GET `/api/email-processing/stats`

Gets email processing statistics for the authenticated user.

**Response:** `200 OK`

Returns statistics object from `EmailProcessingService.GetProcessingStatsAsync()`.

---

#### POST `/api/email-processing/approve/{emailId}`

Manually approves an email from the review queue for processing.

**Parameters:**
| Name | Type | Location | Description |
|------|------|----------|-------------|
| `emailId` | `string` | path | Email document ID |

**Request Body (Optional):** `ApprovalRequest`
```json
{
  "overrideData": {
    "companyName": "Corrected Company Name",
    "position": "Corrected Position",
    "confidence": 100
  }
}
```

**Notes:**
- If `overrideData` is provided, it replaces the AI-extracted data
- User approval sets confidence to 100%
- Forces processing regardless of original confidence score

**Responses:**
| Status | Description |
|--------|-------------|
| `200 OK` | `{ "message": "Email approved and processed", "result": {...} }` |
| `404 Not Found` | Email not found |
| `500 Internal Server Error` | Processing failed |

---

#### POST `/api/email-processing/reject/{emailId}`

Rejects an email, marking it as ignored (will not be processed).

**Parameters:**
| Name | Type | Location | Description |
|------|------|----------|-------------|
| `emailId` | `string` | path | Email document ID |

**Responses:**
| Status | Description |
|--------|-------------|
| `200 OK` | `{ "message": "Email marked as ignored" }` |
| `404 Not Found` | Email not found |
| `500 Internal Server Error` | Rejection failed |

---

## Request/Response Models

### `TestEmailRequest`

Used for testing the email parser.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Subject` | `string` | `""` | Email subject line |
| `From` | `string` | `""` | Sender display name |
| `FromEmail` | `string` | `""` | Sender email address |
| `Body` | `string` | `""` | Email body content (plain text) |

### `ApprovalRequest`

Used when approving emails from the review queue.

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `OverrideData` | `EmailExtractedData?` | No | Optional data to override AI-extracted values |

### `StatusUpdateRequest`

Used for PATCH status updates on job applications.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Status` | `string` | `""` | New status value |
| `AutoStatusUpdated` | `bool` | `false` | Whether status was updated automatically |

---

## Authentication

All endpoints (except `/api/auth/gmail/callback`) require a valid Firebase JWT token in the `Authorization` header:

```
Authorization: Bearer <firebase-jwt-token>
```

The token must contain one of these claims for user identification:
1. `user_id` (preferred)
2. `sub`
3. `NameIdentifier`

---

## Error Responses

All endpoints follow a consistent error response format:

```json
{
  "error": "Error message describing what went wrong"
}
```

Or for validation errors:
```json
{
  "message": "Validation error message"
}
```

---

## Dependencies (External Services)

The following external services are referenced but not included in this module:

| Service | Purpose |
|---------|---------|
| `JobApplicationService` | MongoDB operations for job applications |
| `GmailAuthService` | Gmail OAuth token management |
| `EmailProcessingService` | Email retrieval and processing orchestration |
| `ClaudeEmailParserService` | AI-powered email parsing using Claude API |

---

## Environment Variables

| Variable | Description |
|----------|-------------|
| `CLAUDE_API_KEY` | Anthropic API key for Claude integration |
| `ASPNETCORE_ENVIRONMENT` | `Development` or `Production` |
| `FRONTEND_URL_DEV` | Frontend URL for development redirects |
| `FRONTEND_URL_PROD` | Frontend URL for production redirects |