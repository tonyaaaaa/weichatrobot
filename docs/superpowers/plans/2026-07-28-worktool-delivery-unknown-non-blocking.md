# WorkTool Non-Blocking Delivery-Unknown Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep uncertain WorkTool deliveries quarantined without retrying them or blocking later robot replies, and add a safe administrator send-queue page.

**Architecture:** Treat `deliveryUnknown` as a non-blocking quarantined terminal state in the existing MySQL lease query. Add an Admin-only Minimal API slice that reads bounded, redacted queue projections and performs optimistic-concurrency state transitions with administration audits. Add a Vue/Element Plus page backed by a dedicated TypeScript API module.

**Tech Stack:** ASP.NET Core 10 Minimal APIs, EF Core with MySQL 5.7, xUnit integration tests, Vue 3 Composition API, TypeScript, Element Plus, Vitest.

## Global Constraints

- `deliveryUnknown` is never automatically retried.
- `deliveryUnknown` does not block later send commands for the same robot.
- `pending`, `retrying`, `leased`, `dispatching`, and `blocked` continue to enforce FIFO.
- Queue APIs and UI never expose message text, robot identifiers, WorkTool message IDs, raw responses, credentials, or payload JSON.
- Only `pending` and `retrying` may become `cancelled`.
- Only `deliveryUnknown` may become `deliveryUnknownResolved`.
- `leased` and `dispatching` remain read-only.
- Every mutation requires `expectedVersion`, returns `409` on stale state, and writes an administration audit.
- No schema migration or new dependency.
- Existing user changes remain untouched; no commit is created without explicit user authorization.

---

### Task 1: Make delivery-unknown non-blocking

**Files:**
- Modify: `tests/server/WechatRobot.IntegrationTests/Messaging/DurableRobotCoordinationTests.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs`

**Interfaces:**
- Consumes: `IDurableJobRepository.LeaseNextSendCommandAsync(...)` and `MarkSendDeliveryUnknownAsync(...)`.
- Produces: unchanged public interfaces; only the FIFO barrier semantics change.

- [ ] **Step 1: Write the failing MySQL integration test**

Add a test that seeds two commands for one enabled robot, leases the first, marks it dispatching and then `deliveryUnknown`, and asserts the second command can be leased while the first remains unchanged:

```csharp
[Fact]
public async Task Delivery_unknown_is_quarantined_without_blocking_the_next_command()
{
    using var provider = CreateProvider();
    var now = TruncateToMicroseconds(DateTime.UtcNow.AddMinutes(1));
    var robot = await SeedRobotAndCommandsAsync(provider, 50, now, 2);
    await using var scope = provider.CreateAsyncScope();
    var repository = scope.ServiceProvider.GetRequiredService<IDurableJobRepository>();

    var first = Assert.IsType<LeasedSendCommand>(
        await repository.LeaseNextSendCommandAsync(
            "unknown-worker", now, TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken));
    Assert.True(await repository.MarkSendDispatchingAsync(
        first, now, TestContext.Current.CancellationToken));
    await repository.MarkSendDeliveryUnknownAsync(
        first, "delivery_outcome_unknown", now,
        TestContext.Current.CancellationToken);

    var second = Assert.IsType<LeasedSendCommand>(
        await repository.LeaseNextSendCommandAsync(
            "next-worker", now.AddSeconds(1), TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken));

    Assert.NotEqual(first.Id, second.Id);
    await using var verifyScope = provider.CreateAsyncScope();
    var database = verifyScope.ServiceProvider
        .GetRequiredService<WechatRobotDbContext>();
    var quarantined = await database.SendCommands.AsNoTracking()
        .SingleAsync(command => command.Id == first.Id,
            TestContext.Current.CancellationToken);
    Assert.Equal(WorkToolCommandStatuses.DeliveryUnknown, quarantined.Status);
    Assert.Equal(0, quarantined.AttemptCount);
}
```

- [ ] **Step 2: Run the regression test and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~Delivery_unknown_is_quarantined_without_blocking_the_next_command"
```

Expected: FAIL because the second lease is `null`.

- [ ] **Step 3: Apply the minimal lease-query change**

In the `!database.SendCommands.Any(earlier => ...)` FIFO predicate, remove only:

```csharp
earlier.Status == WorkToolCommandStatuses.DeliveryUnknown
```

Keep `pending`, `retrying`, `leased`, `dispatching`, and `blocked` unchanged.

- [ ] **Step 4: Run the focused messaging tests and verify GREEN**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~DurableRobotCoordinationTests"
```

Expected: all `DurableRobotCoordinationTests` pass.

---

### Task 2: Add bounded send-queue administration APIs

**Files:**
- Modify: `src/server/WechatRobot.Application/WorkTool/WorkToolCommandStatuses.cs`
- Create: `src/server/WechatRobot.Api/Operations/SendCommandOperationsEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Program.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Operations/SendCommandOperationsEndpointTests.cs`

**Interfaces:**
- Produces:
  - `GET /api/admin/operations/send-commands`
  - `POST /api/admin/operations/send-commands/{id:guid}/cancel`
  - `POST /api/admin/operations/send-commands/{id:guid}/acknowledge-unknown`
  - `SendCommandMutationRequest(int ExpectedVersion)`
  - status constants `Cancelled` and `DeliveryUnknownResolved`.

- [ ] **Step 1: Write failing endpoint integration tests**

Cover these observable contracts:

```csharp
[Fact]
public async Task List_requires_admin_and_returns_bounded_redacted_projection();

[Theory]
[InlineData("pending")]
[InlineData("retrying")]
public async Task Cancel_transitions_only_unsent_commands_and_writes_audit(
    string sourceStatus);

[Fact]
public async Task Acknowledge_unknown_records_resolution_without_claiming_success();

[Fact]
public async Task Mutation_returns_conflict_when_version_or_status_changed();
```

The list assertion must verify the response contains `id`, `robotName`,
`groupName`, `status`, `attemptCount`, timestamps, `reason`, `version`, and
`messageLength`, and does not contain seeded message text, `payloadJson`,
robot ID, WorkTool message ID, or credentials.

- [ ] **Step 2: Run the endpoint tests and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~SendCommandOperationsEndpointTests"
```

Expected: FAIL with `404 Not Found` because the endpoints are not mapped.

- [ ] **Step 3: Add terminal status constants**

Add:

```csharp
public const string Cancelled = "cancelled";
public const string DeliveryUnknownResolved = "deliveryUnknownResolved";
```

- [ ] **Step 4: Implement the Minimal API slice**

Map an Admin-only route group:

```csharp
var group = endpoints.MapGroup("/api/admin/operations/send-commands")
    .RequireAuthorization(SystemRoles.Admin);
group.MapGet("", ListAsync);
group.MapPost("/{id:guid}/cancel", CancelAsync);
group.MapPost("/{id:guid}/acknowledge-unknown", AcknowledgeUnknownAsync);
```

List validation:

- `page >= 1`
- `pageSize` between `1` and `100`
- optional robot ID, trimmed group query, exact status, `fromUtc <= toUtc`
- database-side filters and ordering by `CreatedAtUtc desc`, then `Id`
- parse only the bounded page payloads to derive group name and message length
- return `total`, `page`, `pageSize`, and `items`

Mutation transaction:

```csharp
var updated = await database.SendCommands
    .Where(command => command.Id == id
        && command.Version == request.ExpectedVersion
        && allowedStatuses.Contains(command.Status))
    .ExecuteUpdateAsync(setters => setters
        .SetProperty(command => command.Status, targetStatus)
        .SetProperty(command => command.CompletedAtUtc, nowUtc)
        .SetProperty(command => command.LeaseOwner, (string?)null)
        .SetProperty(command => command.LeaseExpiresAtUtc, (DateTime?)null)
        .SetProperty(command => command.Version, command => command.Version + 1),
        cancellationToken);
```

If `updated != 1`, return `409 Conflict`. On success, add an
`AdministrationAuditEntity` with action `send-command.cancel` or
`send-command.acknowledge-unknown`; sanitized detail contains only command ID,
source status, target status, and prior version.

- [ ] **Step 5: Map the endpoint and verify GREEN**

Add `app.MapSendCommandOperationsEndpoints();` beside other administrator
operations endpoints in `Program.cs`.

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~SendCommandOperationsEndpointTests|FullyQualifiedName~DashboardSummaryEndpointTests|FullyQualifiedName~DurableRobotCoordinationTests"
```

Expected: all selected tests pass.

---

### Task 3: Add the administrator send-queue page

**Files:**
- Create: `src/web/wechatrobot-admin/src/api/sendCommands.ts`
- Create: `src/web/wechatrobot-admin/src/views/operations/SendQueueView.vue`
- Create: `src/web/wechatrobot-admin/src/views/operations/SendQueueView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/router/index.ts`
- Modify: `src/web/wechatrobot-admin/src/router/index.spec.ts`

**Interfaces:**
- Consumes Task 2 routes and response fields.
- Produces navigation item `send-queue` with label `发送队列`, Admin-only route
  `/operations/send-commands`.

- [ ] **Step 1: Write failing component and router tests**

The component test must assert:

```typescript
expect(wrapper.text()).toContain('发送队列');
expect(wrapper.text()).not.toContain('sensitive message body');
expect(wrapper.get('[data-testid="cancel-command"]').exists()).toBe(true);
expect(wrapper.get('[data-testid="acknowledge-unknown"]').exists()).toBe(true);
expect(wrapper.text()).toContain('正在发送，暂不可操作');
```

Mock `confirmAction` so cancellation returns `false` first and verify no API
mutation occurs; then return `true`, verify `{ expectedVersion }` is submitted,
and verify the page reloads. Add a `409` mock and verify the Chinese conflict
notice plus reload.

Update router tests so Admin sees `发送队列`; other roles do not.

- [ ] **Step 2: Run frontend tests and verify RED**

Run:

```powershell
cd src/web/wechatrobot-admin
npm test -- --run src/views/operations/SendQueueView.spec.ts src/router/index.spec.ts
```

Expected: FAIL because the module, view, and route do not exist.

- [ ] **Step 3: Implement the typed API module**

Define:

```typescript
export interface SendCommandItem {
  id: string;
  robotConfigId: string;
  robotName: string;
  groupName: string;
  status: string;
  attemptCount: number;
  createdAtUtc: string;
  externalDispatchStartedAtUtc?: string;
  completedAtUtc?: string;
  reason?: string;
  version: number;
  messageLength: number;
}
```

Provide `list(params)`, `cancel(id, expectedVersion)`, and
`acknowledgeUnknown(id, expectedVersion)` methods using the Task 2 routes.

- [ ] **Step 4: Implement the Element Plus page**

Use existing `ops-page`, `page-header`, `panel`, `filter-bar`, `table-scroll`,
`row-actions`, and pagination patterns. Provide:

- robot, group, status, and UTC date-range filters
- loading, empty, error, success, and conflict states
- Chinese status labels
- `取消发送` only for `pending/retrying`
- `确认已处理` only for `deliveryUnknown`
- read-only copy for `leased/dispatching`
- shared Element Plus confirmation helper from `src/utils/dialogs.ts`
- no message body column

- [ ] **Step 5: Add route and verify GREEN**

Add:

```typescript
{ name: 'send-queue', label: '发送队列', roles: ['Admin'] }
```

and:

```typescript
{
  path: 'operations/send-commands',
  name: 'send-queue',
  component: () => import('../views/operations/SendQueueView.vue'),
  meta: { roles: ['Admin'] }
}
```

Run:

```powershell
npm test -- --run src/views/operations/SendQueueView.spec.ts src/router/index.spec.ts
npm run typecheck
```

Expected: selected tests and typecheck pass.

---

### Task 4: Cross-boundary verification and safe rollout evidence

**Files:**
- Modify only if verification exposes a defect in the files listed above.

**Interfaces:**
- Verifies Tasks 1–3 as one workflow.

- [ ] **Step 1: Run backend suites**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj
```

- [ ] **Step 2: Run frontend verification**

```powershell
cd src/web/wechatrobot-admin
npm run typecheck
npm test -- --run
npm run build
```

- [ ] **Step 3: Check diff hygiene and secret safety**

```powershell
git diff --check
git diff -- src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs src/server/WechatRobot.Application/WorkTool/WorkToolCommandStatuses.cs src/server/WechatRobot.Api/Operations/SendCommandOperationsEndpoints.cs src/server/WechatRobot.Api/Program.cs tests/server/WechatRobot.IntegrationTests/Messaging/DurableRobotCoordinationTests.cs tests/server/WechatRobot.IntegrationTests/Operations/SendCommandOperationsEndpointTests.cs src/web/wechatrobot-admin/src/api/sendCommands.ts src/web/wechatrobot-admin/src/views/operations/SendQueueView.vue src/web/wechatrobot-admin/src/views/operations/SendQueueView.spec.ts src/web/wechatrobot-admin/src/router/index.ts src/web/wechatrobot-admin/src/router/index.spec.ts
```

Confirm no payload JSON, message text, robot ID, credential, secret, or
WorkTool message ID is present in API responses, UI, logs, or tests.

- [ ] **Step 4: Prepare safe deployment order**

Do not mutate the current database during implementation. For deployment:

1. Stop the production Worker.
2. Deploy and start the API/frontend.
3. Open `发送队列`.
4. Review and cancel stale `pending/retrying` commands individually.
5. Confirm the existing `deliveryUnknown` as handled.
6. Deploy/start the Worker.
7. Send one new group message and verify it reaches a terminal execution state.
