# Dashboard Operational Summary Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add one authenticated administration API and a real dashboard that show robot, knowledge, queue, dead-letter, and dependency-readiness state without including human-handoff statistics.

**Architecture:** A scoped dashboard query service reads aggregate counts through `IDbContextFactory<WechatRobotDbContext>`, probes infrastructure components independently, and creates an isolated dependency-injection scope for each enabled robot before calling the existing WorkTool probe/callback service. The Minimal API handler only authorizes and returns the typed snapshot. The Vue page calls that single endpoint and renders independently degradable sections with an explicit check time.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, EF Core, xUnit integration tests, Vue 3, TypeScript, Element Plus, Vitest.

## Global Constraints

- WorkTool official contracts remain the only authority for external robot, online, and callback state.
- Robot identifiers, callback secrets, provider credentials, payload JSON, and dead-letter details must never enter the response.
- “Configured”, “reachable”, and “online” are different states; unknown online state is not counted as online.
- A failed robot or component probe must not erase database counts or successful probes.
- Required readiness failure must remain visible as `failed`; the dashboard aggregate itself returns HTTP 200 when database aggregation succeeds.
- Every dashboard response includes `checkedAtUtc`; each component result retains its own sanitized failure detail.
- Human-handoff counts are excluded.
- Automated tests must not call real WorkTool, Qdrant, OCR, OSS, MySQL, or Enterprise WeChat services.

---

### Task 1: Dashboard Aggregate Contract and Query

**Files:**
- Create: `src/server/WechatRobot.Api/Dashboard/DashboardSummaryService.cs`
- Create: `src/server/WechatRobot.Api/Dashboard/DashboardEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Program.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Operations/DashboardSummaryEndpointTests.cs`

**Interfaces:**
- Consumes: `IDbContextFactory<WechatRobotDbContext>`, `IEnumerable<IComponentHealthProbe>`, `IServiceScopeFactory`, `RobotCallbackConfigurationService`, `TimeProvider`, and `IConfiguration`.
- Produces: `GET /api/admin/dashboard/summary` returning `DashboardSummaryResponse`.

- [ ] **Step 1: Write the failing authorization and aggregate-shape tests**

Create integration tests that:

```csharp
Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
Assert.Equal(2, payload.Robots.Total);
Assert.Equal(1, payload.Robots.Enabled);
Assert.Equal(1, payload.Knowledge.Documents);
Assert.Equal(2, payload.Knowledge.Versions);
Assert.Equal(1, payload.Knowledge.PendingCandidates);
Assert.Equal(1, payload.Knowledge.FailedTasks);
Assert.Equal(1, payload.Operations.DeadLetters);
Assert.Equal(2, payload.Operations.DurableJobs["pending"]);
Assert.Equal(1, payload.Operations.SendCommands["retrying"]);
Assert.DoesNotContain("handoff", json, StringComparison.OrdinalIgnoreCase);
Assert.DoesNotContain("PayloadJson", json, StringComparison.OrdinalIgnoreCase);
```

Seed literal database rows for every asserted count. Replace health and WorkTool dependencies with deterministic fakes.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests\server\WechatRobot.IntegrationTests\WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class WechatRobot.IntegrationTests.Operations.DashboardSummaryEndpointTests
```

Expected: FAIL because `/api/admin/dashboard/summary` is not mapped.

- [ ] **Step 3: Implement the typed aggregate query and endpoint**

Define response records for:

```csharp
DashboardSummaryResponse(
    DateTime CheckedAtUtc,
    RobotSummaryResponse Robots,
    KnowledgeSummaryResponse Knowledge,
    OperationsSummaryResponse Operations,
    ReadinessSummaryResponse Readiness);
```

Database semantics:

- `Robots.Total`: all `RobotConfigs`.
- `Robots.Enabled`: `RobotConfigs.IsEnabled`.
- `Knowledge.Documents`: all non-delete-requested `KnowledgeDocuments`.
- `Knowledge.Versions`: all `KnowledgeDocumentVersions`.
- `Knowledge.PendingCandidates`: `KnowledgeCandidates.Status == "pending"`.
- `Knowledge.FailedTasks`: failed document versions plus failed knowledge index jobs.
- Queue dictionaries: group all persisted `Status` values, normalize keys with `StringComparer.OrdinalIgnoreCase`, and return zero-free dictionaries.
- `Operations.DeadLetters`: total `DeadLetters`.

Map the endpoint with `RequireAuthorization(SystemRoles.Admin)` and `RateLimitPolicies.Ordinary`, then register the service and endpoint in `Program.cs`.

- [ ] **Step 4: Run the focused test and verify GREEN**

Run the command from Step 2.

Expected: PASS.

### Task 2: Independent WorkTool and Readiness Probe Degradation

**Files:**
- Modify: `src/server/WechatRobot.Api/Dashboard/DashboardSummaryService.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Operations/DashboardSummaryEndpointTests.cs`

**Interfaces:**
- Consumes: enabled robot IDs and existing `RobotCallbackConfigurationService.ProbeAsync` / `GetStatusAsync`.
- Produces: robot counts for reachable, online, message callback configured, command-result callback configured, and failed checks; readiness status plus component results.

- [ ] **Step 1: Write failing partial-failure tests**

Use two enabled robots: one deterministic healthy robot and one fake that throws. Assert:

```csharp
Assert.Equal(2, payload.Robots.Enabled);
Assert.Equal(1, payload.Robots.Reachable);
Assert.Equal(1, payload.Robots.Online);
Assert.Equal(1, payload.Robots.MessageCallbackConfigured);
Assert.Equal(1, payload.Robots.CommandResultCallbackConfigured);
Assert.Equal(1, payload.Robots.FailedChecks);
Assert.Equal("failed", payload.Readiness.Status);
Assert.Contains(payload.Readiness.Components,
    component => component.Name == "Qdrant" && component.Status == "failed");
Assert.Equal(1, payload.Operations.DeadLetters);
```

The test catches a probe exception incorrectly collapsing the whole dashboard or a required component failure being mislabeled healthy.

- [ ] **Step 2: Run the focused test and verify RED**

Run the Task 1 focused command.

Expected: FAIL on robot/readiness status assertions.

- [ ] **Step 3: Implement isolated, bounded probes**

- Probe health components independently using one linked deadline from `Health:ProbeTimeoutMilliseconds`.
- Compute readiness as `failed` when any required component failed, `degraded` when only optional components failed, otherwise `healthy`.
- For each enabled robot, create an isolated DI scope, apply `Dashboard:RobotProbeTimeoutMilliseconds` with a default of `3000` and range `100..10000`, and catch timeout/provider failure into `FailedChecks`.
- Do not copy exception messages to the response.
- Preserve successful robot and component results when another probe fails.

- [ ] **Step 4: Run the focused test and verify GREEN**

Run the Task 1 focused command.

Expected: PASS with no external calls.

### Task 3: Frontend Dashboard API and Rendering

**Files:**
- Create: `src/web/wechatrobot-admin/src/api/dashboard.ts`
- Create: `src/web/wechatrobot-admin/src/api/dashboard.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/DashboardView.vue`
- Create: `src/web/wechatrobot-admin/src/views/DashboardView.spec.ts`

**Interfaces:**
- Consumes: `GET /api/admin/dashboard/summary`.
- Produces: `dashboardApi.getSummary(): Promise<DashboardSummary>` and a loading/degraded/empty-capable dashboard page.

- [ ] **Step 1: Write the failing API-client test**

Assert that `dashboardApi.getSummary()` calls exactly:

```typescript
apiClient.get('/api/admin/dashboard/summary')
```

Run:

```powershell
npm test -- --run src/api/dashboard.spec.ts
```

Expected: FAIL because `dashboard.ts` does not exist.

- [ ] **Step 2: Implement the typed dashboard client and verify GREEN**

Add TypeScript interfaces matching the server response and return `response.data`.

Run the Step 1 command.

Expected: PASS.

- [ ] **Step 3: Write failing dashboard view tests**

Mount with an injected fake API and assert:

- loading skeleton before resolution;
- robot, knowledge, queue, dead-letter, and readiness values render after success;
- `checkedAtUtc` is displayed in Beijing time;
- a failed required component displays its sanitized detail;
- one failed section does not hide successful database counts;
- request failure displays a retry action.

Run:

```powershell
npm test -- --run src/views/DashboardView.spec.ts
```

Expected: FAIL because the current page is static.

- [ ] **Step 4: Implement the dashboard page and verify GREEN**

Use compact metric cards, queue status rows, component status tags, `ElSkeleton`, `ElAlert`, and a refresh/retry button. Do not add handoff metrics or invent zero values while loading.

Run the Step 3 command.

Expected: PASS.

### Task 4: Phase Acceptance and Roadmap State

**Files:**
- Modify: `docs/superpowers/plans/2026-07-24-frontend-backend-alignment-roadmap.md`
- Modify: `docs/superpowers/plans/2026-07-25-dashboard-operational-summary.md`

**Interfaces:**
- Consumes: all Phase 4 implementation and tests.
- Produces: verified Phase 4 completion evidence and the roadmap transition to Phase 5.

- [ ] **Step 1: Run server verification**

```powershell
dotnet test tests\server\WechatRobot.IntegrationTests\WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class WechatRobot.IntegrationTests.Operations.DashboardSummaryEndpointTests
dotnet test WechatRobot.slnx --no-restore
```

Expected: all discovered tests pass.

- [ ] **Step 2: Run frontend verification**

```powershell
Set-Location src\web\wechatrobot-admin
npm run typecheck
npm test -- --run
npm run build
```

Expected: all commands exit `0`.

- [ ] **Step 3: Review security and worktree scope**

Run:

```powershell
rg -n "WorkToolRobotId|CallbackSecret|PayloadJson|EvidenceJson" src\server\WechatRobot.Api\Dashboard src\web\wechatrobot-admin\src\api\dashboard.ts src\web\wechatrobot-admin\src\views\DashboardView.vue
git diff --check
git status --short
```

Expected: no secret-bearing response fields, no whitespace errors, and unrelated pre-existing migration-test changes remain untouched.

- [ ] **Step 4: Record completion**

Mark all checkboxes complete only after their evidence passes. Set Phase 4 to `Completed` and Phase 5 to `InProgress` when its detailed plan begins.
