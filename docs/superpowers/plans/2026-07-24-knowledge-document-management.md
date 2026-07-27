# Knowledge Document Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every persisted knowledge document discoverable and manageable after upload, expose honest version/parse/OCR/index state, and connect existing retry, disable, delete-request, and chunk-review capabilities to the admin UI.

**Architecture:** A read-only `KnowledgeDocumentAdministrationQuery` projects safe management records directly from persisted document, version, OCR, preview, durable-job, and index-job tables. Existing upload, index, and cleanup services remain the mutation authorities; their management entry points gain explicit expected-state versions and sanitized administration audits. The Vue list and detail pages consume typed records and reuse the completed knowledge-tag selector only where indexing needs tags.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, Entity Framework Core/MySQL, xUnit v3 with Microsoft Testing Platform, Vue 3, TypeScript, Element Plus, Vitest, Vite.

## Global Constraints

- Work in `H:\Codex\WechatRobot\.worktrees\codex-wechatrobot-mvp` on `codex/wechatrobot-mvp`.
- Human handoff, Enterprise WeChat member sync, agent selectors, proactive handoff, and handoff pause-policy UI remain deferred.
- Reuse `/api/knowledge/tags/options` and `KnowledgeTagSelector.vue`; do not restore manual tag UUID entry.
- Do not expose `StagedContent`, `ObjectKey`, SHA values as credentials, provider headers, signed query strings, model/OCR/OSS credentials, or raw durable-job payloads.
- Persisted raw statuses remain the source of truth. Do not claim OCR, parsing, indexing, cleanup, or deletion completed when the corresponding persisted record does not say so.
- A physical-delete request is asynchronous cleanup, not immediate deletion. The UI must say “已提交物理删除请求” until persisted cleanup evidence says otherwise.
- Retry is available only when the latest version is a retryable failed upload with staged content still present.
- Disable and physical-delete request remain separate operations.
- Every management mutation carries `ExpectedStateVersion`.
- KnowledgeOperator may list, read, retry upload, and disable. Only Admin may request physical deletion.
- Retry, disable, and physical-delete request write sanitized `AdministrationAuditEntity` rows.
- Do not stop the running API or Worker. Use isolated build outputs when default binaries are locked.

---

## Current Capability Truth

Already implemented and to be reused:

- multipart upload and provider-failure persistence;
- `POST /api/knowledge/documents/{documentId}/retry-upload`;
- `POST /api/knowledge/documents/{documentId}/disable`;
- `DELETE /api/knowledge/documents/{documentId}/physical`;
- persisted document/version rows, preview revisions, OCR pages, durable jobs, index jobs, and active-version provenance;
- document chunk/preview/index detail page route;
- knowledge-tag options and selector.

Missing in this phase:

- document list, safe detail, and version-history queries;
- expected-state-version HTTP contracts on management actions;
- management audits for retry, disable, and delete request;
- list/detail UI, failed-upload retry entry, disable entry, delete-request entry, and navigation into any persisted version.

No schema migration is planned. If implementation proves an audit or concurrency invariant cannot be met with current columns, stop and amend this plan before adding a migration.

---

## Backend Contracts

Create `src/server/WechatRobot.Application/Knowledge/KnowledgeDocumentAdministrationContracts.cs`:

```csharp
public sealed record KnowledgeDocumentSummary(
    Guid Id,
    string Title,
    string Status,
    int StateVersion,
    Guid? ActiveVersionId,
    int VersionCount,
    Guid? LatestVersionId,
    int? LatestVersion,
    string? LatestVersionStatus,
    string? LatestFailureReason,
    bool CanRetryUpload,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record KnowledgeDocumentPage(
    IReadOnlyList<KnowledgeDocumentSummary> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record KnowledgeDocumentVersionSummary(
    Guid Id,
    int Version,
    string OriginalFileName,
    string SafeFileName,
    string ContentType,
    long SizeBytes,
    string Status,
    string? FailureReason,
    bool IsPublished,
    bool HasPublicObject,
    int PreviewRevision,
    int PreviewCount,
    int ApprovedChunkCount,
    int OcrPageCount,
    int OcrFailedPageCount,
    IReadOnlyList<KnowledgeDocumentJobSummary> UploadAndParseJobs,
    IReadOnlyList<KnowledgeDocumentIndexJobSummary> IndexJobs,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record KnowledgeDocumentDetail(
    KnowledgeDocumentSummary Document,
    IReadOnlyList<KnowledgeDocumentVersionSummary> Versions);

public sealed record KnowledgeDocumentStateRequest(int ExpectedStateVersion);
```

Job summaries expose only IDs, operation/type, status, attempt count, sanitized failure category/summary, and timestamps. They never expose payload JSON, lease owner, object key, collection credentials, or provider response bodies.

Produce these routes:

```text
GET    /api/knowledge/documents
GET    /api/knowledge/documents/{documentId}
GET    /api/knowledge/documents/{documentId}/versions
POST   /api/knowledge/documents/{documentId}/retry-upload
POST   /api/knowledge/documents/{documentId}/disable
DELETE /api/knowledge/documents/{documentId}/physical?expectedStateVersion={version}
```

List query:

```text
query, status, page, pageSize
```

`status` is either absent or an exact persisted document status. Do not invent a second status vocabulary.

Stable errors:

```text
document-not-found
document-concurrency-conflict
document-not-retryable
document-delete-requested
document-state-conflict
```

Concurrency conflicts return:

```json
{
  "error": "document-concurrency-conflict",
  "current": { "id": "...", "status": "...", "stateVersion": 4 }
}
```

---

### Task 1: Add safe paged document projections

**Files:**

- Create: `src/server/WechatRobot.Application/Knowledge/KnowledgeDocumentAdministrationContracts.cs`
- Create: `src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeDocumentAdministrationQuery.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentAdministrationQueryTests.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentAdministrationMySqlTests.cs`

- [ ] Write failing tests for stable updated-descending/id ordering, text/status filters, page bounds, version ordering, and empty documents.
- [ ] Seed upload, parse, OCR, preview, approved chunk, and index records; assert counts and persisted statuses are projected without payloads or staged bytes.
- [ ] Add a malformed/secret-bearing durable payload and assert no returned contract contains it.
- [ ] Implement queries using server-side projection for page selection and bounded follow-up queries for only the selected document IDs.
- [ ] Prove MySQL query translation and ordering with the existing `MySqlFixture`.
- [ ] Run:

```powershell
dotnet test tests\server\WechatRobot.IntegrationTests\WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*KnowledgeDocumentAdministrationQueryTests' '*KnowledgeDocumentAdministrationMySqlTests' --minimum-expected-tests 1
```

- [ ] Commit:

```powershell
git commit -m "feat: query knowledge document administration"
```

---

### Task 2: Expose list, detail, and version-history endpoints

**Files:**

- Modify: `src/server/WechatRobot.Api/Knowledge/DocumentEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Program.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentAdministrationEndpointTests.cs`

- [ ] Write failing route/authorization tests for anonymous 401, HumanAgent 403, KnowledgeOperator/Admin success, missing 404, and page normalization.
- [ ] Assert list and detail JSON omit `stagedContent`, `objectKey`, `payloadJson`, secrets, authorization headers, and signed query strings.
- [ ] Register `KnowledgeDocumentAdministrationQuery`.
- [ ] Map the three GET routes under the existing KnowledgeOperator group.
- [ ] Keep `/versions` as an explicit route even if detail also embeds versions; it supports bounded refresh of the version timeline.
- [ ] Run endpoint and query tests.
- [ ] Commit:

```powershell
git commit -m "feat: expose knowledge document queries"
```

---

### Task 3: Version and audit document management mutations

**Files:**

- Modify: `src/server/WechatRobot.Application/Knowledge/DocumentUploadService.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/KnowledgeDocumentStore.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs`
- Modify: `src/server/WechatRobot.Api/Knowledge/DocumentEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Knowledge/KnowledgeIndexEndpoints.cs`
- Modify existing upload/index/concurrency tests.
- Create: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentAdministrationMutationTests.cs`

- [ ] Write failing tests for current/stale `ExpectedStateVersion` on retry, disable, and physical-delete request.
- [ ] Assert retry rejects non-failed/latest-missing-staged-content states without writing an audit.
- [ ] Assert disable is idempotent only at the current version and does not claim physical deletion.
- [ ] Assert physical-delete request remains Admin-only, sets `IsDeleteRequested`, disables versions, cancels conflicting jobs, queues cleanup, and returns HTTP 202.
- [ ] Add audit actions:

```text
knowledge-document.retry-upload
knowledge-document.disable
knowledge-document.request-physical-delete
```

- [ ] Audit only document/version IDs, prior/new status, state versions, and safe failure categories; never file content, URLs, object keys, job payloads, or credentials.
- [ ] Convert confirmed state-version races to `document-concurrency-conflict`; do not blanket-convert unrelated database/provider failures.
- [ ] Run all document upload, concurrency, index-disable, cleanup, and new mutation tests.
- [ ] Commit:

```powershell
git commit -m "feat: govern knowledge document actions"
```

---

### Task 4: Add the typed frontend document client

**Files:**

- Modify: `src/web/wechatrobot-admin/src/api/knowledge.ts`
- Create: `src/web/wechatrobot-admin/src/api/knowledgeDocuments.spec.ts`

- [ ] Add types matching the backend contracts exactly.
- [ ] Add `listDocuments`, `getDocument`, `getDocumentVersions`, `retryDocumentUpload`, `disableDocument`, and `requestPhysicalDelete`.
- [ ] Encode IDs and send `expectedStateVersion` only in the documented body/query location.
- [ ] Add API tests that lock route paths, query names, concurrency payloads, and absence of secret-bearing fields.
- [ ] Run API tests and typecheck.
- [ ] Commit:

```powershell
git commit -m "feat: add knowledge document admin client"
```

---

### Task 5: Replace the upload-only page with document management

**Files:**

- Modify: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentsView.vue`
- Create: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentsView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/task16-operational.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/element-plus-operational.spec.ts`

- [ ] Preserve the existing multipart upload, progress, DOC guidance, and public OSS warning.
- [ ] Add searchable/status-filtered pagination with document title, persisted status, latest version/status, failure summary, version count, and updated time.
- [ ] Link each row to:

```text
/knowledge/documents/{documentId}
```

- [ ] Show retry only when the latest version is retryable according to the server record; never infer retryability only from display text.
- [ ] On 409 replace the stale row with server `current` and show concurrency copy.
- [ ] Keep upload and list errors independently visible.
- [ ] Run page regressions and typecheck.
- [ ] Commit:

```powershell
git commit -m "feat: manage knowledge documents"
```

---

### Task 6: Add document detail, version history, and safe actions

**Files:**

- Create: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentManagementView.vue`
- Create: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentManagementView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/router/index.ts`
- Modify: `src/web/wechatrobot-admin/src/router/index.spec.ts`

- [ ] Add the `/knowledge/documents/:documentId` route for Admin and KnowledgeOperator.
- [ ] Render persisted document state and newest-first version timeline with upload/parse/OCR/preview/index evidence.
- [ ] Link every version to the existing chunk workflow:

```text
/knowledge/documents/{documentId}/versions/{versionId}
```

- [ ] Add retry and disable for KnowledgeOperator.
- [ ] Show physical-delete request only to Admin and require:

```text
这会停用文档并提交异步物理清理，期间不可上传新版本。确认继续？
```

- [ ] Display “删除请求已受理，等待后台清理”，never “已物理删除” from the HTTP 202 response alone.
- [ ] Refresh from server after every successful action and replace stale state from 409 `current`.
- [ ] Show no raw object URL, object key, hash, staged content, or job payload.
- [ ] Run view/router tests and typecheck.
- [ ] Commit:

```powershell
git commit -m "feat: add knowledge document detail"
```

---

### Task 7: Run the phase gate and update the roadmap

**Files:**

- Modify: `docs/superpowers/plans/2026-07-24-frontend-backend-alignment-roadmap.md`
- Create the next detailed plan: `docs/superpowers/plans/2026-07-24-typed-system-settings.md`

- [ ] Confirm `git status --short --branch` and preserve running API/Worker processes.
- [ ] Run all document administration, upload, parsing/OCR, index, cleanup, and router/view tests.
- [ ] Run full server unit and contract suites with repository-local isolated `net10.0` outputs where fixture discovery requires them.
- [ ] Run serial isolated full solution build and record warnings/errors exactly.
- [ ] Run frontend typecheck, full test suite, and production build.
- [ ] Scan production responses/views for staged bytes, object keys, payload JSON, signed URLs, secrets, false physical-delete claims, and manual tag IDs.
- [ ] Mark document management `Completed`, record counts/commits/skips, and mark typed settings `Planned`.
- [ ] Commit:

```powershell
git commit -m "docs: complete knowledge document management"
```

---

## Plan Self-Review

- Scope covers the missing list, safe detail, version history, persisted parse/OCR/index evidence, retry, disable, physical-delete request, and navigation back into chunk review.
- Existing upload/index/cleanup authorities are reused; the plan does not invent a second document lifecycle.
- Every management action has an authorization boundary, expected state version, stable conflict, audit, UI entry, and regression test.
- Contracts expose persisted truth and safe counts, not file bytes, raw job payloads, secret URLs, or fabricated completion states.
- Knowledge tags use the completed options API and selector; manual UUID entry stays removed.
- Human handoff and Enterprise WeChat member mapping remain out of scope.
