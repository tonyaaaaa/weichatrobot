# Knowledge Physical Cleanup Object Key Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复私聊知识对象键命名与物理清理真实对象判定，使历史伪对象键不再导致清理任务进入 dead letter，同时保持 OSS 前缀安全校验。

**Architecture:** 新私聊版本统一生成 `wechatrobot/private-chat/...` 键；清理 Worker 以持久化 `PublicUrl` 判定版本是否真正上传对象存储，只有真实公共对象才调用 `IObjectStorage.DeleteAsync`。历史伪对象键保持原值，继续执行向量和 MySQL 清理，不新增迁移。

**Tech Stack:** .NET 10、ASP.NET Core 10、Entity Framework Core 10、MySql.EntityFrameworkCore 10.0.7、xUnit v3、Microsoft Testing Platform、Aliyun OSS、Qdrant

## Global Constraints

- 不放宽 `AliyunOssStorage` 和 `LoopbackObjectStorage` 的 `wechatrobot/` 前缀校验。
- 不批量改写正式库历史 `ObjectKey`。
- 不新增数据库迁移，不改变物理删除 API 202/409 合同。
- 不使用容器，不向正式 MySQL 写入测试数据。
- 必须先验证测试 RED，再修改生产代码。
- 正式失败任务只能在新 Worker 部署并确认心跳后重投。

---

### Task 1: Correct private-chat object key generation

**Files:**
- Modify: `tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateKnowledgeIngestPipelineTests.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Agents/PrivateKnowledgeIngestProcessor.cs:396-405`

**Interfaces:**
- Consumes: `PrivateKnowledgeIngestProcessor.CreateStagedVersionAsync(...)` and `KnowledgeDocumentVersionEntity.ObjectKey`.
- Produces: every new `PrivateChatDirect` version has an object key beginning with `wechatrobot/private-chat/`.

- [ ] **Step 1: Add the failing key-prefix assertion**

In `Processor_stages_validated_items_with_global_tag_and_batch_index_job`, immediately after the existing `SourceKind` assertion, add:

```csharp
Assert.StartsWith("wechatrobot/private-chat/", version.ObjectKey, StringComparison.Ordinal);
Assert.Contains($"/{batchId:N}/", version.ObjectKey, StringComparison.Ordinal);
```

- [ ] **Step 2: Run the single test and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-method WechatRobot.IntegrationTests.PrivateChat.PrivateKnowledgeIngestPipelineTests.Processor_stages_validated_items_with_global_tag_and_batch_index_job --no-progress
```

Expected: FAIL because the actual key begins with `private-chat/`.

- [ ] **Step 3: Apply the minimal production change**

Change the version initializer to:

```csharp
ObjectKey = $"wechatrobot/private-chat/{batch.Id:N}/{sequence}",
```

Do not add an OSS upload to the private-chat ingest path.

- [ ] **Step 4: Run the single test and verify GREEN**

Run the Step 2 command. Expected: PASS.

- [ ] **Step 5: Commit the isolated change**

```powershell
git add -- src/server/WechatRobot.Infrastructure/Agents/PrivateKnowledgeIngestProcessor.cs tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateKnowledgeIngestPipelineTests.cs
git diff --cached --check
git commit -m "fix: namespace private knowledge object keys"
```

### Task 2: Skip OSS deletion for historical pseudo objects

**Files:**
- Modify: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentCleanupWorkerTests.cs`
- Modify: `src/server/WechatRobot.Worker/Jobs/KnowledgeDocumentCleanupWorker.cs:42-46`

**Interfaces:**
- Consumes: `KnowledgeDocumentVersionEntity.PublicUrl`, written only after successful object upload.
- Produces: `KnowledgeDocumentCleanupWorker.ProcessOnceAsync` calls `IObjectStorage.DeleteAsync` only for versions whose `PublicUrl` is non-empty.

- [ ] **Step 1: Add a failing historical pseudo-object regression test**

Add `Physical_delete_skips_legacy_pseudo_object_without_public_url` using the existing `FakeJobs`, `FakeStorage`, `FakeVectors`, InMemory `WechatRobotDbContext`, `KnowledgeIndexOptions`, `ModelConfigurationService`, and `QdrantKnowledgeService` setup. Seed:

```csharp
new KnowledgeDocumentEntity
{
    Id = documentId,
    Status = "disabled",
    IsDeleteRequested = true
},
new KnowledgeDocumentVersionEntity
{
    Id = versionId,
    KnowledgeDocumentId = documentId,
    Version = 1,
    OriginalFileName = "private-chat.txt",
    SafeFileName = "private-chat.txt",
    ContentType = "text/plain",
    Sha256 = "c".PadLeft(64, '0'),
    ObjectKey = "private-chat/legacy/1",
    PublicUrl = null,
    Status = "disabled",
    SourceKind = "PrivateChatDirect"
}
```

After one worker iteration assert:

```csharp
Assert.Empty(storage.Deleted);
Assert.True(jobs.Completed);
Assert.False(jobs.Failed, jobs.FailureReason);
Assert.False(await database.KnowledgeDocuments.AnyAsync(
    item => item.Id == documentId,
    TestContext.Current.CancellationToken));
```

- [ ] **Step 2: Run the new test and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-method WechatRobot.IntegrationTests.Knowledge.KnowledgeDocumentCleanupWorkerTests.Physical_delete_skips_legacy_pseudo_object_without_public_url --no-progress
```

Expected: FAIL at `Assert.Empty(storage.Deleted)` because current Worker treats every non-empty key as an OSS object.

- [ ] **Step 3: Preserve real-object coverage in existing tests**

In `Physical_delete_job_removes_every_oss_object_and_vector_generation_then_completes` set:

```csharp
PublicUrl = "https://public.example.test/wechatrobot/knowledge/a.txt",
```

In `Failed_external_cleanup_keeps_database_records_and_fails_job` set:

```csharp
PublicUrl = "https://public.example.test/wechatrobot/knowledge/retained.txt",
```

These fixtures represent objects that were actually uploaded and therefore must still call storage deletion.

- [ ] **Step 4: Apply the minimal Worker query change**

Replace the cleanup object-key query with:

```csharp
var objectKeys = await database.KnowledgeDocumentVersions.AsNoTracking()
    .Where(version =>
        version.KnowledgeDocumentId == documentId &&
        version.PublicUrl != null &&
        version.PublicUrl != "" &&
        version.ObjectKey != "")
    .Select(version => version.ObjectKey)
    .Distinct()
    .ToArrayAsync(token);
```

Do not catch or ignore `DeleteAsync` exceptions for rows with `PublicUrl`; invalid real-object keys must continue to fail safely.

- [ ] **Step 5: Run cleanup tests and verify GREEN**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class WechatRobot.IntegrationTests.Knowledge.KnowledgeDocumentCleanupWorkerTests --no-progress
```

Expected: all tests in the class PASS, including real object deletion, external deletion failure retention, Provider compatibility and historical pseudo-object cleanup.

- [ ] **Step 6: Commit the isolated Worker fix**

```powershell
git add -- src/server/WechatRobot.Worker/Jobs/KnowledgeDocumentCleanupWorker.cs tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentCleanupWorkerTests.cs
git diff --cached --check
git commit -m "fix: skip pseudo objects during knowledge cleanup"
```

### Task 3: Verify backend compatibility and publish artifacts

**Files:**
- Modify only if verification exposes a regression in Task 1 or Task 2 files.
- Create: ignored Release outputs under `artifacts/WechatRobot-<commit>-<timestamp>/` and a sibling ZIP.

**Interfaces:**
- Consumes: committed Task 1 and Task 2 behavior.
- Produces: verified API and Worker Release output suitable for production deployment.

- [ ] **Step 1: Run focused regression tests together**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class WechatRobot.IntegrationTests.Knowledge.KnowledgeDocumentCleanupWorkerTests --filter-method WechatRobot.IntegrationTests.PrivateChat.PrivateKnowledgeIngestPipelineTests.Processor_stages_validated_items_with_global_tag_and_batch_index_job --no-progress
```

Expected: all selected tests PASS.

- [ ] **Step 2: Run complete non-container backend suites**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --no-progress
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore -- --no-progress
```

Expected: zero failures. Do not run MySQL Testcontainers.

- [ ] **Step 3: Run source hygiene checks**

```powershell
git diff --check
git status --short
rg -n 'ObjectKey = \$"private-chat/' src/server -g '*.cs'
```

Expected: clean diff, only intended state, and no old private-chat object-key generator.

- [ ] **Step 4: Publish API and Worker**

```powershell
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$shortCommit = (git rev-parse --short HEAD).Trim()
$releaseRoot = Join-Path (Resolve-Path artifacts) "WechatRobot-$shortCommit-$stamp"
dotnet publish src/server/WechatRobot.Api/WechatRobot.Api.csproj -c Release --no-restore -o (Join-Path $releaseRoot 'api')
dotnet publish src/server/WechatRobot.Worker/WechatRobot.Worker.csproj -c Release --no-restore -o (Join-Path $releaseRoot 'worker')
```

Expected: both publish commands exit 0.

- [ ] **Step 5: Compress and validate the archive**

```powershell
$archive = "$releaseRoot.zip"
Compress-Archive -LiteralPath (Join-Path $releaseRoot 'api'), (Join-Path $releaseRoot 'worker') -DestinationPath $archive
$entries = @(tar -tf $archive)
$forbidden = @($entries | Where-Object {
    $_ -match '(^|/)(\.env|\.local)(/|$)' -or
    $_ -match '\.(log|cs|csproj|sln|slnx)$' -or
    $_ -match '(^|/)(tests?|TestResults)(/|$)'
})
if ($forbidden.Count -ne 0) { throw "Release archive contains forbidden entries." }
Get-FileHash -Algorithm SHA256 -LiteralPath $archive
```

Expected: no forbidden entries and one SHA-256 value.

### Task 4: Push and provide production recovery handoff

**Files:**
- No source changes expected.

**Interfaces:**
- Consumes: verified commits and release ZIP.
- Produces: pushed `origin/master` plus a deployment-order checklist; it does not mutate production before deployment.

- [ ] **Step 1: Verify remote history and push normally**

```powershell
git fetch origin master
git rev-list --left-right --count origin/master...master
git push origin master
git rev-parse HEAD
git rev-parse origin/master
```

If the remote is ahead, rebase normally and re-run verification; never force-push.

- [ ] **Step 2: Report the deployment order**

The final handoff must state:

1. deploy both API and Worker from the new archive;
2. verify API readiness and fresh Worker heartbeat;
3. click “重新提交物理清理” for the two failed `PrivateChatDirect` documents;
4. verify each cleanup job completes and the documents disappear;
5. verify logs contain no object-key-prefix, `CreateDbCommand`, or `AddDbParameter` errors.

Do not requeue the production jobs before the new Worker is deployed. No database migration is required.
