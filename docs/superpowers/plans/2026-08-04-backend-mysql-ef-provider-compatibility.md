# Backend MySQL EF Provider Compatibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 审计并修复整个后端的 MySQL EF Provider 高风险查询和批量状态变更，消除运行时 Guid 集合参数与 nullable bulk setter 引发的 API 500、Worker 退出和静默降级。

**Architecture:** 先用全库契约测试建立 74 个 bulk mutation 调用的审计边界，再按状态机模块将 nullable bulk setter 改为有并发令牌保护的跟踪实体更新；只有原子抢占所需的非空条件更新可以保留。运行时 Guid 集合统一使用 `GuidBatchQuery`，最后用静态守卫、行为测试和非容器测试套件验证，不修改数据库结构或外部 API。

**Tech Stack:** .NET 10、ASP.NET Core 10、Entity Framework Core 10.0.7、MySql.EntityFrameworkCore 10.0.7、xUnit v3、Microsoft Testing Platform、Vue 3、Vitest

## Global Constraints

- 审计 `src/server` 全部 74 个现有 `ExecuteUpdateAsync`/`ExecuteDeleteAsync` 调用和所有运行时 Guid 集合查询。
- 运行时 Guid 集合进入 EF SQL 时必须使用 `GuidBatchQuery.CreateBatches` 与 `GuidBatchQuery.BuildPredicate`，每批最多 100 个 ID。
- nullable setter、可空捕获变量或已观察到 Provider 失败的 bulk mutation 必须改为跟踪实体更新。
- 只有任务抢占、租约获取等必须保持单条 CAS 的非空 bulk mutation 可以保留，并必须出现在精确白名单中。
- 保留 API 202/409、事务、审计、幂等键、`Version/StateVersion`、Worker 重试和 dead-letter 语义。
- 不使用容器，不向正式 MySQL 写测试数据，不创建或删除正式服务器上的测试库。
- 不新增迁移，不改变 HTTP、Qdrant、WorkTool 或回调合同。
- 不把模拟 Provider 验证描述为真实 MySQL 验证。

---

### Task 1: Establish the backend Provider compatibility guard and audit ledger

**Files:**
- Create: `tests/server/WechatRobot.UnitTests/Persistence/BackendProviderCompatibilityContractTests.cs`
- Create: `docs/runbooks/backend-mysql-ef-provider-audit.md`
- Modify: `docs/runbooks/mysql-ef-provider-query-compatibility.md`

**Interfaces:**
- Consumes: repository root discovery pattern from `KnowledgeVectorMigrationMySqlQueryContractTests`.
- Produces: `ApprovedBulkMutation` records keyed by normalized path, enclosing method, and ordinal; a failing contract test for unapproved or nullable bulk mutations; an audit ledger used by all later tasks.

- [ ] **Step 1: Write the failing full-backend inventory test**

Add a test that scans `src/server/**/*.cs`, extracts every `ExecuteUpdateAsync` and `ExecuteDeleteAsync`, associates it with the nearest enclosing method, and compares the result with an initially empty approval list:

```csharp
[Fact]
public void Every_bulk_mutation_is_explicitly_audited()
{
    var actual = ScanBulkMutations(RepositoryRoot());

    Assert.Equal(74, actual.Count);
    Assert.Empty(actual.Select(item => item.Key).Except(ApprovedBulkMutations));
}

private sealed record BulkMutationKey(
    string Path,
    string Method,
    int Ordinal,
    string Operation);

private sealed record ScannedBulkMutation(
    BulkMutationKey Key,
    string Invocation);

private static readonly BulkMutationKey[] ApprovedBulkMutations = [];
```

The scanner must normalize separators to `/`, ignore `bin`, `obj`, migrations and tests, and count calls in the 10 files listed in the approved spec. It must extract balanced invocation blocks rather than relying on a single-line regex.

- [ ] **Step 2: Run the contract test and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class WechatRobot.UnitTests.Persistence.BackendProviderCompatibilityContractTests
```

Expected: FAIL because 74 actual calls are not in the empty approval list.

- [ ] **Step 3: Add nullable-setter and Guid-query guards**

Add two tests:

```csharp
[Fact]
public void Approved_bulk_mutations_do_not_assign_null_or_nullable_captures()
{
    var risky = ScanBulkMutations(RepositoryRoot())
        .Where(item => item.Invocation.Contains(")null", StringComparison.Ordinal)
            || NullableCaptureNames.Any(name =>
                item.Invocation.Contains($", {name})", StringComparison.Ordinal)))
        .ToArray();

    Assert.Empty(risky);
}

[Fact]
public void Ef_queries_do_not_use_unapproved_runtime_guid_contains()
{
    Assert.Empty(ScanRuntimeGuidContains(RepositoryRoot()));
}
```

`NullableCaptureNames` begins with the currently observed captured values such as `nextAttempt`, `groupProfileId`, `result`, and `completedAtUtc`. `ScanRuntimeGuidContains` reports only queryable expressions; it excludes lines whose receiver is an already loaded array, dictionary, HashSet, Qdrant request or application-only collection.

```csharp
private static readonly string[] NullableCaptureNames =
    ["nextAttempt", "groupProfileId", "result", "completedAtUtc"];
```

- [ ] **Step 4: Verify both guards fail on current production code**

Run the same filtered test command. Expected: FAIL listing `KnowledgeDocumentStore.RequestPhysicalDeleteCoreAsync` and other nullable bulk mutation sites, plus any remaining EF runtime Guid collection queries.

- [ ] **Step 5: Create the audit ledger**

Document all 74 calls with columns `Path`, `Method`, `Ordinal`, `Classification`, `Reason`, and `Regression Test`. Initial classifications are `ReplaceTracked`, `KeepAtomic`, or `RemoveGuidContains`. No row may use `Unreviewed`.

- [ ] **Step 6: Commit the red guard and ledger**

```powershell
git add -- tests/server/WechatRobot.UnitTests/Persistence/BackendProviderCompatibilityContractTests.cs docs/runbooks/backend-mysql-ef-provider-audit.md docs/runbooks/mysql-ef-provider-query-compatibility.md
git commit -m "test: inventory backend ef provider risks"
```

### Task 2: Repair knowledge document upload and physical-delete transitions

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/KnowledgeDocumentStore.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentAdministrationMutationTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentCleanupWorkerTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentConcurrencyTests.cs`
- Modify: `tests/server/WechatRobot.UnitTests/Persistence/BackendProviderCompatibilityContractTests.cs`
- Modify: `docs/runbooks/backend-mysql-ef-provider-audit.md`

**Interfaces:**
- Consumes: `KnowledgeDocumentEntity.StateVersion` and entity `Version` concurrency tokens; existing `BeginTransactionIfRelationalAsync`; existing audit helpers.
- Produces: Provider-stable `MarkUploadedAsync`, `MarkFailedAsync`, physical-delete request and cleanup requeue transitions with unchanged application contracts.

- [ ] **Step 1: Add a failing Provider-boundary physical-delete test**

Configure an InMemory context whose `IDatabaseProvider.Name` is not the InMemory provider and whose bulk-operation boundary throws `InvalidOperationException("nullable_bulk_update_not_supported")`. Execute the real store method:

```csharp
[Fact]
public async Task Physical_delete_request_does_not_require_nullable_bulk_updates()
{
    await using var database = ProviderBoundaryDatabase();
    var seeded = await SeedUploadedDocumentAsync(database);

    var accepted = await new KnowledgeDocumentStore(database)
        .RequestPhysicalDeleteAsync(
            seeded.DocumentId,
            seeded.StateVersion,
            "admin",
            TestContext.Current.CancellationToken);

    Assert.True(accepted);
    Assert.True((await database.KnowledgeDocuments.FindAsync(seeded.DocumentId))!.IsDeleteRequested);
}
```

- [ ] **Step 2: Run the focused integration class and verify RED**

Expected: current relational branch attempts the nullable `ExecuteUpdateAsync` and fails with `nullable_bulk_update_not_supported`.

- [ ] **Step 3: Use tracked transitions for all knowledge-document state changes**

Remove the provider split and make `MarkUploadedAsync`, `MarkFailedAsync`, `RequestPhysicalDeleteCoreAsync`, and `TryRequeuePhysicalCleanupAsync` call tracked implementations. Wrap multi-entity mutations in `BeginTransactionIfRelationalAsync`, preserve original values for concurrency tokens, and map `DbUpdateConcurrencyException` through existing conflict helpers.

The physical-delete core must perform this sequence:

```csharp
await using var transaction = await BeginTransactionIfRelationalAsync(token);
var document = await database.KnowledgeDocuments
    .SingleOrDefaultAsync(item => item.Id == documentId, token);
if (document is null) return false;
if (document.StateVersion != expectedStateVersion) throw Concurrency(document);
// mutate document, bounded versions/jobs, enqueue cleanup, add audit
await database.SaveChangesAsync(token);
if (transaction is not null) await transaction.CommitAsync(token);
```

- [ ] **Step 4: Preserve the public behavior with regression assertions**

Verify stale version returns 409, success returns 202, `StateVersion` increments once, versions are disabled, cleanup is queued once, audit stays sanitized, repeated delete follows existing conflict/requeue rules, and upload completion cannot reactivate a deleted document.

- [ ] **Step 5: Run focused non-container tests and update the ledger**

Run the mutation, cleanup worker and source-contract tests. Move all 13 `KnowledgeDocumentStore` rows to `ReplaceTracked` and remove them from the approved bulk list.

- [ ] **Step 6: Commit**

```powershell
git add -- src/server/WechatRobot.Infrastructure/Persistence/KnowledgeDocumentStore.cs tests/server/WechatRobot.IntegrationTests/Knowledge tests/server/WechatRobot.UnitTests/Persistence/BackendProviderCompatibilityContractTests.cs docs/runbooks/backend-mysql-ef-provider-audit.md
git commit -m "fix: stabilize knowledge document mysql transitions"
```

### Task 3: Repair knowledge indexing, activation and lifecycle transitions

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeCandidatePublishProcessor.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Groups/EfGroupLifecycleStore.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/EfHandoffStore.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Knowledge/QdrantKnowledgeTests.cs`
- Modify: `tests/server/WechatRobot.UnitTests/Persistence/BackendProviderCompatibilityContractTests.cs`
- Modify: `docs/runbooks/backend-mysql-ef-provider-audit.md`

**Interfaces:**
- Consumes: Qdrant job `Version`, lease owner and activation generation; group and handoff concurrency tokens.
- Produces: tracked nullable cleanup/disable transitions while preserving atomic non-null lease acquisition.

- [ ] **Step 1: Add failing disable and completion boundary tests**

Add tests that execute document disable, index cleanup completion and handoff/group transitions through a provider boundary rejecting nullable bulk setters. Assert current code fails before persisted state changes.

- [ ] **Step 2: Keep only non-null atomic claims**

Retain `LeaseNextAsync` and lease renewal expressions whose setters are non-null and whose predicates include status, owner and version. Add their path/method/ordinal to `ApprovedBulkMutations`.

- [ ] **Step 3: Convert nullable activation, disable and completion setters**

Load bounded target rows inside existing transactions, mutate tracked entities, increment the same version fields, and call `SaveChangesAsync(token)`. Keep Qdrant external calls outside database transactions exactly where they are today.

- [ ] **Step 4: Verify concurrency and Qdrant ordering**

Assert one active version, old versions disabled, no collection-wide delete, cleanup completes only for the current lease owner, and group/handoff stale versions still conflict.

- [ ] **Step 5: Run focused tests, update guard approvals and commit**

```powershell
git add -- src/server/WechatRobot.Infrastructure/Knowledge src/server/WechatRobot.Infrastructure/Groups/EfGroupLifecycleStore.cs src/server/WechatRobot.Infrastructure/Persistence/EfHandoffStore.cs tests/server docs/runbooks/backend-mysql-ef-provider-audit.md
git commit -m "fix: stabilize knowledge lifecycle mysql transitions"
```

### Task 4: Repair durable job and send-command state machines

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Messaging/DurableJobWorkerResilienceTests.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Messaging/DurableJobProviderBoundaryTests.cs`
- Modify: `tests/server/WechatRobot.UnitTests/Persistence/BackendProviderCompatibilityContractTests.cs`
- Modify: `docs/runbooks/backend-mysql-ef-provider-audit.md`

**Interfaces:**
- Consumes: durable job `Version`, lease owner, status, robot guard and message state contracts.
- Produces: atomic non-null claim/renew operations plus tracked completion, defer, failure, release and send-result transitions.

- [ ] **Step 1: Add failing state-machine boundary tests**

Cover `CompleteJobAsync`, `DeferJobAsync`, `FailJobAsync`, send accepted/rejected/unknown, robot guard release and related-message completion. The provider boundary rejects nullable setters; each test asserts the public repository result and persisted status.

- [ ] **Step 2: Verify RED**

Expected: at least completion, defer, failure, send accepted and guard release fail with `nullable_bulk_update_not_supported`.

- [ ] **Step 3: Classify the 23 calls**

Keep only lease/claim/renew updates that set non-null owner, expiry, status and version under a single conditional predicate. Convert terminal transitions that clear lease/result/completion fields to tracked entities guarded by the current owner and `Version`.

Use the existing return contracts: a concurrency miss returns `false` or zero where the repository currently uses row count; it must not become an unhandled `DbUpdateConcurrencyException`.

- [ ] **Step 4: Replace the memory cleanup Guid query**

Change the EF query using `memoryIds.Contains(entry.Id)` to bounded `GuidBatchQuery` queries, merge rows, and preserve the existing status filter and order.

- [ ] **Step 5: Verify competing workers and retries**

Run tests proving only one worker leases a job, renewal requires the same owner, stale completion fails, dead-letter attempts are unchanged, send idempotency is preserved, and memory cleanup does not use runtime Guid collection parameters.

- [ ] **Step 6: Update the ledger and commit**

```powershell
git add -- src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs tests/server/WechatRobot.IntegrationTests/Messaging tests/server/WechatRobot.UnitTests/Persistence/BackendProviderCompatibilityContractTests.cs docs/runbooks/backend-mysql-ef-provider-audit.md
git commit -m "fix: stabilize durable job mysql transitions"
```

### Task 5: Repair conversation persistence and lease transitions

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Conversations/ConversationAuditQuery.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Conversations/ConversationProviderBoundaryTests.cs`
- Modify: `tests/server/WechatRobot.UnitTests/Persistence/BackendProviderCompatibilityContractTests.cs`
- Modify: `docs/runbooks/backend-mysql-ef-provider-audit.md`

**Interfaces:**
- Consumes: message processing state, session sequence, lease owner/expiry and outbox idempotency contracts.
- Produces: Provider-stable terminal, release, answer persistence and session-clear operations; Guid-batched audit message loading.

- [ ] **Step 1: Add failing provider-boundary tests**

Cover no-reply terminal persistence, lease release, answer/outbox persistence, error recovery and group-session clearing. Assert message terminal state, session relation, retrieval audit and outbox uniqueness.

- [ ] **Step 2: Keep atomic message claims and renewals**

Retain only non-null conditional updates needed to claim a message or extend a lease. Add exact approvals to the guard.

- [ ] **Step 3: Convert nullable terminal and release paths**

Load the single message/session by ID and current owner, set nullable fields through tracked entities, preserve `ProcessingState`, `TerminalDecision`, sequence and outbox transaction semantics, and handle concurrency as the current method contract requires.

- [ ] **Step 4: Batch conversation-audit Guid queries**

Use `GuidBatchQuery.CreateBatches(messageIds)` and `BuildPredicate<ConversationMessageEntity>` for database loading. Keep later `messageIds.Contains` only where it operates on already loaded in-memory rows.

- [ ] **Step 5: Run focused tests, update ledger and commit**

```powershell
git add -- src/server/WechatRobot.Infrastructure/Conversations tests/server/WechatRobot.IntegrationTests/Conversations tests/server/WechatRobot.UnitTests/Persistence/BackendProviderCompatibilityContractTests.cs docs/runbooks/backend-mysql-ef-provider-audit.md
git commit -m "fix: stabilize conversation mysql transitions"
```

### Task 6: Repair WorkTool command and reconciliation transitions

**Files:**
- Modify: `src/server/WechatRobot.Api/WorkTool/WorkToolGroupOperationEndpoints.cs`
- Modify: `src/server/WechatRobot.Worker/Jobs/WorkToolGroupOperationWorker.cs`
- Modify: `src/server/WechatRobot.Worker/Jobs/WorkToolGroupReconciliationWorker.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/WorkTool/WorkToolGroupReconciliationWorkerTests.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/WorkTool/WorkToolProviderBoundaryTests.cs`
- Modify: `tests/server/WechatRobot.UnitTests/Persistence/BackendProviderCompatibilityContractTests.cs`
- Modify: `docs/runbooks/backend-mysql-ef-provider-audit.md`

**Interfaces:**
- Consumes: WorkTool audit `Version`, command status, dispatch owner, result timeout and reconciliation retry contracts.
- Produces: atomic dispatch/reconciliation claims plus tracked nullable completion and retry transitions.

- [ ] **Step 1: Add failing provider-boundary tests**

Cover admin retry, dispatch timeout, accepted command, rejected/unknown completion, reconciliation completion and retry scheduling. Assert business status independently from HTTP success.

- [ ] **Step 2: Retain atomic non-null claims**

Keep queued-to-dispatching and pending-to-retrying CAS updates when predicates include current status and version and setters contain no nullable values.

- [ ] **Step 3: Convert result and reconciliation completion**

Use tracked rows for transitions clearing lease, result, completion or next-attempt fields. Preserve WorkTool business-code handling, delivery-unknown semantics and version increments.

- [ ] **Step 4: Run WorkTool tests, update ledger and commit**

```powershell
git add -- src/server/WechatRobot.Api/WorkTool src/server/WechatRobot.Worker/Jobs/WorkToolGroupOperationWorker.cs src/server/WechatRobot.Worker/Jobs/WorkToolGroupReconciliationWorker.cs tests/server/WechatRobot.IntegrationTests/WorkTool tests/server/WechatRobot.UnitTests/Persistence/BackendProviderCompatibilityContractTests.cs docs/runbooks/backend-mysql-ef-provider-audit.md
git commit -m "fix: stabilize worktool mysql transitions"
```

### Task 7: Close all remaining Guid-query and bulk-mutation audit findings

**Files:**
- Modify: `tests/server/WechatRobot.UnitTests/Persistence/BackendProviderCompatibilityContractTests.cs`
- Modify: `docs/runbooks/backend-mysql-ef-provider-audit.md`

**Interfaces:**
- Consumes: the exact residual report produced after Tasks 2–6.
- Produces: zero unapproved bulk mutations, zero nullable approved setters and zero EF runtime Guid collection `Contains` findings.

- [ ] **Step 1: Run the full guard and capture the residual list**

Run the filtered unit test. Every reported item must already identify an exact path, method and ordinal; do not broaden the allowlist to silence failures.

- [ ] **Step 2: Route every residual back to its owning module task**

Do not modify a new production file in this closure task. A residual in `KnowledgeDocumentStore` returns to Task 2; knowledge/lifecycle returns to Task 3; durable jobs to Task 4; conversations to Task 5; WorkTool to Task 6. Re-run that task's red-green cycle and commit the correction there. Only exact non-null CAS approvals and ledger corrections belong in Task 7.

- [ ] **Step 3: Verify GREEN and inventory closure**

The contract test must assert:

```csharp
Assert.Empty(UnapprovedBulkMutations());
Assert.Empty(ApprovedNullableBulkMutations());
Assert.Empty(UnapprovedEfRuntimeGuidContains());
Assert.Equal(ScanBulkMutations(RepositoryRoot()).Count, AuditLedgerEntries().Count);
```

- [ ] **Step 4: Run backend unit and contract suites**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore
```

- [ ] **Step 5: Commit audit closure**

```powershell
git add -- src/server tests/server/WechatRobot.UnitTests/Persistence/BackendProviderCompatibilityContractTests.cs docs/runbooks/backend-mysql-ef-provider-audit.md
git commit -m "test: close backend mysql provider audit"
```

### Task 8: Verify, package and push the complete repair

**Files:**
- Modify only if verification exposes a regression in current-task files.
- Create: release files under `artifacts/` without staging them unless repository policy explicitly tracks artifacts.

**Interfaces:**
- Consumes: all module commits and the closed audit ledger.
- Produces: verified API/Worker publish outputs, one compressed release archive and pushed `origin/master` commits.

- [ ] **Step 1: Run non-container backend verification**

Run UnitTests, ContractTests, and IntegrationTests excluding classes that instantiate `MySqlFixture`. If the test platform cannot express that exclusion reliably, run the known non-container integration classes explicitly and record the excluded MySQL fixture classes in the handoff.

- [ ] **Step 2: Run frontend contract verification**

```powershell
npm run typecheck
npm test -- --run
npm run build
```

Run from `src/web/wechatrobot-admin`. Expected: exit code 0 for all commands.

- [ ] **Step 3: Run final source and Git checks**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class WechatRobot.UnitTests.Persistence.BackendProviderCompatibilityContractTests
git diff --check
git status --short
```

Confirm there are no secrets, `.local` files, connection strings, logs or test data in the diff.

- [ ] **Step 4: Publish API and Worker**

Use Release configuration and the repository SDK to publish both projects into a new timestamped directory under `artifacts/`. Copy only runtime outputs and required non-secret appsettings files. Do not copy `.local` or environment files.

```powershell
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$shortCommit = git rev-parse --short HEAD
$releaseRoot = Join-Path (Resolve-Path artifacts) "WechatRobot-$shortCommit-$stamp"
dotnet publish src/server/WechatRobot.Api/WechatRobot.Api.csproj -c Release --no-restore -o (Join-Path $releaseRoot 'api')
dotnet publish src/server/WechatRobot.Worker/WechatRobot.Worker.csproj -c Release --no-restore -o (Join-Path $releaseRoot 'worker')
```

- [ ] **Step 5: Create and verify the release archive**

Compress the API and Worker publish directories into one archive named with the final short commit and timestamp. List the archive entries and compute SHA-256; verify no `.env`, `.local`, log, test or source file is present.

```powershell
$archive = "$releaseRoot.zip"
Compress-Archive -LiteralPath (Join-Path $releaseRoot 'api'), (Join-Path $releaseRoot 'worker') -DestinationPath $archive
tar -tf $archive
Get-FileHash -Algorithm SHA256 -LiteralPath $archive
```

- [ ] **Step 6: Confirm all implementation changes are already committed**

```powershell
git status --short
git log -8 --oneline
```

Expected: status has no current-task source or test changes; the module commits from Tasks 1–7 are visible.

- [ ] **Step 7: Push without rewriting history**

```powershell
git fetch origin master
git rev-list --left-right --count origin/master...master
git push origin master
```

If the remote is ahead, stop and rebase normally; never force-push without explicit authorization.

- [ ] **Step 8: Production acceptance handoff**

Report that local Provider simulation passed but real MySQL remains pending until the new API and Worker are deployed. The production checklist must include physical delete 202/409, API logs, Worker heartbeat, message processing, index jobs and WorkTool transitions with no `CreateDbCommand` or `AddDbParameter` errors.
