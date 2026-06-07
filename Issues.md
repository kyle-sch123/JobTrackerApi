# Issues & Shortcomings

Findings from a deep code read of the backend (`backend/JobTrackerApi`) and
frontend (`frontend/job-application-tracker-frontend`). Each item lists the
location, what the code actually does, and a brief suggestion.

Severity labels: 🔴 critical · 🟠 high · 🟡 medium · 🟢 low/polish.

---

## Backend

### Security & authorization

- 🔴 **PUT/DELETE/PATCH on JobApplications skip ownership checks**
  `Controllers/JobApplicationController.cs`
  - `Update(string id, …)` (line ~87), `Delete(string id)` (line ~119),
    `UpdateStatus(string id, …)` (line ~153) load the existing record but
    never compare `existing.userId` to `GetUserId()`.
  - `DeleteAllForUser(string userId)` (line ~135) trusts the URL parameter
    with no comparison to the caller's uid.
  - **Impact:** any authenticated user can mutate or delete any other user's
    applications (and bulk-wipe a user) given the document ID.
  - **Fix:** in each handler, return `Forbid()` when
    `existingApplication.userId != GetUserId()`; for the bulk endpoint,
    require `userId == GetUserId()` or remove the endpoint entirely.

- 🟠 **`GET /api/jobapplications` returns ALL applications when uid is null**
  `Controllers/JobApplicationController.cs:20-28`
  - `GetAll()` falls back to `_jobApplicationService.GetAsync()` (no filter)
    when `GetUserId()` returns null. The Firebase middleware should prevent
    that, but if the skip-auth list ever changes this becomes a data leak.
  - **Fix:** return `Unauthorized()` (or 400) when `userId` is null.

- 🟠 **`GET /api/email-processing/test-claude` leaks API key prefix**
  `Controllers/EmailProcessingController.cs:240-246`
  - Response includes `apiKeyPrefix = apiKey.Substring(0, 20) + "..."`.
  - **Fix:** drop the prefix from the response, or gate the whole endpoint
    behind `app.Environment.IsDevelopment()`.

- 🟠 **Gmail OAuth tokens stored in plaintext**
  `Models/UserEmailConnection.cs` + `Services/GmailAuthService.cs`
  - `AccessToken`/`RefreshToken` written to Mongo as-is.
  - **Fix:** at minimum, encrypt at rest with a key from env (e.g. AES-GCM
    via `Microsoft.AspNetCore.DataProtection`), or store via a secrets manager.

- 🟠 **Disconnect doesn't revoke the Google grant**
  `Services/GmailAuthService.cs:200-212`
  - `DisconnectAsync` only sets `IsActive = false`. The OAuth refresh token
    remains valid on Google's side.
  - **Fix:** also POST to
    `https://oauth2.googleapis.com/revoke?token=<refresh_token>` before
    flipping the flag.

- 🟡 **OAuth state isn't actually verified**
  `Controllers/GmailAuthController.cs:38-46, 60-89`
  - A random `state` is generated and round-tripped, but the callback
    never compares it against anything (no server-side store, no cookie).
  - **Fix:** persist `state → userId` in an in-memory cache (or signed
    cookie) for the duration of the flow and reject mismatches.

- 🟡 **CORS is `AllowAnyOrigin/Method/Header`**
  `Program.cs:143-150`
  - Fine for local dev, but should be tightened for production (and would
    block credentials in browsers if you ever needed them).
  - **Fix:** read allowed origins from env (`FRONTEND_URL_DEV`/`_PROD`).

- 🟡 **Hangfire dashboard auth filter exists but the dashboard is never mounted**
  `Program.cs:198-232`
  - `HangfireAuthorizationFilter` is defined and references basic-auth
    credentials, but `app.UseHangfireDashboard(...)` is not called, so the
    filter has no effect.
  - **Fix:** either remove the class, or wire it up with
    `app.UseHangfireDashboard("/hangfire", new DashboardOptions { Authorization = new[] { new HangfireAuthorizationFilter(user, pass) } })`.

- 🟢 **Verbose token logging in middleware**
  `Middleware/FirebaseAuthMiddleware.cs:33-54`
  - Logs token length, dot count, prefix, suffix on every request. Not a
    leak of the secret, but noisy in production logs.
  - **Fix:** demote to `LogDebug` or guard with `IsDevelopment`.

### Architecture & correctness

- 🔴 **Two divergent email pipelines produce different results**
  - `Services/EmailSyncService.SyncUserEmailsAsync` (the path Hangfire and
    `SyncAllUsersAsync` use) calls `ClaudeEmailParserService.ParseEmailAsync`
    directly (full LLM, no rule-based pre-pass) and matches existing jobs
    with a naive `j.company == X || j.jobTitle == Y` (line ~113).
  - `Services/EmailProcessingService.ProcessEmailWithHybridAsync` (the path
    the `/api/email-processing/process-pending` controller calls) uses
    `HybridEmailParser` + `ApplicationMatchingService` with company/title
    variations, status-hierarchy promotion, and a 7-day duplicate guard.
  - **Impact:** the scheduled sync silently produces lower-quality matches
    and skips the rule-based confidence routing entirely. Same email,
    different outcome depending on whether it was synced or processed.
  - **Fix:** have `EmailSyncService` delegate per-email work to
    `EmailProcessingService.ProcessEmailWithHybridAsync` after insert,
    rather than re-implementing parse/match inline.

- 🟠 **Three overlapping "is this job-related?" filters**
  - `Services/GmailEmailService.IsJobRelatedEmail` runs at fetch time with
    one keyword list.
  - `Services/JobRelatedEmailFilter.IsJobRelated` runs in both pipelines
    with a different (richer) keyword list.
  - `Services/RuleBasedEmailParser.DetectNonApplicationEmail` runs a third
    pass inside the rule-based parser.
  - **Fix:** collapse to one canonical filter (`JobRelatedEmailFilter`) and
    have the others delegate to it.

- 🟠 **`/api/email-processing/test-parse` skips the hybrid parser**
  `Controllers/EmailProcessingController.cs:87`
  - Calls `_parserService.ParseEmailAsync` (LLM-only) directly. The
    "Processing" section of the response then evaluates against
    `ClaudeEmailParserService.ShouldAutoProcess` (default 80/50), not
    `HybridEmailParser`'s 70/40 — so the test endpoint reports a *different
    decision* than the real pipeline would make.
  - **Fix:** inject and call `HybridEmailParser.ParseEmailAsync` and use
    its `ShouldAutoProcess`/`RequiresReview`.

- 🟠 **Confidence threshold defaults disagree across services**
  - `Services/HybridEmailParser.ShouldAutoProcess` defaults `70/40`.
  - `Services/ClaudeEmailParserService.ShouldAutoProcess` defaults `80/50`.
  - `README.md` documents one pair in one section and the other elsewhere.
  - **Fix:** centralise the thresholds (one options class bound to
    `AI_CONFIDENCE_THRESHOLD_*`) and inject it instead of reading env vars
    on every call.

- 🟠 **No Mongo indexes are created in code**
  - Frequent queries (`GmailMessageId + UserId`, `UserId + ProcessingStatus`,
    `UserId + IsActive`) rely on full collection scans.
  - **Fix:** create unique/compound indexes at startup
    (`CreateIndexesAsync`) e.g. unique `(UserId, GmailMessageId)` on
    `processed_emails`, unique `UserId` on `user_email_connections`.

- 🟡 **`ApplicationMatchingService.FindExactMatch` loads every user app into memory**
  `Services/ApplicationMatchingService.cs:69-97`
  - `Find(x => x.userId == userId).ToListAsync()` then LINQ
    `FirstOrDefault`. Scales linearly with a user's history.
  - **Fix:** narrow the Mongo filter to candidate companies first, or push
    the variation matching into the query.

- 🟡 **`GetProcessingStatsAsync` pulls full collection into memory**
  `Services/EmailProcessingService.cs:426-461`
  - All emails for a user are loaded, then counted in process.
  - **Fix:** use Mongo aggregation (`$match` + `$group`) and return the
    rollup.

- 🟡 **Status taxonomy isn't consistent**
  - `EmailProcessingService.ShouldUpdateStatus` hierarchy: `Applied,
    In Progress, Interview Scheduled, Offer, Rejected, Accepted, Declined`.
  - `ClaudeEmailParserService.PostProcessExtractedData` valid set:
    `Applied | Interview Scheduled | Rejected | Offer | In Progress`
    (no `Accepted/Declined`).
  - Frontend form: `Applied, Interview Scheduled, Interview Completed,
    Rejected, Accepted, Withdrawn, On Hold`.
  - **Fix:** introduce a single canonical enum / constants file shared by
    the parser, matcher, and DTOs.

- 🟡 **`Program.cs` strips all quote characters from the Firebase private key**
  `Program.cs:58-60`
  - `cleanedPrivateKey = firebasePrivateKey.Replace("\\n","\n").Replace("\"","")`.
  - Works because PEM payloads don't contain `"`, but it's brittle and
    silently corrupts any key whose env value was *not* quoted.
  - **Fix:** trim a single pair of surrounding quotes, don't `Replace`
    every occurrence.

- 🟡 **No retry / backoff on Claude API calls**
  `Services/ClaudeEmailParserService.cs`
  - A single failure (rate limit, transient 5xx) falls straight through to
    `CreateFallbackExtraction` (heuristic, confidence 30).
  - **Fix:** wrap in Polly with exponential backoff and explicit handling
    of `429` (respect `retry-after`) and `5xx`.

- 🟡 **`HttpClient` reuse pattern is inconsistent**
  - `ClaudeEmailParserService` constructs a single `HttpClient` and stores
    it on a Singleton (good, but no `IHttpClientFactory`).
  - `Controllers/EmailProcessingController.TestClaude` `new`s a one-off
    `HttpClient` per request (socket-exhaustion antipattern).
  - **Fix:** register both via `AddHttpClient` and inject.

- 🟢 **`GmailAuthService` builds the OAuth URL by hand-concatenating strings**
  `Services/GmailAuthService.cs:46-64`
  - Works, but reinventing `GoogleAuthorizationCodeFlow.CreateAuthorizationCodeRequest`.
  - **Fix:** use the flow's own builder for fewer bugs.

### Dead / orphaned code

- 🟡 **`Services/EmailParserService.cs` is unused**
  Not registered in DI, not referenced. Older rule-based parser that returns
  confidence 0-1 (everywhere else uses 0-100).
  - **Fix:** delete the file.

- 🟡 **`HangfireAuthorizationFilter` is unused**
  (See "Hangfire dashboard …" above.)

- 🟢 **`appsettings.json` keys are all empty**
  `appsettings.json`
  - Every value comes from env vars overridden in `Program.cs`. The shell
    settings file is misleading.
  - **Fix:** delete the empty `JobApplicationDatabase` section (or keep it
    with comments documenting it's overridden at runtime).

### Operational

- 🟡 **Dockerfile exposes 8080 but `Program.cs` binds 5000**
  `Dockerfile:13` vs `Program.cs:141`
  - Without `ASPNETCORE_URLS=http://+:8080`, the container listens on a
    port nothing is mapped to.
  - **Fix:** either change `WebHost.UseUrls` to read `ASPNETCORE_URLS`/env
    or align both sides on one port.

- 🟡 **`README.md` and `CLAUDE.local.md` are out of date**
  - Both still reference `GmailController.cs` (actual file:
    `GmailAuthController.cs`) and `JobApplicationsController.cs` (plural —
    actual: `JobApplicationController.cs`).
  - README documents thresholds `80/50`; the in-use hybrid path defaults
    `70/40`.
  - README lists `Services/Parsing/…` as a subfolder; the parser files
    actually live flat under `Services/`.
  - **Fix:** rewritten in this pass.

---

## Frontend

### Correctness bugs

- 🔴 **Application updates send PascalCase fields that the API ignores**
  `src/sections/JobApplicationForm.tsx:557-575`
  - `updateApplication` does
    `{ ...editingApplication, JobTitle, Company, Status, ApplicationDate, Notes }`.
  - `editingApplication` already has camelCase fields (`jobTitle`, `company`,
    …); the PascalCase overrides land on different keys.
  - The backend (`PropertyNamingPolicy = null`) only reads `jobTitle` etc.,
    so the spread's *original* camelCase values win — **user edits never
    persist on update**.
  - **Fix:** send the body with camelCase keys
    (`jobTitle: formData.JobTitle`, etc.).

- 🔴 **`StatusBadge` color logic never fires**
  `src/components/statusBadge.tsx:3-15`
  - Switch uses lowercase cases (`"applied"`); data is capitalised
    (`"Applied"`). Every badge falls through to the gray default.
  - **Fix:** `.toLowerCase()` the switch input *or* match on the real
    casing.

- 🟠 **`ReviewQueue` reads fields the API doesn't return**
  `src/components/ReviewQueue.tsx:4-10, 108-117`
  - Local `Email` interface uses `sender` / `body`; backend returns
    `from` / no body (it returns `subject`, `from`, `date`, `extractedData`,
    `processingStatus`).
  - **Fix:** rename to match the API and render `extractedData` (company,
    position, status, confidence) instead of a non-existent body.

- 🟠 **`ReviewQueue` prop type lies about its shape**
  `src/components/ReviewQueue.tsx:17`
  - Declared `{ uid }: { uid: string }`, but `JobApplicationForm.tsx:726`
    passes the entire Firebase `user` object.
  - **Fix:** either accept `user: User` (and read `user.uid`) or pass
    `user.uid` from the parent.

- 🟠 **"Response Rate" can divide by zero**
  `src/sections/dashboard.tsx:282-289`
  - `(... / metrics.totalApplications) * 100` is rendered even when
    `totalApplications` is 0 (logs NaN%).
  - **Fix:** guard `totalApplications === 0` and render `—`.

- 🟡 **`authedFetch` retry on 401 will fail for non-GET requests with bodies**
  `src/lib/authedFetch.ts:32-36`
  - When the original call had a `body`, retrying with the same
    `options` works for a string body but not for a `ReadableStream`
    (consumed). Today all callers pass strings, so OK in practice — worth
    a comment.

### Architecture & duplication

- 🟠 **Two parallel auth integrations**
  - `src/lib/contexts/AuthContext.tsx` — React Context exposing
    `user, loading, getToken`.
  - `src/store/authStore.ts` — Zustand store with `user, uid, setUser,
    setUid, clearAuth`, persisted to `localStorage` under `auth-storage`.
  - Both are updated independently (auth page writes to Zustand; context
    is purely Firebase-driven). Components mix them freely.
  - **Fix:** pick one as source of truth. Recommended: keep
    `AuthContext` (it tracks `loading`), drop Zustand or shrink it to
    `{ uid }` for the navbar's "logged in?" check.

- 🟠 **Two HTTP clients — one of them dead**
  - `src/lib/authedFetch.ts` — used by every real call.
  - `src/lib/api/api-client.ts` — `ApiClient` class with methods like
    `syncEmails`, `getSyncHistory`, `getProcessedEmails` that hit
    `/api/emails/sync`, `/api/emails/history`, `/api/emails/job-related`
    — **none of those endpoints exist on the backend**. Uses a different
    env var (`NEXT_PUBLIC_API_URL`) than the rest of the app
    (`NEXT_PUBLIC_API_BASE_URL`). Not imported anywhere.
  - **Fix:** delete `src/lib/api/`.

- 🟡 **`AuthContext` force-refreshes the ID token on every auth state change**
  `src/lib/contexts/AuthContext.tsx:32-38`
  - `firebaseUser.getIdToken(true)` runs on every page reload / tab focus.
  - **Fix:** drop the force refresh — `authedFetch` already refreshes on
    401, and `getIdToken(false)` auto-refreshes when expired.

- 🟡 **`authStore` persists the Firebase `User` object**
  `src/store/authStore.ts:14-26`
  - Most of the `User` shape is non-serializable internals; only
    enumerable own properties survive JSON. Stale `localStorage` can
    cause subtle "looks logged in but actually not" states.
  - **Fix:** persist `{ uid, email }` only; drop `user` from the store.

- 🟡 **Settings page mixes raw `fetch` with `authedFetch`**
  `src/app/settings/page.tsx:62, 90, 116`
  - `fetchStatus` and `handleDisconnect` use raw `fetch` with a manual
    `Authorization` header; `handleConnect` uses `authedFetch`.
  - **Fix:** route all three through `authedFetch` for consistent
    401-retry behaviour.

### UX / polish

- 🟡 **Hardcoded marketing copy that may misrepresent the product**
  `src/sections/heroSection.tsx` and `src/app/page.tsx`
  - "South Africa's #1 Job Tracker", "10k+ Active Users", "50k+ J*bs
    Tracked", "4.9★", "Trusted by job seekers across South Africa".
  - For a solo passion project, these are likely overclaims.
  - **Fix:** replace with honest copy or remove the stat blocks.

- 🟡 **Newsletter form has no backend**
  `src/sections/footer.tsx:22-29`
  - Submit just sets local `subscribed=true` for 3 seconds.
  - **Fix:** wire to a real list (or remove the form to avoid setting an
    expectation).

- 🟡 **`/dashboard` page does not gate on auth**
  `src/app/dashboard/page.tsx`
  - Renders `<DashboardSection />` (which queries `?userId=…` and shows
    loading) and `<JobApplicationForm />` (which renders "Please log in"
    when `user` is null). The two children handle the absent-user state
    differently. The page itself never redirects to `/auth`.
  - **Fix:** add a top-level redirect in the layout/page when
    `!authLoading && !user`.

- 🟡 **`?userId=` query param on `GET /jobapplications` is ignored**
  `src/sections/dashboard.tsx:69` and `JobApplicationForm.tsx:464`
  - Backend uses the JWT, not the query. Sending the param is harmless
    but misleading.
  - **Fix:** drop the query param.

- 🟢 **`MetricCard` (`src/components/metricCard.tsx`) is unused**
  Dashboard renders its own bespoke metric cards inline.
  - **Fix:** delete or refactor the dashboard to use it.

- 🟢 **`heroSection.tsx` carries the full original implementation as a
  commented-out block**
  Lines 1-61. Clutters the file; git history is the right home for old
  versions.
  - **Fix:** delete.

- 🟢 **Typo: `about="enter your password"` on an `<input>`**
  `src/app/auth/page.tsx:233`
  - Looks like a stray attribute. Browsers ignore it.
  - **Fix:** remove.

- 🟢 **`window.auth = auth` for "debugging"**
  `src/lib/firebase.ts:19-21`
  - Useful in dev, but ships to production too.
  - **Fix:** guard with `process.env.NODE_ENV !== "production"`.

- 🟢 **Bell icon in the navbar is decorative**
  `src/sections/navbar.tsx:137-140`
  - Renders an "unread" orange dot with no click handler and no real
    feed.
  - **Fix:** wire to something or remove until ready.

- 🟢 **`README.md` is the create-next-app template**
  Rewritten in this pass.

### Type & build hygiene

- 🟡 **`as any` cast on `window`**
  `src/lib/firebase.ts:20`
  - `(window as any).auth = auth`.
  - **Fix:** declare a global augmentation or drop the assignment.

- 🟡 **`JobApplication` interface duplicated in two files**
  `src/sections/dashboard.tsx:21-31` and `src/sections/JobApplicationForm.tsx:27-36`
  - Slightly different (dashboard adds `jobNumber`).
  - **Fix:** extract to `src/lib/types.ts` and import from both.

- 🟡 **`next.config.ts` is empty**
  Not really a bug, but no config means no image-domain whitelisting, no
  experimental flags, no headers/redirects — flag for when you need them.
