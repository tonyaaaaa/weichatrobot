# Knowledge Document List Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the knowledge document list show authoritative knowledge-tag bindings and version sources, add server-side filters, and replace the permanently expanded upload form with an accessible dialog while preserving every existing document operation.

**Architecture:** Extend the existing document administration summary and query rather than creating a parallel endpoint. The query selects one effective version per document (active version, otherwise latest), uses that same version for source and tag truth, applies source/tag filters before pagination, and projects safe summaries. The Vue page reuses the existing knowledge-tag options API and document mutations, with a dense desktop table and narrow-screen cards.

**Tech Stack:** ASP.NET Core 10 Minimal APIs, EF Core with MySQL 5.7 and in-memory integration tests, Vue 3 Composition API, TypeScript, Element Plus, Vitest.

## Global Constraints

- Do not create a new `KnowledgeBase` entity; “绑定知识库” means existing `KnowledgeTag` records.
- Do not fabricate “全局知识” for an empty binding; empty bindings display “未绑定”.
- Do not add a database migration for this feature.
- Preserve current detail, retry, authorization, optimistic-concurrency, and physical-delete behavior.
- Keep all list filters server-side and apply them before `Total`, ordering, and pagination.
- Keep MySQL 5.7 translation compatibility.
- Keep secrets, object keys, staged content, upstream responses, and raw failure details out of API responses.
- Element Plus usage requires logic imports, dedicated style imports, style regression coverage, and visual verification.
- Work in the current checkout without a new branch or worktree.
- Preserve all unrelated working-tree changes.
- Do not commit, push, or deploy without explicit user authorization.

---

### Task 1: Extend the document administration query contract

**Files:**
- Modify: `src/server/WechatRobot.Application/Knowledge/KnowledgeDocumentAdministrationContracts.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeDocumentAdministrationQuery.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentAdministrationQueryTests.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentAdministrationMySqlTests.cs`

**Interfaces:**
- Produces:
  - `KnowledgeDocumentTagSummary(Guid Id, string Name)`
  - `KnowledgeDocumentSummary.SourceKind`
  - `KnowledgeDocumentSummary.SourceActorDisplayName`
  - `KnowledgeDocumentSummary.Tags`
  - `KnowledgeDocumentVersionSummary.Tags`
  - `KnowledgeDocumentAdministrationQuery.ListAsync(string? query, string? status, string? sourceKind, Guid? tagId, int page, int pageSize, CancellationToken cancellationToken)`
- Effective version: `ActiveVersionId` when present, otherwise greatest version number.

- [x] **Step 1: Add failing source/tag projection tests**

Create test data containing:

```csharp
var activeVersion = Version(document.Id, 1, "active");
activeVersion.SourceKind = "PrivateChatDirect";
activeVersion.SourceActorDisplayName = "张伟";
document.ActiveVersionId = activeVersion.Id;

var newerDraft = Version(document.Id, 2, "preview");
newerDraft.SourceKind = "DocumentUpload";

var tag = new KnowledgeTagEntity
{
    Name = "加拿大签证",
    NormalizedName = "加拿大签证"
};
var chunk = new KnowledgeChunkEntity
{
    KnowledgeDocumentVersionId = activeVersion.Id,
    Sequence = 1,
    Text = "签证进度",
    Status = "approved"
};
database.KnowledgeChunkTags.Add(new()
{
    KnowledgeChunkId = chunk.Id,
    KnowledgeTagId = tag.Id
});
```

Assert the summary uses `PrivateChatDirect`, `张伟`, and the active-version tag,
not the newer draft source.

- [x] **Step 2: Add failing server-side filter tests**

Call:

```csharp
await query.ListAsync(
    query: null,
    status: null,
    sourceKind: "PrivateChatDirect",
    tagId: tag.Id,
    page: 1,
    pageSize: 20,
    cancellationToken);
```

Assert both `Items` and `Total` include only documents whose effective version
matches both filters. Add an unbound document assertion returning `Tags = []`.

- [x] **Step 3: Run the focused query tests and confirm expected compilation failures**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter FullyQualifiedName~KnowledgeDocumentAdministrationQueryTests
```

Expected: fail because the new summary properties and `ListAsync` parameters do
not exist.

- [x] **Step 4: Implement the minimal query contract**

Add:

```csharp
public sealed record KnowledgeDocumentTagSummary(Guid Id, string Name);
```

Extend `KnowledgeDocumentSummary` with:

```csharp
string SourceKind,
string? SourceActorDisplayName,
IReadOnlyList<KnowledgeDocumentTagSummary> Tags
```

Extend `KnowledgeDocumentVersionSummary` with the same version-specific
`Tags` collection.

Extend `VersionRow` with `SourceKind` and `SourceActorDisplayName`. Determine
the effective version consistently for source and tags. Load tag rows in bounded
GUID batches and stable-sort by `Name`, then `Id`. Reuse the loaded bindings to
populate every version in document detail, not only the effective version.

Apply `sourceKind` and `tagId` to the database query before `CountAsync`,
`Skip`, and `Take`. Do not filter an already paged in-memory result.

- [x] **Step 5: Add MySQL translation coverage**

Seed a source and real chunk-tag binding in
`KnowledgeDocumentAdministrationMySqlTests`, call `ListAsync` with both new
filters, and assert the expected document is returned.

- [x] **Step 6: Run query and MySQL tests**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~KnowledgeDocumentAdministrationQueryTests|FullyQualifiedName~KnowledgeDocumentAdministrationMySqlTests"
```

Expected: all selected tests pass. If the MySQL fixture is unavailable, record
that boundary and still run the in-memory test; do not claim MySQL verification.

### Task 2: Extend the HTTP and TypeScript API contracts

**Files:**
- Modify: `src/server/WechatRobot.Api/Knowledge/DocumentEndpoints.cs`
- Modify: `src/web/wechatrobot-admin/src/api/knowledge.ts`
- Test: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentAdministrationEndpointTests.cs`
- Test: `src/web/wechatrobot-admin/src/api/knowledgeDocuments.spec.ts`

**Interfaces:**
- Consumes: Task 1 `ListAsync` signature and `KnowledgeDocumentSummary`.
- Produces:
  - query parameters `sourceKind?: string` and `tagId?: string`
  - TypeScript `KnowledgeDocumentTagSummary`
  - TypeScript summary fields `sourceKind`, `sourceActorDisplayName`, and `tags`

- [x] **Step 1: Add failing endpoint and frontend API tests**

Extend the endpoint test request:

```text
/api/knowledge/documents?sourceKind=PrivateChatDirect&tagId={tagId}&page=1&pageSize=20
```

Assert returned JSON contains the effective version source and tag ID/name.

Extend the frontend API expectation to:

```ts
params: {
  query: '产品',
  status: 'failed',
  sourceKind: 'PrivateChatDirect',
  tagId: 'tag-id',
  page: 2,
  pageSize: 25
}
```

- [x] **Step 2: Run focused tests and confirm failure**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter FullyQualifiedName~KnowledgeDocumentAdministrationEndpointTests
Set-Location src/web/wechatrobot-admin
npm test -- --run src/api/knowledgeDocuments.spec.ts
```

Expected: fail because the endpoint and TypeScript request do not forward the
new filters.

- [x] **Step 3: Implement the endpoint and frontend API types**

Add `string? sourceKind` and `Guid? tagId` to `DocumentEndpoints.ListAsync`,
then pass them to the query service.

Add:

```ts
export interface KnowledgeDocumentTagSummary {
  id: string;
  name: string;
}
```

Extend the request and response types, and forward trimmed values as undefined
when empty.

- [x] **Step 4: Run endpoint and API tests**

Run the two commands from Step 2. Expected: pass.

### Task 3: Redesign the Vue document-management page

**Files:**
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentsView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentsView.spec.ts`
- Verify: `src/web/wechatrobot-admin/src/main.ts`
- Verify/Test: `src/web/wechatrobot-admin/src/styles.spec.ts`

**Interfaces:**
- Consumes:
  - `KnowledgeApi.listDocuments`
  - `knowledgeTagApi.options`
  - Task 2 source/tag summary fields
- Produces:
  - filters `{ query, status, sourceKind, tagId, page, pageSize }`
  - upload dialog controlled by `uploadDialogVisible`
  - source/status Chinese label functions

- [x] **Step 1: Update fixtures and write failing layout behavior tests**

Add summary fixture data:

```ts
sourceKind: 'PrivateChatDirect',
sourceActorDisplayName: '张伟',
tags: [{ id: 'tag-1', name: '加拿大签证' }]
```

Assert:

- “上传文档” button exists but file input is absent initially.
- Clicking the button opens a dialog containing the existing upload controls.
- The table row displays “加拿大签证”, “私聊直接入库”, “张伟”, and a Chinese
  status label.
- An empty tag list displays “未绑定”.
- Source/tag/status filters are included in `listDocuments`.
- Reset clears all four filters and requests page 1.

- [x] **Step 2: Add tag-options failure behavior test**

Inject a `tagApi.options` rejection. Assert the document list still renders and
the tag-filter error/retry control is visible.

- [x] **Step 3: Run the component test and confirm failure**

Run:

```powershell
Set-Location src/web/wechatrobot-admin
npm test -- --run src/views/knowledge/KnowledgeDocumentsView.spec.ts
```

Expected: fail because the dialog, source/tag fields, filters, and reset control
do not yet exist.

- [x] **Step 4: Implement the upload dialog**

Use `ElDialog` with a visible title, `append-to-body`, close behavior, and the
existing upload state. Move the current file input, `.doc` warning, progress,
success, and error elements into the dialog without changing upload API calls.

On successful upload:

```ts
await load();
uploadDialogVisible.value = false;
```

On failure, leave the dialog open. Reset file/progress/result when opening a new
upload.

- [x] **Step 5: Implement filters and table/card rendering**

Use `ElSelect`/`ElOption` for knowledge tags, source, and status. Reuse
`knowledgeTagApi.options()` for tag options. Add “查询” and “重置”.

Render desktop columns:

```text
文档 | 绑定知识库 | 来源 | 状态 | 更新时间 | 操作
```

Merge the safe failure summary into the status cell. Keep all existing
data-testid hooks for details, retry, and physical delete.

Add a narrow-screen card rendering of the same fields and operations. Use CSS
media queries to show the table on desktop and cards below the selected
breakpoint without horizontal overflow.

- [x] **Step 6: Verify Element Plus logic and style dependencies**

`src/web/wechatrobot-admin/src/main.ts` already imports dedicated styles for
`dialog`, `select`, and `option`. Keep those imports. Update
`src/web/wechatrobot-admin/src/styles.spec.ts` only if it does not already assert
these exact style entry points.

- [x] **Step 7: Run the component and style tests**

Run:

```powershell
npm test -- --run src/views/knowledge/KnowledgeDocumentsView.spec.ts src/styles.spec.ts
```

Expected: pass.

### Task 4: Make document detail and version operations source-aware

**Files:**
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentManagementView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentManagementView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/DocumentDetailView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/DocumentDetailView.spec.ts`

**Interfaces:**
- Consumes:
  - version `sourceKind`, `sourceActorDisplayName`, `sourceBatchId`,
    `changeKind`, `supersedesVersionId`, and `tags`
- Produces:
  - `isAutomaticSource` for `ConversationReview` and `PrivateChatDirect`
  - source-aware titles and operation visibility
  - `selectedTagIds` initialized from the exact loaded version

- [x] **Step 1: Add failing management-page source tests**

Create `DocumentUpload`, `ConversationReview`, and `PrivateChatDirect` fixtures.
Assert:

- upload source shows file-version upload;
- automatic sources show Chinese source, actor, change kind, and tags;
- automatic sources do not render `upload-new-version`;
- disable and administrator physical delete remain available.

- [x] **Step 2: Add failing version-page source and tag tests**

Return an automatic-source version:

```ts
{
  sourceKind: 'PrivateChatDirect',
  sourceActorDisplayName: '张伟',
  changeKind: 'Correction',
  tags: [{ id: 'tag-1', name: '加拿大签证' }]
}
```

Assert:

- page title is “入库内容与索引”;
- `KnowledgeTagSelector` receives `modelValue: ['tag-1']`;
- generate/edit/split/merge/delete/approve controls are absent or disabled;
- queueing index without changing the selection sends `['tag-1']`.

Keep an upload-source test proving the full chunk controls still work.

- [x] **Step 3: Run both component tests and confirm failure**

```powershell
Set-Location src/web/wechatrobot-admin
npm test -- --run `
  src/views/knowledge/KnowledgeDocumentManagementView.spec.ts `
  src/views/knowledge/DocumentDetailView.spec.ts
```

- [x] **Step 4: Implement source-aware document management**

Use the effective version (`activeVersionId`, otherwise first/latest version)
to derive the document source. Render source evidence and version tags on every
version card. Only show file upload for `DocumentUpload`, `LegacyUnknown`, or a
document with no version; keep document-level disable/delete unchanged.

- [x] **Step 5: Implement source-aware chunk/index behavior**

Store the exact loaded version object, then set:

```ts
selectedTagIds.value = version.tags.map(tag => tag.id);
```

For `ConversationReview` and `PrivateChatDirect`, render read-only chunk text and
hide mutation controls. Keep the tag selector enabled and allow queue/reindex
with the selected IDs. Keep index retry for every source.

- [x] **Step 6: Run both component tests**

Expected: pass with no unhandled Element Plus warnings.

### Task 5: Cross-boundary regression verification

**Files:**
- Verify only: all files changed by Tasks 1-3.

**Interfaces:**
- Consumes all completed backend/frontend contracts.
- Produces verification evidence; no new behavior.

- [x] **Step 1: Run backend focused integration tests**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~KnowledgeDocumentAdministration"
```

- [x] **Step 2: Run frontend document tests**

```powershell
Set-Location src/web/wechatrobot-admin
npm test -- --run src/api/knowledgeDocuments.spec.ts src/views/knowledge/KnowledgeDocumentsView.spec.ts src/styles.spec.ts
```

- [x] **Step 3: Run frontend typecheck and production build**

```powershell
npm run typecheck
npm run build
```

- [x] **Step 4: Run diff hygiene**

```powershell
Set-Location H:/Codex/WechatRobot
git diff --check -- `
  docs/superpowers/specs/2026-07-30-knowledge-document-list-redesign.md `
  docs/superpowers/plans/2026-07-30-knowledge-document-list-redesign.md `
  src/server/WechatRobot.Application/Knowledge/KnowledgeDocumentAdministrationContracts.cs `
  src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeDocumentAdministrationQuery.cs `
  src/server/WechatRobot.Api/Knowledge/DocumentEndpoints.cs `
  src/web/wechatrobot-admin/src/api/knowledge.ts `
  src/web/wechatrobot-admin/src/api/knowledgeDocuments.spec.ts `
  src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentsView.vue `
  src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentsView.spec.ts `
  src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentManagementView.vue `
  src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentManagementView.spec.ts `
  src/web/wechatrobot-admin/src/views/knowledge/DocumentDetailView.vue `
  src/web/wechatrobot-admin/src/views/knowledge/DocumentDetailView.spec.ts `
  tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentAdministrationQueryTests.cs `
  tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentAdministrationEndpointTests.cs `
  tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentAdministrationMySqlTests.cs
```

- [x] **Step 5: Review scoped diff**

Confirm:

- no unrelated file was modified by this task;
- no secret or source content was added;
- no empty tag was labeled as global;
- no existing operation or authorization check was removed.

### Task 6: Browser and real-flow acceptance

**Files:**
- No source changes unless verification reveals a reproducible defect.

**Interfaces:**
- Consumes the rebuilt local API/frontend and existing local `.local`
  configuration.
- Produces browser screenshots and runtime evidence.

- [ ] **Step 1: Rebuild/restart only the affected local API and frontend**

Use the repository’s existing local startup procedure with
`WECHATROBOT_ENV_FILE` pointing to `.local/.env` and `.local` as the API working
directory. Do not read or print secret values.

- [ ] **Step 2: Verify local runtime readiness**

Verify:

- `http://127.0.0.1:5268/health/live` returns healthy.
- the frontend `http://127.0.0.1:5173` returns HTTP 200.
- the authenticated document list endpoint returns the new fields.

- [ ] **Step 3: Perform visual checks**

Partial evidence completed on 2026-07-30: intercepted-API browser checks passed at
1440px and 390px for the list, upload dialog, automatic-source read-only detail,
preselected tags, and horizontal overflow. The remaining listed viewport and
keyboard checks require the local API/runtime acceptance pass.

Check the document list at 1440, 1024, 768, 414, and 375 CSS pixels:

- upload dialog is fully visible;
- select controls have Element Plus styling;
- no table/card content escapes the page;
- automatic-source detail shows source evidence without file upload;
- automatic-source version content is read-only and existing tags are selected;
- keyboard focus can open/close the dialog and reach every action;
- loading, empty, success, and failure states remain visible.

- [ ] **Step 4: Hand off the real private-ingest acceptance steps**

Ask the user to send one private direct-ingest message, then verify:

1. list filter “私聊直接入库” returns the document;
2. the row shows the actual source actor and bound knowledge tag;
3. a follow-up private question retrieves the new knowledge and receives a
   normal robot reply.
