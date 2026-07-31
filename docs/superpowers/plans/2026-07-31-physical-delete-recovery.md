# Physical Delete Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ensure a physical-delete request ultimately removes the knowledge document from MySQL, including tombstones left by an older Worker, and expose the pending-delete state accurately in the admin UI.

**Architecture:** Preserve the existing asynchronous cleanup pipeline and its invariant that a completed cleanup leaves no document row. The cleanup Worker will recover a bounded set of legacy `completed` cleanup jobs whose delete-requested documents still exist, while the administration contract exposes `IsDeleteRequested` so the UI can distinguish ordinary disablement from pending physical cleanup.

**Tech Stack:** ASP.NET Core 10, EF Core with MySQL/InMemory providers, xUnit v3, Vue 3, TypeScript, Vitest.

## Global Constraints

- Do not add or change runtime configuration.
- Do not physically remove MySQL rows before OSS and Qdrant cleanup succeeds and is verified.
- Do not automatically retry dead-letter jobs indefinitely.
- Preserve knowledge candidates and reviews while clearing their version reference before document deletion.
- Keep recovery bounded and idempotent.

---

### Task 1: Recover legacy completed cleanup jobs

**Files:**
- Modify: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentCleanupWorkerTests.cs`
- Modify: `src/server/WechatRobot.Worker/Jobs/KnowledgeDocumentCleanupWorker.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/KnowledgeDocumentCleanupJobIdentity.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/KnowledgeDocumentStore.cs`

**Interfaces:**
- Produces: `KnowledgeDocumentCleanupJobIdentity.Create(Guid documentId)`.
- Produces: bounded Worker reconciliation that changes a matching legacy job from `completed` to `pending` before leasing it.

- [x] **Step 1: Write the failing regression test**

Seed a delete-requested document and its deterministic `CleanupKnowledgeDocument` job in `completed` state, invoke `ProcessOnceAsync`, and assert that the real durable repository leases the recovered job, deletes the document, and returns the job to `completed`.

- [x] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "WechatRobot.IntegrationTests.Knowledge.KnowledgeDocumentCleanupWorkerTests"
```

Expected: the legacy job is not leased and the document remains.

- [x] **Step 3: Implement bounded recovery**

Extract the deterministic job ID calculation. When no cleanup job is immediately leaseable, inspect at most 100 oldest delete-requested documents, reset matching `completed` cleanup jobs to `pending`, clear completion/lease fields, and lease again. Do not revive `deadLetter`, `retrying`, `leased`, or `pending` jobs.

- [x] **Step 4: Run focused tests and verify GREEN**

Run the focused integration test command from Step 2 and expect all cleanup Worker tests to pass.

### Task 2: Expose pending physical deletion accurately

**Files:**
- Modify: `src/server/WechatRobot.Application/Knowledge/KnowledgeDocumentAdministrationContracts.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeDocumentAdministrationQuery.cs`
- Modify: `src/web/wechatrobot-admin/src/api/knowledge.ts`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentsView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentManagementView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/DocumentDetailView.vue`
- Modify: relevant Vue and backend administration query tests.

**Interfaces:**
- Produces: `KnowledgeDocumentSummary.IsDeleteRequested` / `isDeleteRequested`.
- Consumes: the existing physical-delete request endpoint without changing its HTTP contract.

- [x] **Step 1: Write failing contract and component tests**

Assert that summaries serialize `isDeleteRequested: true` and that pending-delete documents display `等待物理清理` without presenting the initial `提交物理删除` action.

- [x] **Step 2: Run focused backend and frontend tests and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*KnowledgeDocumentAdministration*"
npm test -- --run src/views/knowledge/KnowledgeDocumentsView.spec.ts
```

Expected: the property and pending-delete UI do not exist.

- [x] **Step 3: Implement the contract and UI state**

Map the entity flag into every summary, update TypeScript fixtures, render a pending-cleanup label, and suppress the initial delete action once a request exists.

- [x] **Step 4: Run focused tests and verify GREEN**

Re-run Step 2 and expect all focused tests to pass.

### Task 3: Verify, package, and commit

**Files:**
- Modify only files named in Tasks 1 and 2 plus this plan.

**Interfaces:**
- Produces: a verified Windows/IIS publish archive containing API, Worker, and admin UI.

- [x] **Step 1: Run relevant complete verification**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj
npm run typecheck
npm test -- --run
npm run build
git diff --check
```

- [x] **Step 2: Build the existing Windows/IIS release package**

Use the repository's established packaging command and verify the archive contains the freshly built `WechatRobot.Worker.dll`.

- [x] **Step 3: Review and commit**

Review the scoped diff and commit the implementation, tests, and plan with:

```powershell
git commit -m "fix: recover stranded physical deletions"
```
