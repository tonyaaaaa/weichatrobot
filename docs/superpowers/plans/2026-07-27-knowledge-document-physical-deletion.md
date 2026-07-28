# Knowledge Document Physical Deletion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a completed physical-delete job remove the document and its content hash from MySQL so identical content can be uploaded again, while retaining human-answer review history.

**Architecture:** Keep the existing asynchronous API and durable Worker boundary. The Worker continues to delete and verify OSS/Qdrant state first, then performs one EF Core `SaveChangesAsync` transaction that clears nullable candidate references and removes the document/version rows; the durable job is completed only after that database transaction succeeds.

**Tech Stack:** ASP.NET Core 10, EF Core 10, MySQL 8.4, xUnit v3/Microsoft Testing Platform, Vue 3/Vite packaging, PowerShell.

## Global Constraints

- Treat `.local` as the source of truth for local runtime configuration.
- Do not read, print, package, or commit `.local` secret values.
- Preserve all unrelated working-tree changes.
- Do not change the physical-delete HTTP response contract.
- Do not weaken the duplicate-file rule while cleanup is pending or retrying.
- Preserve cleanup durable-job rows, administrative audit history, knowledge candidates, and knowledge reviews.
- Do not add a database migration; the existing nullable candidate reference and cascade relationships are sufficient.
- Delete MySQL rows only after OSS and Qdrant cleanup verification succeeds.
- Do not commit, push, or deploy without separate user authorization.

---

## File Structure

- Modify `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentCleanupWorkerTests.cs`
  to cover Worker ordering, database deletion, candidate-reference retention,
  and external-verification failure.
- Modify `src/server/WechatRobot.Worker/Jobs/KnowledgeDocumentCleanupWorker.cs`
  to perform the final MySQL cleanup before completing the durable job.
- Modify `tests/server/WechatRobot.IntegrationTests/Knowledge/DocumentUploadTests.cs`
  to prove identical content can be uploaded after a completed cleanup job.
- Create
  `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentCleanupMySqlTests.cs`
  to prove the real MySQL restrictive foreign key, cascades, retained review
  history, and released SHA-256 uniqueness.
- Create a new timestamped directory and ZIP under `.local/packages/` for the
  verified IIS release; no package output is source controlled.

### Task 1: Add Worker Regression Coverage

**Files:**

- Modify: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentCleanupWorkerTests.cs`

**Interfaces:**

- Consumes: `KnowledgeDocumentCleanupWorker.ProcessOnceAsync(CancellationToken)`.
- Produces: executable regression expectations for the database cleanup and
  failure ordering.

- [ ] **Step 1: Extend the successful-cleanup test with a retained candidate**

Seed a candidate pointing to the deleted version in the existing in-memory
database:

```csharp
var candidate = new KnowledgeCandidateEntity
{
    Id = Guid.NewGuid(),
    KnowledgeDocumentVersionId = versionId,
    Question = "question",
    Answer = "answer",
    EvidenceJson = "{}",
    Status = "published"
};
database.AddRange(document, version, indexJob, candidate);
```

After `ProcessOnceAsync`, assert that the root and hash-bearing version are
gone while the candidate remains detached:

```csharp
await using var verifyScope = provider.CreateAsyncScope();
var verify = verifyScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
Assert.False(await verify.KnowledgeDocuments.AnyAsync(x => x.Id == documentId, token));
Assert.False(await verify.KnowledgeDocumentVersions.AnyAsync(x => x.Id == versionId, token));
Assert.Null((await verify.KnowledgeCandidates.SingleAsync(x => x.Id == candidate.Id, token))
    .KnowledgeDocumentVersionId);
Assert.True(jobs.Completed);
```

- [ ] **Step 2: Add an external-verification failure test**

Add `RetainDeletedVersion` to `FakeVectors` and return one point from
`InspectVersionAsync` when it is enabled:

```csharp
public bool RetainDeletedVersion { get; init; }

public Task<IReadOnlyList<VectorPointMetadata>> InspectVersionAsync(
    VectorCollection collection,
    Guid versionId,
    CancellationToken token) =>
    Task.FromResult<IReadOnlyList<VectorPointMetadata>>(
        RetainDeletedVersion
            ? [new VectorPointMetadata(
                Guid.NewGuid(),
                Guid.Empty,
                versionId,
                [],
                false,
                1)]
            : []);
```

The new test must assert that vector verification fails the durable job and
leaves both MySQL rows present:

```csharp
Assert.True(await worker.ProcessOnceAsync(token));
Assert.True(jobs.Failed);
Assert.False(jobs.Completed);
Assert.True(await verify.KnowledgeDocuments.AnyAsync(x => x.Id == documentId, token));
Assert.True(await verify.KnowledgeDocumentVersions.AnyAsync(x => x.Id == versionId, token));
```

- [ ] **Step 3: Run the focused tests and confirm the success test fails**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj `
  --filter "FullyQualifiedName~KnowledgeDocumentCleanupWorkerTests"
```

Expected before implementation: the success test fails because the document
and version still exist. The failure-ordering test passes or fails only for
test-fixture adjustments, never because MySQL rows were deleted.

- [ ] **Step 4: Review checkpoint**

Review only the cleanup test diff. Do not commit it.

### Task 2: Delete MySQL Tombstones After External Verification

**Files:**

- Modify: `src/server/WechatRobot.Worker/Jobs/KnowledgeDocumentCleanupWorker.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentCleanupWorkerTests.cs`

**Interfaces:**

- Consumes: `WechatRobotDbContext.KnowledgeDocuments`,
  `KnowledgeDocumentVersions`, and `KnowledgeCandidates`.
- Produces: a private
  `DeleteDatabaseRecordsAsync(WechatRobotDbContext, Guid, CancellationToken)`
  operation called only after vector verification.

- [ ] **Step 1: Add the database-cleanup method**

Add this method to `KnowledgeDocumentCleanupWorker`:

```csharp
private static async Task DeleteDatabaseRecordsAsync(
    WechatRobotDbContext database,
    Guid documentId,
    CancellationToken token)
{
    var versions = await database.KnowledgeDocumentVersions
        .Where(version => version.KnowledgeDocumentId == documentId)
        .ToArrayAsync(token);
    var versionIds = versions.Select(version => version.Id).ToArray();

    if (versionIds.Length != 0)
    {
        var candidates = await database.KnowledgeCandidates
            .Where(candidate =>
                candidate.KnowledgeDocumentVersionId.HasValue &&
                versionIds.Contains(candidate.KnowledgeDocumentVersionId.Value))
            .ToArrayAsync(token);
        foreach (var candidate in candidates)
            candidate.KnowledgeDocumentVersionId = null;
    }

    database.KnowledgeDocumentVersions.RemoveRange(versions);
    var document = await database.KnowledgeDocuments
        .SingleOrDefaultAsync(item => item.Id == documentId, token);
    if (document is not null)
        database.KnowledgeDocuments.Remove(document);

    await database.SaveChangesAsync(token);
}
```

`SaveChangesAsync` is the transaction boundary. Explicitly removing versions
releases the unique SHA-256 row in both relational and in-memory regression
tests; existing relational cascades remove version dependents.

- [ ] **Step 2: Call database cleanup at the correct boundary**

Insert the call after the final vector-inspection loop and before
`CompleteJobAsync`:

```csharp
foreach (var contract in contracts)
{
    if (contract.IsCollectionExclusive
        ? await vectors.InspectCollectionAsync(contract.Collection.Name, token) is not null
        : (await vectors.InspectVersionAsync(contract.Collection, contract.VersionId, token)).Count != 0)
    {
        throw new InvalidOperationException(
            $"Vector cleanup verification failed for {contract.Collection.Name}/{contract.VersionId:D}.");
    }
}

await DeleteDatabaseRecordsAsync(database, documentId, token);
await jobs.CompleteJobAsync(
    job.Id,
    job.LeaseOwner,
    timeProvider.GetUtcNow().UtcDateTime,
    token);
```

- [ ] **Step 3: Run the Worker tests**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj `
  --filter "FullyQualifiedName~KnowledgeDocumentCleanupWorkerTests"
```

Expected: all `KnowledgeDocumentCleanupWorkerTests` pass.

- [ ] **Step 4: Review checkpoint**

Confirm the diff has no early database delete, no swallowed exception, no
schema change, and no durable-job deletion. Do not commit it.

### Task 3: Prove Same-Content Re-Upload Through the HTTP Workflow

**Files:**

- Modify: `tests/server/WechatRobot.IntegrationTests/Knowledge/DocumentUploadTests.cs`

**Interfaces:**

- Consumes: existing `UploadTextAsync`, `RequestPhysicalDeleteAsync`,
  `DocumentUploadApiFactory`, and `KnowledgeDocumentCleanupWorker`.
- Produces: an API-level regression proving duplicate rejection before cleanup
  and successful re-upload after cleanup completion.

- [ ] **Step 1: Add the API workflow test**

Add:

```csharp
[Fact]
public async Task Completed_physical_delete_allows_identical_content_to_be_uploaded_again()
{
    _factory.Storage.Reset();
    using var uploader = CreateClient(SystemRoles.KnowledgeOperator);
    using var admin = CreateClient(SystemRoles.Admin);

    using var first = await UploadTextAsync(
        uploader,
        "replaceable.txt",
        "same-content-after-delete");
    first.EnsureSuccessStatusCode();
    var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>(
        TestContext.Current.CancellationToken);
    var documentId = firstBody.GetProperty("documentId").GetGuid();

    using var duplicateBeforeDelete = await UploadTextAsync(
        uploader,
        "duplicate-before-delete.txt",
        "same-content-after-delete");
    Assert.Equal(HttpStatusCode.Conflict, duplicateBeforeDelete.StatusCode);

    using var delete = await RequestPhysicalDeleteAsync(admin, documentId);
    Assert.Equal(HttpStatusCode.Accepted, delete.StatusCode);

    var worker = new KnowledgeDocumentCleanupWorker(
        _factory.Services.GetRequiredService<IServiceScopeFactory>(),
        TimeProvider.System);
    Assert.True(await worker.ProcessOnceAsync(TestContext.Current.CancellationToken));

    using var replacement = await UploadTextAsync(
        uploader,
        "replacement.txt",
        "same-content-after-delete");
    replacement.EnsureSuccessStatusCode();
    var replacementBody = await replacement.Content.ReadFromJsonAsync<JsonElement>(
        TestContext.Current.CancellationToken);
    Assert.NotEqual(documentId, replacementBody.GetProperty("documentId").GetGuid());
}
```

- [ ] **Step 2: Run the focused HTTP regression**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj `
  --filter "FullyQualifiedName~DocumentUploadTests.Completed_physical_delete_allows_identical_content_to_be_uploaded_again"
```

Expected: PASS, including the pre-delete `409 Conflict` and post-cleanup
successful upload.

- [ ] **Step 3: Review checkpoint**

Confirm the test exercises the public upload and delete endpoints and the real
Worker orchestration, without weakening active-document duplicate detection.
Do not commit it.

### Task 4: Verify Real MySQL Foreign Keys and Cascades

**Files:**

- Create:
  `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeDocumentCleanupMySqlTests.cs`

**Interfaces:**

- Consumes: `MySqlFixture`, `WechatRobotDbContext`,
  `KnowledgeDocumentCleanupWorker`, `HandoffService`, and fake OSS/vector/job
  dependencies scoped to the test.
- Produces: real-provider proof that candidate/review history survives, all
  document-owned rows cascade away, and the SHA-256 unique value can be reused.

- [ ] **Step 1: Seed a relational candidate and review linked to a document version**

Create minimal user, robot, group, conversation message, document, version,
chunk, preview, OCR page, index job, and durable cleanup job rows. Create the
candidate through `HandoffService`, then link it to the version and add a
review:

```csharp
var handoff = new HandoffService(new EfHandoffStore(database), TimeProvider.System);
var started = await handoff.StartAsync(
    new StartHandoffCommand(
        message.Id,
        robot.Id,
        group.Id,
        robot.WorkToolRobotId,
        group.Name,
        "explicit_transfer",
        "[]",
        HandoffPauseScope.Group,
        null,
        reviewer.Id,
        reviewer.UserName!,
        "physical-delete-mysql"),
    token);
var resolved = await handoff.ResolveAsync(
    started.Id,
    reviewer.Id,
    "retained answer",
    started.Version,
    token);
var candidate = await database.KnowledgeCandidates.SingleAsync(
    item => item.Id == resolved.Id,
    token);
candidate.KnowledgeDocumentVersionId = version.Id;
database.KnowledgeReviews.Add(new KnowledgeReviewEntity
{
    KnowledgeCandidateId = candidate.Id,
    ReviewerUserId = reviewer.Id,
    Decision = "approve",
    TagIdsJson = "[]",
    IdempotencyKey = "physical-delete-review"
});
await database.SaveChangesAsync(token);
```

- [ ] **Step 2: Process cleanup with MySQL and fake external providers**

Build a test `ServiceProvider` with
`AddDbContext<WechatRobotDbContext>(options => options.UseMySQL(fixture.ConnectionString))`,
the repository/fakes required by the Worker, and process one cleanup job:

```csharp
var worker = new KnowledgeDocumentCleanupWorker(
    provider.GetRequiredService<IServiceScopeFactory>(),
    TimeProvider.System);
Assert.True(await worker.ProcessOnceAsync(token));
```

- [ ] **Step 3: Assert cascades, retained history, and SHA reuse**

Use a fresh context:

```csharp
Assert.False(await verify.KnowledgeDocuments.AnyAsync(x => x.Id == document.Id, token));
Assert.False(await verify.KnowledgeDocumentVersions.AnyAsync(
    x => x.KnowledgeDocumentId == document.Id,
    token));
Assert.False(await verify.KnowledgeChunks.AnyAsync(
    x => x.KnowledgeDocumentVersionId == version.Id,
    token));
Assert.False(await verify.KnowledgeChunkPreviews.AnyAsync(
    x => x.KnowledgeDocumentVersionId == version.Id,
    token));
Assert.False(await verify.KnowledgeOcrPages.AnyAsync(
    x => x.KnowledgeDocumentVersionId == version.Id,
    token));
Assert.False(await verify.KnowledgeIndexJobs.AnyAsync(
    x => x.KnowledgeDocumentId == document.Id,
    token));
Assert.Null((await verify.KnowledgeCandidates.SingleAsync(
    x => x.Id == candidate.Id,
    token)).KnowledgeDocumentVersionId);
Assert.True(await verify.KnowledgeReviews.AnyAsync(
    x => x.KnowledgeCandidateId == candidate.Id,
    token));
```

Then add a new document/version with `Sha256 = version.Sha256` and require
`SaveChangesAsync` to succeed:

```csharp
verify.AddRange(replacementDocument, replacementVersion);
await verify.SaveChangesAsync(token);
```

- [ ] **Step 4: Run the real MySQL regression**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj `
  --filter "FullyQualifiedName~KnowledgeDocumentCleanupMySqlTests"
```

Expected: PASS against the disposable MySQL 8.4 container.

- [ ] **Step 5: Review checkpoint**

Confirm the test exercises real foreign keys and cascades and does not use
production `.local` database configuration. Do not commit it.

### Task 5: Full Verification and IIS Release Package

**Files:**

- Verify all task files above.
- Create:
  `.local/packages/wechatrobot-windows-iis-<yyyyMMdd-HHmmss>/release/`
- Create:
  `.local/packages/wechatrobot-windows-iis-<yyyyMMdd-HHmmss>.zip`

**Interfaces:**

- Consumes: the verified backend, frontend source, existing deployment layout,
  and non-secret `.env.example`.
- Produces: a complete API + Worker + frontend IIS release ZIP and SHA-256.

- [ ] **Step 1: Run relevant backend verification**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj `
  --filter "FullyQualifiedName~KnowledgeDocumentCleanupWorkerTests|FullyQualifiedName~KnowledgeDocumentCleanupMySqlTests|FullyQualifiedName~DocumentUploadTests"
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj
```

Expected: all selected integration tests and both complete unit/contract
projects pass.

- [ ] **Step 2: Run frontend and diff verification**

Run:

```powershell
Push-Location src/web/wechatrobot-admin
npm run typecheck
npm test -- --run
npm run build
Pop-Location
git diff --check
```

Expected: all commands pass. Report unrelated baseline failures separately
without changing unrelated files.

- [ ] **Step 3: Publish the complete release layout**

Create a new timestamped package directory under `.local/packages`, publish
fresh API and Worker binaries, and copy the freshly built frontend plus the
existing deployment configuration:

```powershell
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$packageRoot = Join-Path (Resolve-Path '.local/packages') "wechatrobot-windows-iis-$stamp"
$releaseRoot = Join-Path $packageRoot 'release'
$apiRoot = Join-Path $releaseRoot 'api'
$workerRoot = Join-Path $releaseRoot 'worker'
$frontendRoot = Join-Path $releaseRoot 'frontend'
$configRoot = Join-Path $releaseRoot 'config'

New-Item -ItemType Directory -Force $apiRoot, $workerRoot, $frontendRoot, $configRoot | Out-Null
dotnet publish src/server/WechatRobot.Api/WechatRobot.Api.csproj -c Release -o $apiRoot
dotnet publish src/server/WechatRobot.Worker/WechatRobot.Worker.csproj -c Release -o $workerRoot
Copy-Item 'src/web/wechatrobot-admin/dist/*' $frontendRoot -Recurse -Force
Copy-Item 'deploy/iis/wxrobot.aavisa.com.frontend.web.config' `
  (Join-Path $frontendRoot 'web.config') -Force
Copy-Item 'deploy/windows/wechatrobot.env.example' `
  (Join-Path $configRoot '.env.example') -Force
```

Verify the copied production `.env.example` contains replacement markers only;
never copy the repository-root local example or `.local/.env`.

- [ ] **Step 4: Archive and hash the release**

Run:

```powershell
$zipPath = "$packageRoot.zip"
Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
$hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
$hash | Format-List Algorithm,Hash,Path
```

Expected: a new ZIP containing `release/api`, `release/worker`,
`release/frontend`, and `release/config`, with a reported SHA-256.

- [ ] **Step 5: Final review checkpoint**

Report changed source/test/docs files, exact passing commands, package path,
ZIP size, and SHA-256. Clearly distinguish all pre-existing working-tree
changes. Do not commit, push, or deploy.
