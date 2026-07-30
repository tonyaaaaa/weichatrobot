# Knowledge Document Version Workbench Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make automatic-source knowledge readable and safely correctable through a source-aware, versioned workbench without mutating an active approved version.

**Architecture:** Add a bounded MySQL workbench query for approved chunks, source evidence, lineage and revision availability, plus a transactional revision service that copies approved chunks into editable previews. Reuse the existing preview approval and durable indexing pipeline so the active version changes only after indexing succeeds. Replace the current preview-centric Vue page with a source-aware workbench that keeps existing upload and index capabilities.

**Tech Stack:** ASP.NET Core 10 Minimal APIs, EF Core with MySQL 5.7 compatibility, xUnit v3/Microsoft Testing Platform, Vue 3 Composition API, TypeScript, Element Plus, Vitest.

## Global Constraints

- Existing approved, indexed and active `KnowledgeChunk` rows remain immutable.
- `AdministrationRevision` uses the existing string `SourceKind` field; no migration is required.
- A document may have at most one mutable administrator revision.
- The active version changes only through the existing successful index activation path.
- Knowledge tags are replaced only when indexing completes; do not create a separate tag-save endpoint.
- Never expose staged bytes, object keys, callback data, robot identifiers or secret-bearing URLs.
- Preserve existing authorization, administration audit, state-version concurrency and preview-revision concurrency.
- Import every new Element Plus component together with its dedicated `style/css` entry and regression test.
- Preserve unrelated working-tree changes; do not commit, stage, deploy or rewrite them without explicit user authorization.

---

## File Structure

### Backend

- Create `src/server/WechatRobot.Application/Knowledge/KnowledgeDocumentWorkbenchContracts.cs`
  - Owns the source-evidence, approved-content, revision-link and workbench response records.
- Create `src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeDocumentWorkbenchQuery.cs`
  - Reads a single document/version workbench projection and performs historical review-source fallback.
- Create `src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeDocumentRevisionService.cs`
  - Owns transactional administrator-revision creation and conflict contracts.
- Modify `src/server/WechatRobot.Api/Knowledge/DocumentEndpoints.cs`
  - Adds workbench GET and revision POST transport boundaries.
- Modify `src/server/WechatRobot.Api/Program.cs`
  - Registers the query and revision services.
- Modify `src/server/WechatRobot.Infrastructure/Persistence/EfKnowledgeCandidateStore.cs`
  - Persists direct source evidence for newly approved conversation-review versions.
- Test `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentWorkbenchQueryTests.cs`
- Test `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentWorkbenchEndpointTests.cs`
- Test `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentRevisionServiceTests.cs`
- Test `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentRevisionEndpointTests.cs`
- Modify `tests/server/WechatRobot.IntegrationTests/Knowledge/HumanAnswerReviewTests.cs`
  - Verifies new review versions persist their source relationship.

### Frontend

- Modify `src/web/wechatrobot-admin/src/api/knowledge.ts`
  - Adds workbench and revision DTOs and client methods.
- Modify `src/web/wechatrobot-admin/src/views/knowledge/DocumentDetailView.vue`
  - Implements the three-tab workbench, revision entry, source-aware editing and index button semantics.
- Modify `src/web/wechatrobot-admin/src/views/knowledge/DocumentDetailView.spec.ts`
  - Covers read-only content, source evidence, revision creation/continuation and tag/index labels.
- Modify `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentManagementView.vue`
  - Adds `AdministrationRevision` source text and keeps document actions/upload rules source-aware.
- Modify `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentManagementView.spec.ts`
  - Covers administrator-revision rendering and automatic-source upload suppression.
- Modify `src/web/wechatrobot-admin/src/main.ts`
  - Adds the dedicated dropdown, dropdown-menu and dropdown-item style entries.
- Modify `src/web/wechatrobot-admin/src/styles.spec.ts`
  - Locks the new Element Plus style dependencies.

---

### Task 1: Persist and Query Source-Aware Workbench Data

**Files:**
- Create: `src/server/WechatRobot.Application/Knowledge/KnowledgeDocumentWorkbenchContracts.cs`
- Create: `src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeDocumentWorkbenchQuery.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/EfKnowledgeCandidateStore.cs`
- Modify: `src/server/WechatRobot.Api/Program.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentWorkbenchQueryTests.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Knowledge/HumanAnswerReviewTests.cs`

**Interfaces:**
- Consumes: `WechatRobotDbContext`, `KnowledgeDocumentVersionEntity.SourceConversationMessageId`, `KnowledgeCandidateEntity.KnowledgeDocumentVersionId`.
- Produces:

```csharp
public sealed record KnowledgeDocumentWorkbench(
    Guid DocumentId,
    string DocumentTitle,
    string DocumentStatus,
    int DocumentStateVersion,
    Guid? ActiveVersionId,
    KnowledgeWorkbenchVersion Version,
    IReadOnlyList<KnowledgeWorkbenchChunk> Chunks,
    KnowledgeWorkbenchSourceEvidence? SourceEvidence,
    KnowledgeWorkbenchRevisionLink? EditableRevision,
    bool CanCreateRevision);

public sealed class KnowledgeDocumentWorkbenchQuery
{
    public Task<KnowledgeDocumentWorkbench?> GetAsync(
        Guid documentId,
        Guid versionId,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write failing workbench query tests**

Add tests proving:

```csharp
[Fact]
public async Task Private_chat_workbench_returns_approved_chunks_tags_and_source_message()
```

```csharp
[Fact]
public async Task Conversation_review_workbench_falls_back_through_candidate_for_legacy_source()
```

```csharp
[Fact]
public async Task Workbench_returns_empty_source_evidence_without_guessing_when_relationship_is_missing()
```

Assert that staged bytes, object keys and secret-shaped callback fields are absent from serialized responses.

- [ ] **Step 2: Run the query tests and verify the expected failure**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~KnowledgeDocumentWorkbenchQueryTests"
```

Expected: compilation fails because the workbench contracts/query do not exist.

- [ ] **Step 3: Add the workbench contracts**

Define bounded records for:

```csharp
public sealed record KnowledgeWorkbenchChunk(
    Guid Id,
    int Sequence,
    string Text,
    int? PageNumber,
    string? Question,
    IReadOnlyList<string> Synonyms,
    string? Answer,
    string Status);

public sealed record KnowledgeWorkbenchSourceEvidence(
    string ChannelType,
    int? RoomType,
    string ActorDisplayName,
    string Text,
    DateTime ReceivedAtUtc);

public sealed record KnowledgeWorkbenchRevisionLink(
    Guid VersionId,
    int Version,
    int PreviewRevision);
```

The version record must include source kind, actor, batch, change kind, superseded version, status, publication state, tags and index job summary.

- [ ] **Step 4: Implement the bounded workbench query**

Implement one-document/one-version queries:

1. Validate the version belongs to the requested document.
2. Read approved chunks ordered by `Sequence`.
3. Read version tags through `KnowledgeChunkTag`.
4. Resolve the direct source message.
5. For legacy `ConversationReview`, resolve
   `KnowledgeCandidate.KnowledgeDocumentVersionId -> SourceConversationMessageId ?? QuestionMessageId`.
6. Locate at most one mutable `AdministrationRevision` with status `uploaded` or `preview`.
7. Compute `CanCreateRevision` only when the document is active/writable, the version has approved chunks and no mutable revision exists.

Use bounded queries and avoid loading unrelated documents or conversations.

- [ ] **Step 5: Persist direct source evidence for new conversation-review versions**

When `EfKnowledgeCandidateStore` creates a `ConversationReview` version, set:

```csharp
SourceConversationMessageId = candidate.SourceConversationMessageId ?? candidate.QuestionMessageId,
SourceActorDisplayName = sourceMessage?.SenderDisplayName,
StagedContent = Encoding.UTF8.GetBytes($"问题：{candidate.Question}\n答案：{answer}")
```

Load only the selected source message. Preserve the existing candidate transaction and idempotency behavior.

- [ ] **Step 6: Register the query and run focused tests**

Register:

```csharp
builder.Services.AddScoped<KnowledgeDocumentWorkbenchQuery>();
```

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~KnowledgeDocumentWorkbenchQueryTests|FullyQualifiedName~HumanAnswerReviewTests"
```

Expected: all selected tests pass.

- [ ] **Step 7: Review the task diff**

Run:

```powershell
git diff --check
git diff -- src/server/WechatRobot.Application/Knowledge src/server/WechatRobot.Infrastructure/Knowledge src/server/WechatRobot.Infrastructure/Persistence/EfKnowledgeCandidateStore.cs tests/server/WechatRobot.IntegrationTests/Knowledge
```

Confirm unrelated files are untouched. Do not stage or commit.

---

### Task 2: Add Workbench and Administrator-Revision APIs

**Files:**
- Create: `src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeDocumentRevisionService.cs`
- Modify: `src/server/WechatRobot.Api/Knowledge/DocumentEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Program.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentWorkbenchEndpointTests.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentRevisionServiceTests.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentRevisionEndpointTests.cs`

**Interfaces:**
- Consumes: `KnowledgeDocumentWorkbenchQuery.GetAsync`, `WechatRobotDbContext`, current claims principal, `TimeProvider`.
- Produces:

```csharp
public sealed record CreateKnowledgeRevisionCommand(
    Guid DocumentId,
    Guid SourceVersionId,
    int ExpectedDocumentStateVersion,
    string ActorId,
    string ActorDisplayName);

public sealed record KnowledgeRevisionResult(
    Guid DocumentId,
    Guid VersionId,
    int Version,
    int PreviewRevision);

public sealed class KnowledgeDocumentRevisionService
{
    public Task<KnowledgeRevisionResult> CreateAsync(
        CreateKnowledgeRevisionCommand command,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write failing revision service tests**

Cover:

```csharp
[Fact]
public async Task Create_revision_copies_approved_chunks_to_editable_previews_without_switching_active_version()
```

```csharp
[Fact]
public async Task Create_revision_preserves_question_answer_synonyms_page_and_table_metadata()
```

```csharp
[Fact]
public async Task Create_revision_rejects_state_version_conflict_disabled_delete_pending_and_empty_source()
```

```csharp
[Fact]
public async Task Create_revision_returns_existing_mutable_revision_conflict()
```

- [ ] **Step 2: Run the service tests and verify failure**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~KnowledgeDocumentRevisionServiceTests"
```

Expected: compilation fails because `KnowledgeDocumentRevisionService` does not exist.

- [ ] **Step 3: Implement transactional revision creation**

Inside one transaction:

1. Lock or concurrency-check the document.
2. Validate `StateVersion`.
3. Validate source ownership and approved chunks.
4. Reject disabled/delete-pending documents.
5. Reject an existing mutable `AdministrationRevision`.
6. Allocate `Version = Max(existing.Version) + 1`.
7. Create a version with:

```csharp
Status = "preview";
PreviewRevision = 1;
SourceKind = "AdministrationRevision";
ChangeKind = "Correction";
SupersedesVersionId = source.Id;
SourceActorDisplayName = command.ActorDisplayName;
IsPublished = false;
```

8. Copy approved chunks into `KnowledgeChunkPreviewEntity` rows.
9. Increment document `StateVersion`.
10. Add an `AdministrationAuditEntity` using the existing safe audit conventions.

Do not change `ActiveVersionId`, Qdrant state or source-version publication.

- [ ] **Step 4: Write failing endpoint contract tests**

Verify:

- `GET /api/knowledge/documents/{documentId}/versions/{versionId}/workbench`
  requires `KnowledgeOperator` and returns safe data.
- `POST /api/knowledge/documents/{documentId}/versions/{versionId}/revisions`
  requires `KnowledgeOperator`.
- Request body is `{ "expectedDocumentStateVersion": 5 }`.
- Success returns `201 Created`.
- Missing records return `404`.
- concurrency and existing-revision conflicts return stable `409` payloads.

- [ ] **Step 5: Add endpoint mappings and safe error contracts**

Add:

```csharp
documents.MapGet(
    "/{documentId:guid}/versions/{versionId:guid}/workbench",
    WorkbenchAsync);
documents.MapPost(
    "/{documentId:guid}/versions/{versionId:guid}/revisions",
    CreateRevisionAsync);
```

Resolve `ActorId` from `ClaimTypes.NameIdentifier` and display name from
`ClaimTypes.Name`. Return only safe revision metadata.

- [ ] **Step 6: Register the revision service and run endpoint tests**

Register:

```csharp
builder.Services.AddScoped<KnowledgeDocumentRevisionService>();
```

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~KnowledgeDocumentWorkbenchEndpointTests|FullyQualifiedName~KnowledgeDocumentRevision"
```

Expected: all selected tests pass.

- [ ] **Step 7: Run MySQL compatibility coverage**

Add the workbench query and revision transaction to the existing MySQL knowledge test fixture, then run against both configured compatibility targets:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~KnowledgeDocumentAdministrationMySqlTests|FullyQualifiedName~KnowledgeDocumentRevisionMySqlTests"
```

Expected: MySQL 5.7-compatible SQL executes without unsupported window, CTE or JSON translation.

---

### Task 3: Add the Frontend Workbench Contract and Read-Only Tabs

**Files:**
- Modify: `src/web/wechatrobot-admin/src/api/knowledge.ts`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/DocumentDetailView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/DocumentDetailView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/main.ts`
- Modify: `src/web/wechatrobot-admin/src/styles.spec.ts`

**Interfaces:**
- Consumes:
  - `GET .../workbench`
  - existing `getDocumentVersions`, `getIndexStatus`, `queueIndex`, `retryIndex`.
- Produces:

```ts
getWorkbench(documentId: string, versionId: string): Promise<KnowledgeDocumentWorkbench>;
createRevision(
  documentId: string,
  versionId: string,
  expectedDocumentStateVersion: number
): Promise<KnowledgeRevisionResult>;
```

- [ ] **Step 1: Write failing API and component tests**

Add API assertions for exact encoded routes and payload.

Add component tests:

```ts
it('shows approved automatic-source chunks instead of an empty preview state')
it('shows source message evidence and explicit missing-evidence state')
it('renders version history from real version data')
it('keeps approved content read only')
```

- [ ] **Step 2: Run focused frontend tests and verify failure**

Run:

```powershell
Set-Location src/web/wechatrobot-admin
npm test -- --run src/api/knowledgeDocuments.spec.ts src/views/knowledge/DocumentDetailView.spec.ts
```

Expected: tests fail because the new client methods and tabs are absent.

- [ ] **Step 3: Add TypeScript contracts and API methods**

Define bounded DTOs matching the C# response exactly. Do not expose staged content,
object keys or internal conversation identifiers.

Implement:

```ts
async getWorkbench(documentId, versionId) {
  return (await apiClient.get(
    `/api/knowledge/documents/${encodeURIComponent(documentId)}` +
    `/versions/${encodeURIComponent(versionId)}/workbench`
  )).data;
}
```

and the matching revision POST.

- [ ] **Step 4: Refactor initial loading to use the workbench response**

Load in parallel:

```ts
const [workbench, indexStatus, versions] = await Promise.all([
  props.api.getWorkbench(props.documentId, props.versionId),
  props.api.getIndexStatus(props.documentId),
  props.api.getDocumentVersions(props.documentId)
]);
```

Do not call `getPreviews` when displaying an approved automatic-source version.

- [ ] **Step 5: Build the three-tab read-only layout**

Use `ElTabs`/`ElTabPane`:

- “已入库内容” renders structured QA or plain chunk text.
- “原始消息” renders source actor, channel, time and exact message.
- “版本历史” renders existing version records and lineage.

Show an explicit empty source-evidence alert when the backend returns no reliable relationship.

- [ ] **Step 6: Verify Element Plus style completeness**

`ElTabs` is already covered. Add the new document-level `ElDropdown`,
`ElDropdownMenu` and `ElDropdownItem` styles:

```ts
import 'element-plus/es/components/dropdown/style/css';
import 'element-plus/es/components/dropdown-menu/style/css';
import 'element-plus/es/components/dropdown-item/style/css';
```

Extend `src/styles.spec.ts` with these exact strings before declaring the UI complete.

- [ ] **Step 7: Run focused tests, typecheck and build**

Run:

```powershell
npm test -- --run src/api/knowledgeDocuments.spec.ts src/views/knowledge/DocumentDetailView.spec.ts
npm run typecheck
npm run build
```

Expected: all commands pass.

---

### Task 4: Wire Revision Editing and Correct Index Buttons

**Files:**
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/DocumentDetailView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/DocumentDetailView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentManagementView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentManagementView.spec.ts`

**Interfaces:**
- Consumes: `createRevision`, existing preview CRUD/approve APIs, existing index/reindex/retry APIs.
- Produces: complete A-scheme version workflow.

- [ ] **Step 1: Write failing revision and index-button component tests**

Cover:

```ts
it('creates a revision and navigates to its editable version')
it('continues the existing mutable revision without creating another')
it('allows preview editing for AdministrationRevision but hides regenerate')
it('labels unchanged tags as reindex current version')
it('labels changed tags as save tags and reindex')
it('disables index submission while a job is in progress')
it('shows retry index only for the latest failed job')
```

- [ ] **Step 2: Run the focused tests and verify failure**

Run:

```powershell
Set-Location src/web/wechatrobot-admin
npm test -- --run src/views/knowledge/DocumentDetailView.spec.ts src/views/knowledge/KnowledgeDocumentManagementView.spec.ts
```

Expected: the new behavior assertions fail.

- [ ] **Step 3: Add revision creation and continuation actions**

Before creating, show an Element Plus confirmation. On success navigate to:

```ts
`/knowledge/documents/${documentId}/versions/${result.versionId}`
```

When `workbench.editableRevision` exists, render “继续编辑修订 vN” and navigate without POST.

- [ ] **Step 4: Separate mutability from source generation**

Use explicit computed capabilities:

```ts
const canEditPreviews = computed(() =>
  workbench.value?.version.sourceKind === 'AdministrationRevision' &&
  workbench.value.version.status === 'preview');

const canGenerateFromSource = computed(() =>
  workbench.value?.version.sourceKind === 'DocumentUpload' &&
  ['uploaded', 'preview'].includes(workbench.value.version.status));
```

Keep edit/split/multi-merge/delete/approve for editable administrator revisions, but hide “重新生成预览” unless a real document source exists.

- [ ] **Step 5: Implement tag dirty-state and index button labels**

Store initial IDs separately:

```ts
const initialTagIds = ref<string[]>([]);
const tagsChanged = computed(() =>
  [...selectedTagIds.value].sort().join(',') !==
  [...initialTagIds.value].sort().join(','));
```

Render:

- unchanged: “重新索引当前版本”
- changed: “保存标签并重新索引”
- failed job: separate “重试索引”

Use existing `queueIndex(..., true)` and do not add a tag-save call.

- [ ] **Step 6: Keep document-level actions and source rules correct**

Add `AdministrationRevision -> 管理员修订` display mapping.

Keep upload-new-version visible only when the document’s lineage is file-upload based. Keep stop/delete behavior and permissions unchanged.

- [ ] **Step 7: Run focused and complete frontend verification**

Run:

```powershell
npm test -- --run src/views/knowledge/DocumentDetailView.spec.ts src/views/knowledge/KnowledgeDocumentManagementView.spec.ts
npm test -- --run
npm run typecheck
npm run build
```

Expected: all frontend tests, typecheck and production build pass.

---

### Task 5: Cross-Boundary Regression and Runtime Verification

**Files:**
- Modify only tests or documentation proven necessary by failures in this task.
- Do not opportunistically refactor unrelated code.

**Interfaces:**
- Consumes all deliverables from Tasks 1–4.
- Produces verified backend/frontend behavior and a deployment-ready change set.

- [ ] **Step 1: Run backend focused suites**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~KnowledgeDocument|FullyQualifiedName~ChunkPreview|FullyQualifiedName~HumanAnswerReview"
```

Expected: all selected integration tests pass.

- [ ] **Step 2: Run backend complete unit, contract and integration suites**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj
```

Report unrelated baseline failures separately; do not hide or fix them without evidence.

- [ ] **Step 3: Run frontend complete verification**

Run:

```powershell
Set-Location src/web/wechatrobot-admin
npm test -- --run
npm run typecheck
npm run build
```

- [ ] **Step 4: Run diff and secret hygiene checks**

Run:

```powershell
Set-Location H:\Codex\WechatRobot
git diff --check
git status --short
git diff --stat
```

Review changed files and verify no credential, callback token, robot identifier, `.local` value or generated preview artifact is tracked.

- [ ] **Step 5: Run local runtime smoke tests when configuration is available**

Start API and Worker from `.local` with `WECHATROBOT_ENV_FILE` pointing to
`.local/.env`, then verify:

```text
GET http://127.0.0.1:5268/health/live
GET /api/knowledge/documents/{documentId}/versions/{versionId}/workbench
POST /api/knowledge/documents/{documentId}/versions/{versionId}/revisions
```

Use test records only. Confirm old active knowledge remains queryable until the revision index succeeds.

- [ ] **Step 6: Browser visual verification**

Check desktop and narrow viewport behavior:

- tabs have Element Plus styling;
- approved content is readable and immutable;
- source evidence and missing-source states are distinct;
- sidebar moves below content on narrow screens;
- buttons do not overflow;
- confirmation dialogs use Element Plus, not native browser alerts.

- [ ] **Step 7: Final scope review**

Compare the implementation with
`docs/superpowers/specs/2026-07-30-knowledge-document-version-workbench-design.md`.
Confirm every accepted requirement has implementation and test evidence. Do not commit, push or deploy unless the user explicitly requests it.
