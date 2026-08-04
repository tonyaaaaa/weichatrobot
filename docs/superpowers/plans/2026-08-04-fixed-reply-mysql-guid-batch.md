# Fixed Reply MySQL Guid Batch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make fixed-reply administration, group routing, and private routing load multiple template IDs without triggering the MySQL EF Provider Guid collection parameter failure.

**Architecture:** Keep `FixedReplyTemplateStore` as the persistence boundary and reuse `GuidBatchQuery` for every child-row lookup keyed by template IDs. Preserve current ordering, scope rules, Agent routing, fallback behavior, API contracts, and database schema.

**Tech Stack:** ASP.NET Core 10, Entity Framework Core 10, MySql.EntityFrameworkCore 10.0.7, xUnit v3/Microsoft Testing Platform.

## Global Constraints

- Modify all four runtime Guid collection queries in `FixedReplyTemplateStore` together.
- Do not add packages, configuration keys, schema changes, or migrations.
- Do not use containers, connect to production data, or start API/Worker processes.
- Preserve fixed-reply ordering and the per-template example limit.
- Commit and package only after fresh tests and builds pass.

---

### Task 1: Reproduce provider-independent fixed-reply child loading failure

**Files:**
- Modify: `tests/server/WechatRobot.IntegrationTests/FixedReplies/PrivateFixedReplyTemplateStoreTests.cs`

**Interfaces:**
- Consumes: `FixedReplyTemplateStore.ListEffectiveAsync`, `FixedReplyTemplateStore.ListEffectiveForPrivateAsync`, and the admin list endpoint.
- Produces: regression coverage proving multiple templates can load examples and group rules without runtime Guid collection parameter support.

- [ ] **Step 1: Add failing tests for multiple-template child loading**

Add a test-only `IQueryExpressionInterceptor` that throws `InvalidOperationException("runtime_guid_contains_not_supported")` when a compiled EF query contains `Enumerable.Contains<Guid>` or `Queryable.Contains<Guid>`. Register it on the existing InMemory context so ordinary EF reads remain real while reproducing the MySQL Provider's unsupported runtime Guid collection boundary.

Use three focused cases:

- the existing private-scope case must return four enabled templates with one example each;
- a group-scope case must return the applicable global and selected-group templates with their examples;
- an administration-list case must return multiple templates with their examples and group rules.

- [ ] **Step 2: Run only the new tests and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class "WechatRobot.IntegrationTests.FixedReplies.PrivateFixedReplyTemplateStoreTests"
```

Expected: all three multi-ID paths fail with `runtime_guid_contains_not_supported` before the production change.

### Task 2: Replace all fixed-reply Guid collection queries

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/FixedReplyTemplateStore.cs`

**Interfaces:**
- Consumes: `GuidBatchQuery.CreateBatches(IEnumerable<Guid>, int)` and `GuidBatchQuery.BuildPredicate<TEntity>(IReadOnlyCollection<Guid>, Expression<Func<TEntity, Guid>>)`.
- Produces: private helper methods that return ordered example and rule rows for bounded template-ID batches.

- [ ] **Step 1: Implement bounded example loading**

Add a private `LoadExamplesAsync(IReadOnlyCollection<Guid>, CancellationToken)` method. For each `GuidBatchQuery.CreateBatches(ids)` result, build a predicate selecting `FixedReplyTemplateExampleEntity.TemplateId`, execute `AsNoTracking().Where(predicate).OrderBy(item => item.Id).ToArrayAsync`, and append rows to one list. Return the list ordered by `Id` so batching cannot alter observable order.

- [ ] **Step 2: Implement bounded group-rule loading**

Add a private `LoadGroupRulesAsync(IReadOnlyCollection<Guid>, CancellationToken)` method using the same pattern with `FixedReplyTemplateGroupRuleEntity.TemplateId`. Return rows ordered by `GroupProfileId` and then template ID to make the existing view mapping deterministic.

- [ ] **Step 3: Replace four call sites**

Use `LoadExamplesAsync` in `ListEffectiveAsync`, `ListEffectiveForPrivateAsync`, and `ViewsAsync`. Use `LoadGroupRulesAsync` in `ViewsAsync`. Remove every `ids.Contains(item.TemplateId)` occurrence from this store while keeping the existing template queries and in-memory projection unchanged.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the Task 1 command and confirm all selected tests pass.

### Task 3: Verify routing behavior and release artifacts

**Files:**
- Verify: `src/server/WechatRobot.Infrastructure/Agents/TemplateRoutingAgent.cs`
- Verify: `src/server/WechatRobot.Application/Messaging/InboundMessageProcessor.cs`
- Create: `artifacts/WechatRobot-<commit>-20260804.zip`

**Interfaces:**
- Consumes: fixed-reply candidates loaded by Task 2.
- Produces: release package containing `api`, `web`, and `worker` without `.local` or `.env`.

- [ ] **Step 1: Run fixed-reply integration tests**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-namespace "WechatRobot.IntegrationTests.FixedReplies"
```

- [ ] **Step 2: Run backend regression and builds**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore
dotnet build src/server/WechatRobot.Api/WechatRobot.Api.csproj --no-restore
dotnet build src/server/WechatRobot.Worker/WechatRobot.Worker.csproj --no-restore
npm --prefix src/web/wechatrobot-admin run build
git diff --check
```

- [ ] **Step 3: Commit implementation**

```powershell
git add -- src/server/WechatRobot.Infrastructure/Persistence/FixedReplyTemplateStore.cs tests/server/WechatRobot.IntegrationTests/FixedReplies
git diff --cached --check
git commit -m "fix: stabilize fixed reply template queries"
```

- [ ] **Step 4: Publish and validate the archive**

Publish API and Worker in Release mode, copy the frontend `dist` directory, compress the three top-level directories, verify required assemblies and `web/index.html`, verify no `.local` or `.env` entry exists, and calculate SHA256.
