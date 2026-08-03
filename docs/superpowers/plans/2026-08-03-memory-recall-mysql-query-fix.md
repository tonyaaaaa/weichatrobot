# Memory Recall MySQL Query Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate the EF Core/MySQL query exception observed after memory-vector search while preserving sender isolation, bounded lookup, and non-fatal memory behavior.

**Architecture:** Keep Qdrant recall bounded to 20 IDs, then execute four small provider-stable MySQL scope queries instead of one dynamically batched predicate combined with a large OR expression. Merge unique rows in memory, retain existing scope priority and character limits, and keep any remaining recall failure isolated from knowledge RAG.

**Tech Stack:** .NET 10, EF Core, MySQL.EntityFrameworkCore, MySQL 5.7 integration fixture, xUnit v3/Microsoft Testing Platform.

## Global Constraints

- Do not expose recalled content, subject keys, vectors, credentials, connection strings, or exception details in logs or API responses.
- Keep vector hits bounded at 20, returned memories bounded at 5, and returned content bounded at 2,000 characters.
- Preserve `Global`, `Robot`, `Group`, and sender-isolated `User` scope semantics exactly.
- Cancellation requested by the caller must still propagate.
- A memory-only failure must return `memory_recall_unavailable` and must not fail knowledge retrieval.

---

### Task 1: Reproduce and replace the provider-fragile scope query

**Files:**
- Modify: `tests/server/WechatRobot.IntegrationTests/Memory/MemoryRecallMySqlTests.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Memory/MemoryRecallService.cs`

**Interfaces:**
- Consumes: at most 20 `MemoryVectorHit` IDs and the normalized subject key.
- Produces: `LoadVisibleEntriesAsync(Guid[], Guid, Guid, string, DateTime, CancellationToken)` returning unique authorized `MemoryEntryEntity` rows.

- [ ] **Step 1: Add a production-shape failing MySQL regression**

```csharp
[Fact]
public async Task Recall_loads_twenty_mixed_scope_hits_without_provider_query_failure()
{
    var fixtureData = await SeedMixedScopeEntriesAsync(database, robotId, groupId, subjectKey, count: 20);
    var service = Service(database, fixtureData.AllHits);
    var result = await service.RecallAsync("日本签证材料", robotId, groupId, subjectKey, Token);
    Assert.Null(result.FailureCode);
    Assert.DoesNotContain(result.Memories, x => fixtureData.ForbiddenIds.Contains(x.Id));
    Assert.True(result.Memories.Count <= 5);
}
```

Seed allowed and forbidden Robot, Group, and User rows plus Global rows. Include expired and inactive hits. Use the real MySQL fixture and the same 20-hit shape present in production.

- [ ] **Step 2: Run the focused MySQL regression against the current query**

Run: `dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter FullyQualifiedName~MemoryRecallMySqlTests`

Expected: reproduce the provider failure or capture that the current test environment passes. In either case, retain the production-shape test as a regression and proceed with the deterministic query rewrite because production evidence already shows the failure twice.

- [ ] **Step 3: Implement four bounded scope queries**

```csharp
private async Task<MemoryEntryEntity[]> LoadVisibleEntriesAsync(
    Guid[] ids, Guid robotId, Guid groupId, string subject, DateTime now, CancellationToken token)
{
    var visible = new Dictionary<Guid, MemoryEntryEntity>();
    foreach (var batch in GuidBatchQuery.CreateBatches(ids))
    {
        var idFilter = GuidBatchQuery.BuildPredicate<MemoryEntryEntity>(batch, x => x.Id);
        var baseQuery = database.MemoryEntries.AsNoTracking().Where(idFilter)
            .Where(x => x.Status == "active" && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > now));
        await AddAsync(baseQuery.Where(x => x.ScopeType == "Global"), visible, token);
        await AddAsync(baseQuery.Where(x => x.ScopeType == "Robot" && x.RobotConfigId == robotId), visible, token);
        await AddAsync(baseQuery.Where(x => x.ScopeType == "Group" && x.RobotConfigId == robotId && x.GroupProfileId == groupId), visible, token);
        await AddAsync(baseQuery.Where(x => x.ScopeType == "User" && x.RobotConfigId == robotId && x.GroupProfileId == groupId && x.SubjectKey == subject), visible, token);
    }
    return visible.Values.ToArray();
}
```

`AddAsync` awaits `ToArrayAsync` sequentially and inserts by ID. Do not run queries concurrently on the scoped `WechatRobotDbContext`.

- [ ] **Step 4: Add null-subject and cross-user isolation assertions**

Verify `MemoryScope.NormalizeSubject(null)` behavior, ensure another sender's `User` memory is never returned, and ensure Global/Robot/Group ordering remains below User priority only according to the existing `ScopePriority` method.

- [ ] **Step 5: Run memory unit/integration tests**

Run: `dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~MemoryRecallMySqlTests|FullyQualifiedName~MemoryEndpointTests"`

Run: `dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~Memory|FullyQualifiedName~GroundedAnswerTests"`

Expected: PASS.

- [ ] **Step 6: Commit the memory fix**

```powershell
git add src/server/WechatRobot.Infrastructure/Memory/MemoryRecallService.cs tests/server/WechatRobot.IntegrationTests/Memory/MemoryRecallMySqlTests.cs
git commit -m "fix: stabilize mysql memory recall queries"
```

### Task 2: Verify the failure remains non-fatal at the answer boundary

**Files:**
- Modify: `tests/server/WechatRobot.UnitTests/Conversations/GroundedAnswerTests.cs`

**Interfaces:**
- Consumes: `MemoryRecallResult([], "memory_recall_unavailable")`.
- Produces: a normal knowledge answer when retrieval and chat succeed despite memory failure.

- [ ] **Step 1: Add the answer-boundary regression**

```csharp
[Fact]
public async Task Memory_failure_does_not_replace_a_grounded_knowledge_answer()
{
    var service = Service(memory: new MemoryRecallResult([], "memory_recall_unavailable"), evidence: [Evidence()]);
    var result = await service.AnswerAsync(Request(), Token);
    Assert.Equal(AnswerDecisionKind.Answer, result.Decision);
    Assert.NotEqual("retrieval_unavailable", result.FailureCode);
}
```

- [ ] **Step 2: Run the test and preserve existing behavior**

Run: `dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter FullyQualifiedName~GroundedAnswerTests`

Expected: PASS. If it fails, adjust only the memory-failure branch so knowledge evidence remains authoritative; do not suppress knowledge/provider failures.

- [ ] **Step 3: Commit only if the boundary test changed**

```powershell
git add tests/server/WechatRobot.UnitTests/Conversations/GroundedAnswerTests.cs
git commit -m "test: keep memory recall failures nonfatal"
```
