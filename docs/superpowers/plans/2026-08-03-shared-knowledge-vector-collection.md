# Shared Knowledge Vector Collection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Store all active knowledge produced by one embedding-space contract in one Qdrant collection, migrate existing exclusive collections without recomputing embeddings, and restore private/group RAG retrieval beyond 64 documents.

**Architecture:** A deterministic non-secret embedding contract key selects one shared collection. MySQL remains authoritative for active versions and tag visibility; Qdrant payload filters enforce `active`, `version_id`, and `tag_ids`. Existing vectors are copied from legacy exclusive collections in bounded pages, verified per version, switched transactionally in MySQL, and only then are old collections eligible for cleanup.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core/MySQL, Qdrant HTTP API, xUnit v3/Microsoft Testing Platform, PowerShell operations.

## Global Constraints

- Do not recompute embeddings during normal migration; copy the existing vectors and payloads from Qdrant.
- Never mix different embedding provider/model identities, dimensions, or distance algorithms in one collection.
- Do not partition by knowledge tag; preserve existing multi-tag OR and globally public behavior through payload filters.
- Do not print `.local` secrets, vectors, chunk text, connection strings, credentials, or upstream response bodies.
- Shared collections must never be physically deleted by document/version cleanup.
- Keep MySQL authoritative for active-version visibility at every Qdrant/MySQL failure boundary.
- Production mutation requires a separate explicit authorization after dry-run evidence.
- Preserve unrelated working-tree changes and use centrally managed package versions.

---

## File Map

- `src/server/WechatRobot.Application/Knowledge/EmbeddingSpaceContract.cs`: derives and validates the safe contract key and collection name.
- `src/server/WechatRobot.Application/Knowledge/IVectorStore.cs`: exposes payload-index and vector-page contracts used by runtime and migration.
- `src/server/WechatRobot.Application/Knowledge/IKnowledgeService.cs`: carries contract metadata through leased index work.
- `src/server/WechatRobot.Application/Knowledge/KnowledgeIndexService.cs`: indexes inactive shared points and preserves activation ordering.
- `src/server/WechatRobot.Infrastructure/Knowledge/QdrantVectorStore.cs`: manages payload indexes, vector scrolling, and shared-collection deletion protection.
- `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs`: queues shared jobs, persists contracts, activates versions, and searches one matching collection.
- `src/server/WechatRobot.Infrastructure/Persistence/Entities/KnowledgeDocumentEntity.cs`: persists the active embedding contract key.
- `src/server/WechatRobot.Infrastructure/Persistence/Entities/KnowledgeDocumentVersionEntity.cs`: persists the indexed embedding contract key.
- `src/server/WechatRobot.Infrastructure/Persistence/Entities/KnowledgeIndexJobEntity.cs`: persists current and previous contract keys for retries and cleanup.
- `src/server/WechatRobot.Infrastructure/Persistence/Configurations/KnowledgeDocumentConfigurations.cs`: maps bounded contract-key columns.
- `src/server/WechatRobot.Infrastructure/Persistence/Migrations/20260803090000_AddSharedKnowledgeVectorContracts.cs`: adds nullable migration-safe columns.
- `src/server/WechatRobot.Infrastructure/Persistence/Migrations/20260803090000_AddSharedKnowledgeVectorContracts.Designer.cs`: generated migration metadata.
- `src/server/WechatRobot.Infrastructure/Persistence/Migrations/WechatRobotDbContextModelSnapshot.cs`: updated EF model snapshot.
- `src/server/WechatRobot.Worker/Jobs/KnowledgeIndexWorker.cs`: enforces shared cleanup safeguards.
- `tools/WechatRobot.KnowledgeVectorMigration/*`: dry-run/apply/resume/verify/rollback migration command.
- `docs/runbooks/shared-knowledge-vector-migration.md`: exact maintenance and acceptance procedure.

### Task 1: Persist a deterministic embedding-space contract

**Files:**
- Create: `src/server/WechatRobot.Application/Knowledge/EmbeddingSpaceContract.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/KnowledgeDocumentEntity.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/KnowledgeDocumentVersionEntity.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/KnowledgeIndexJobEntity.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Configurations/KnowledgeDocumentConfigurations.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/20260803090000_AddSharedKnowledgeVectorContracts.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/20260803090000_AddSharedKnowledgeVectorContracts.Designer.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/WechatRobotDbContextModelSnapshot.cs`
- Test: `tests/server/WechatRobot.UnitTests/Knowledge/EmbeddingSpaceContractTests.cs`
- Test: `tests/server/WechatRobot.ContractTests/Persistence/MySql57MigrationCompatibilityTests.cs`

**Interfaces:**
- Produces: `EmbeddingSpaceContract.Create(string provider, string baseUrl, string model, int dimension, VectorDistance distance)`, `IsSharedCollectionName(string)`, and properties `Key`, `CollectionName`, `Dimension`, `Distance`.
- Produces: nullable `ActiveEmbeddingContractKey`, `IndexEmbeddingContractKey`, `EmbeddingContractKey`, and `PreviousActiveEmbeddingContractKey` persistence fields.

- [ ] **Step 1: Write the failing contract-key tests**

```csharp
[Fact]
public void Same_semantic_model_settings_produce_the_same_safe_collection()
{
    var first = EmbeddingSpaceContract.Create("glm", "https://open.bigmodel.cn/api/paas/v4", "embedding-3", 1024, VectorDistance.Cosine);
    var second = EmbeddingSpaceContract.Create("glm", "https://open.bigmodel.cn/api/paas/v4/", "embedding-3", 1024, VectorDistance.Cosine);
    Assert.Equal(first.Key, second.Key);
    Assert.Equal(first.CollectionName, second.CollectionName);
    Assert.DoesNotContain("api-key", first.CollectionName, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void Different_models_do_not_share_a_contract()
{
    Assert.NotEqual(
        EmbeddingSpaceContract.Create("glm", "https://open.bigmodel.cn/api/paas/v4", "embedding-3", 1024, VectorDistance.Cosine).Key,
        EmbeddingSpaceContract.Create("glm", "https://open.bigmodel.cn/api/paas/v4", "embedding-4", 1024, VectorDistance.Cosine).Key);
}
```

- [ ] **Step 2: Run the focused tests and confirm the missing type failure**

Run: `dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter FullyQualifiedName~EmbeddingSpaceContractTests`

Expected: FAIL because `EmbeddingSpaceContract` does not exist.

- [ ] **Step 3: Implement the contract and persistence fields**

```csharp
public sealed record EmbeddingSpaceContract(string Key, string CollectionName, int Dimension, VectorDistance Distance)
{
    public static EmbeddingSpaceContract Create(string provider, string baseUrl, string model, int dimension, VectorDistance distance)
    {
        var identity = $"{provider.Trim().ToLowerInvariant()}\n{baseUrl.Trim().TrimEnd('/').ToLowerInvariant()}\n{model.Trim()}\n{dimension}\n{distance}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..16];
        return new($"{hash}:{distance}:{dimension}", $"kb_{hash}_{distance.ToString().ToLowerInvariant()}_{dimension}", dimension, distance);
    }
}
```

Map every contract-key column as nullable `varchar(96)`. Generate/review the migration so existing rows remain valid and no existing value is backfilled without Qdrant verification.

- [ ] **Step 4: Add MySQL 5.7 migration assertions and run migration tests**

Run: `dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --filter FullyQualifiedName~MySql57MigrationCompatibilityTests`

Expected: PASS; generated SQL contains only supported nullable string-column additions.

- [ ] **Step 5: Commit the contract slice**

```powershell
git add src/server/WechatRobot.Application/Knowledge/EmbeddingSpaceContract.cs src/server/WechatRobot.Infrastructure/Persistence tests/server/WechatRobot.UnitTests/Knowledge/EmbeddingSpaceContractTests.cs tests/server/WechatRobot.ContractTests/Persistence/MySql57MigrationCompatibilityTests.cs
git commit -m "feat: persist knowledge embedding contracts"
```

### Task 2: Add shared-collection payload indexes and vector export

**Files:**
- Modify: `src/server/WechatRobot.Application/Knowledge/IVectorStore.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Knowledge/QdrantVectorStore.cs`
- Modify: all test `IVectorStore` fakes reported by `rg -n "IVectorStore" tests/server`
- Test: `tests/server/WechatRobot.IntegrationTests/Knowledge/QdrantKnowledgeTests.cs`

**Interfaces:**
- Consumes: `VectorCollection` and `VectorPoint`.
- Produces: `EnsurePayloadIndexesAsync(VectorCollection, CancellationToken)`.
- Produces: `ReadVersionPointsAsync(VectorCollection, Guid, string?, int, CancellationToken)` returning `VectorPointPage(Points, NextOffset)` with vectors included.

- [ ] **Step 1: Add failing Qdrant tests for index creation and vector paging**

```csharp
[Fact]
public async Task Shared_collection_indexes_payload_and_round_trips_vectors()
{
    await _store.EnsureCollectionAsync(_collection, Token);
    await _store.EnsurePayloadIndexesAsync(_collection, Token);
    await _store.UpsertAsync(_collection, [_point], Token);
    var page = await _store.ReadVersionPointsAsync(_collection, _point.VersionId, null, 100, Token);
    Assert.Equal(_point.Vector, Assert.Single(page.Points).Vector);
    Assert.Null(page.NextOffset);
}
```

- [ ] **Step 2: Run the focused integration test and confirm interface failures**

Run: `dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter FullyQualifiedName~QdrantKnowledgeTests`

Expected: FAIL because the two new methods and `VectorPointPage` do not exist.

- [ ] **Step 3: Implement idempotent payload indexes and bounded vector scroll**

Use Qdrant `PUT /collections/{name}/index?wait=true` for `active` (`bool`) and the three keyword fields. Use `/points/scroll` with `with_vector=true`, `with_payload=true`, the exact `version_id` filter, and a clamped page size of 1–256. Parse vectors without logging their values.

```csharp
public sealed record VectorPointPage(IReadOnlyList<VectorPoint> Points, string? NextOffset);
```

- [ ] **Step 4: Add no-op/recording implementations to all test fakes and rerun tests**

Run: `dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~QdrantKnowledgeTests|FullyQualifiedName~KnowledgeRetrievalVisibilityTests"`

Expected: PASS.

- [ ] **Step 5: Commit the vector-store slice**

```powershell
git add src/server/WechatRobot.Application/Knowledge/IVectorStore.cs src/server/WechatRobot.Infrastructure/Knowledge/QdrantVectorStore.cs tests/server
git commit -m "feat: support shared qdrant collections"
```

### Task 3: Queue and activate new indexes in the shared collection

**Files:**
- Modify: `src/server/WechatRobot.Application/Knowledge/IKnowledgeService.cs`
- Modify: `src/server/WechatRobot.Application/Knowledge/KnowledgeIndexService.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs`
- Test: `tests/server/WechatRobot.UnitTests/Knowledge/KnowledgeIndexServiceTests.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeIndexMySqlConcurrencyTests.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateKnowledgeIngestPipelineTests.cs`

**Interfaces:**
- Consumes: `EmbeddingSpaceContract` and `EnsurePayloadIndexesAsync`.
- Produces: `KnowledgeIndexWork.EmbeddingContractKey` and `PreviousActiveEmbeddingContractKey`.
- Preserves: pre-activate Qdrant, then atomically switch MySQL, then enqueue previous-version cleanup.

- [ ] **Step 1: Write failing tests for deterministic collection reuse**

```csharp
[Fact]
public async Task Two_documents_with_one_embedding_contract_queue_the_same_nonexclusive_collection()
{
    var first = await QueueApprovedDocumentAsync();
    var second = await QueueApprovedDocumentAsync();
    Assert.Equal(first.CollectionName, second.CollectionName);
    Assert.False(first.IsCollectionExclusive);
    Assert.False(second.IsCollectionExclusive);
}
```

Add failure-boundary assertions that new points are active before the MySQL switch, old versions remain filtered by `ActiveVersionId`, and a failed switch deactivates/deletes only the new version points.

- [ ] **Step 2: Run focused unit and MySQL concurrency tests**

Run: `dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter FullyQualifiedName~KnowledgeIndexServiceTests`

Run: `dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter FullyQualifiedName~KnowledgeIndexMySqlConcurrencyTests`

Expected: FAIL because jobs still create `_g<generation>_<job-id>` exclusive collections.

- [ ] **Step 3: Change queueing and activation to shared provenance**

In `QueueIndexCoreAsync`, derive the contract from the leased model configuration, set `CollectionName` to its deterministic name, set `IsCollectionExclusive=false`, and persist the current/previous contract keys. In `KnowledgeIndexService.IndexAsync`, ensure payload indexes before the first upsert. Keep points inactive until the existing pre-activation step.

Update normal and private-batch activation to persist contract fields alongside collection/dimension/distance. If MySQL activation fails, call `DeleteVersionAsync`, never `DeleteCollectionAsync`, for shared work.

- [ ] **Step 4: Add hard shared-deletion guards**

Before any exclusive collection deletion, require all of:

```csharp
job.IsCollectionExclusive
&& !EmbeddingSpaceContract.IsSharedCollectionName(job.CollectionName)
&& !await database.KnowledgeDocuments.AnyAsync(x => x.ActiveCollectionName == job.CollectionName, token)
```

Reject contradictory provenance with a stable failure code rather than attempting deletion.

- [ ] **Step 5: Run the complete index and private-ingest slices**

Run: `dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~KnowledgeIndexServiceTests|FullyQualifiedName~KnowledgeSearchFanoutTests"`

Run: `dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~KnowledgeIndexMySqlConcurrencyTests|FullyQualifiedName~PrivateKnowledgeIngestPipelineTests|FullyQualifiedName~KnowledgeDocumentCleanup"`

Expected: PASS.

- [ ] **Step 6: Commit the shared indexing slice**

```powershell
git add src/server/WechatRobot.Application/Knowledge src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs src/server/WechatRobot.Worker/Jobs/KnowledgeIndexWorker.cs tests/server
git commit -m "feat: index knowledge into shared collections"
```

### Task 4: Search one collection for more than 64 visible documents

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Conversations/KnowledgeRetrievalEvidenceProvider.cs`
- Modify: `tests/server/WechatRobot.UnitTests/Knowledge/KnowledgeSearchFanoutTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeRetrievalVisibilityTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeRetrievalMySql57CompatibilityTests.cs`

**Interfaces:**
- Consumes: query embedding configuration and its `EmbeddingSpaceContract`.
- Produces: one `VectorSearchRequest` containing every eligible active version under the matching contract.

- [ ] **Step 1: Replace the capacity regression with a failing shared-search regression**

```csharp
[Fact]
public async Task Three_hundred_twenty_two_documents_in_one_contract_use_one_vector_search()
{
    for (var index = 0; index < 322; index++) AddActiveDocument(database, tag.Id, sharedCollection, contractKey);
    var hits = await service.SearchVisibleAsync(vector, scope, vectors, 8, Token);
    Assert.Equal(1, vectors.CallCount);
    Assert.Equal(322, Assert.Single(vectors.Requests).ActiveVersionIds.Count);
}
```

Also add tests that an incompatible contract fails explicitly and that multi-tag/global-public OR visibility is unchanged.

- [ ] **Step 2: Run the focused search tests**

Run: `dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter FullyQualifiedName~KnowledgeSearchFanoutTests`

Expected: FAIL because the service still groups 322 exclusive collection names and throws at 64.

- [ ] **Step 3: Implement contract-matched single-collection search**

Pass the query contract from `KnowledgeRetrievalEvidenceProvider` into `SearchVisibleAsync`. Filter active documents by `ActiveEmbeddingContractKey`, require exactly one shared collection for the current contract after migration, and build one request with all eligible version IDs. Preserve the legacy bounded fan-out path only for pre-migration exclusive rows and keep its explicit capacity failure.

- [ ] **Step 4: Run unit and MySQL retrieval coverage**

Run: `dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter FullyQualifiedName~KnowledgeSearchFanoutTests`

Run: `dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~KnowledgeRetrievalVisibilityTests|FullyQualifiedName~KnowledgeRetrievalMySql57CompatibilityTests"`

Expected: PASS.

- [ ] **Step 5: Commit the retrieval slice**

```powershell
git add src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs src/server/WechatRobot.Infrastructure/Conversations/KnowledgeRetrievalEvidenceProvider.cs tests/server
git commit -m "fix: retrieve knowledge from shared collections"
```

### Task 5: Build the resumable vector-copy migration command

**Files:**
- Create: `tools/WechatRobot.KnowledgeVectorMigration/WechatRobot.KnowledgeVectorMigration.csproj`
- Create: `tools/WechatRobot.KnowledgeVectorMigration/Program.cs`
- Create: `tools/WechatRobot.KnowledgeVectorMigration/KnowledgeVectorMigrationRunner.cs`
- Create: `tools/WechatRobot.KnowledgeVectorMigration/KnowledgeVectorMigrationPlanner.cs`
- Create: `tools/WechatRobot.KnowledgeVectorMigration/MigrationCheckpoint.cs`
- Create: `tests/server/WechatRobot.UnitTests/Knowledge/KnowledgeVectorMigrationPlannerTests.cs`
- Modify: `WechatRobot.slnx`

**Interfaces:**
- Consumes: `WechatRobotDbContext`, `IVectorStore`, `EmbeddingSpaceContract`, `.local` configuration.
- Produces commands: `--dry-run`, `--apply`, `--resume <checkpoint>`, `--verify <checkpoint>`, `--rollback <checkpoint>`.

- [ ] **Step 1: Write failing planner tests**

```csharp
[Fact]
public void Planner_groups_versions_by_contract_without_requesting_embeddings()
{
    var plan = planner.Build(activeVersions);
    Assert.Single(plan.Destinations);
    Assert.Equal(322, plan.Versions.Count);
    Assert.All(plan.Versions, x => Assert.NotEqual(x.SourceCollection, x.DestinationCollection));
}

[Fact]
public void Any_point_mismatch_blocks_database_switch()
{
    Assert.False(planner.CanSwitch([new VersionVerification(expected: 5, actual: 4, metadataMatches: true)]));
}
```

- [ ] **Step 2: Run the tests and confirm missing planner failures**

Run: `dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter FullyQualifiedName~KnowledgeVectorMigrationPlannerTests`

Expected: FAIL because the migration planner does not exist.

- [ ] **Step 3: Implement dry-run and checkpoint state**

The dry run reads active mappings and source point metadata only. It reports version/document/collection counts and validation codes without vectors or content. The checkpoint state machine is:

```text
Planned -> Copied -> Verified -> Switched -> Accepted -> CleanupQueued
```

Write checkpoint updates atomically to a caller-supplied local path outside committed source.

- [ ] **Step 4: Implement bounded vector copying and verification**

Read at most 256 points per page using `ReadVersionPointsAsync`, upsert into the destination, and verify exact chunk-ID, document-ID, version-ID, tag-ID, generation, active-state, and point-count equality. Do not call `IEmbeddingClient` anywhere in the tool.

- [ ] **Step 5: Implement transactional switch and pre-cleanup rollback**

Update only rows whose active version and source collection still equal the checkpoint. Set shared collection, contract key, and exclusive flags in one transaction. Rollback restores the checkpointed mapping only while old collections still pass consistency checks and cleanup has not begun.

- [ ] **Step 6: Run tool tests and build**

Run: `dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter FullyQualifiedName~KnowledgeVectorMigrationPlannerTests`

Run: `dotnet build tools/WechatRobot.KnowledgeVectorMigration/WechatRobot.KnowledgeVectorMigration.csproj`

Expected: PASS.

- [ ] **Step 7: Commit the migration tool**

```powershell
git add tools/WechatRobot.KnowledgeVectorMigration tests/server/WechatRobot.UnitTests/Knowledge/KnowledgeVectorMigrationPlannerTests.cs WechatRobot.slnx
git commit -m "feat: add knowledge vector migration tool"
```

### Task 6: Document and verify the offline migration procedure

**Files:**
- Create: `docs/runbooks/shared-knowledge-vector-migration.md`
- Modify: `docs/runbooks/knowledge-pipeline-smoke-test.md`

**Interfaces:**
- Consumes: migration tool commands and `.local` startup contract.
- Produces: operator gates for dry-run, apply, acceptance, rollback, and cleanup.

- [ ] **Step 1: Write the runbook with exact safe commands**

Document environment loading by setting `WECHATROBOT_ENV_FILE` to the absolute `.local/.env` path without printing it. Include checks for no leased/activating jobs, stopped Worker, checkpoint location, dry-run totals, per-version verification, MySQL switch, API/Worker restart, and old-collection cleanup approval.

- [ ] **Step 2: Add acceptance queries**

Require a private and group question over `签证知识`, then query conversation audit and assert neither result has `failureCode=retrieval_unavailable`. Require one document update in the shared collection and verify another document's point count is unchanged.

- [ ] **Step 3: Review for destructive boundaries**

Verify the runbook never deletes old collections before `Accepted`, never targets a broad directory, never prints secrets, and clearly states that `--apply`, `--rollback`, and cleanup mutate production.

- [ ] **Step 4: Commit the runbook**

```powershell
git add docs/runbooks/shared-knowledge-vector-migration.md docs/runbooks/knowledge-pipeline-smoke-test.md
git commit -m "docs: add shared vector migration runbook"
```

### Task 7: Run repository verification and prepare dry-run evidence

**Files:**
- Modify only files required by failures introduced by Tasks 1–6; do not fix unrelated baseline failures.

- [ ] **Step 1: Run diff hygiene and backend unit tests**

Run: `git diff --check`

Run: `dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj`

Expected: PASS.

- [ ] **Step 2: Run contract and integration tests**

Run: `dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj`

Run: `dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj`

Expected: PASS, or report infrastructure-unavailable tests separately with exact names.

- [ ] **Step 3: Build the complete solution**

Run: `dotnet build WechatRobot.slnx --no-restore`

Expected: build succeeds with zero errors.

- [ ] **Step 4: Execute production-target dry-run only**

With the Worker still stopped as requested and `.local` loaded securely, run the migration tool with `--dry-run`. Confirm the planned active-version count, source collection count, destination contract count, and mismatch count. Do not use `--apply` without a new explicit production-mutation authorization.

If verification exposes a task-scoped defect, return to the owning task, change only that task's listed files, rerun its focused tests, and include the correction in that task's commit. Do not create a catch-all verification commit.
