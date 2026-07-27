# Full Robot Administration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the robot administration API and page for safe creation, credential rotation, connection probing, enable/disable, rate limits, message callbacks, command-result callbacks, and callback-state display.

**Architecture:** Extend the existing official-document-backed WorkTool administration endpoints instead of inventing connector capabilities. Robot credentials remain write-only and encrypted. A successful read-only probe issues a short-lived operator-bound enable confirmation token tied to the robot’s current update timestamp; disabled robots cannot be enabled without that token, and credential rotation requires the robot to be disabled first.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, EF Core, AES-GCM secret protection, existing HMAC confirmation service, xUnit integration tests, Vue 3, TypeScript, Element Plus, Vitest.

## Global Constraints

- WorkTool paths, request fields, response codes, online state, and callback behavior must remain traceable to official WorkTool documentation.
- Never return plaintext robot IDs, callback secrets, encrypted blobs, hashes, authorization headers, or callback URLs containing tokens.
- “Configured”, “reachable”, “online”, “message callback configured”, and “command-result callback configured” are separate states.
- Unknown online state remains unknown and must not be rendered as offline or online.
- New robots are created disabled.
- Credential rotation is rejected while a robot is enabled; the operator must disable, rotate, test, then re-enable.
- A disabled robot can be enabled only with a non-expired confirmation from a successful probe of the same current robot version and operator.
- Disabling continues to block queued send commands; enabling releases only commands previously blocked by robot disablement.
- Credential and callback mutations write sanitized administration audits.
- Automated tests use fake WorkTool clients and never call real external services.

---

### Task 1: Safe Robot Mutation and Enable Confirmation Contract

**Files:**
- Modify: `src/server/WechatRobot.Api/WorkTool/WorkToolGroupOperationEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Robots/RobotSettingsEndpoints.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/WorkTool/RobotSettingsEndpointTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/WorkTool/RobotCallbackConfigurationTests.cs`

**Interfaces:**
- Produces: safe robot list metadata including rate limit, update time, and `hasWorkToolRobotId`.
- Produces: probe response with optional `enableConfirmationToken` and `enableConfirmationExpiresAtUtc`.
- Consumes: update request with optional write-only `workToolRobotId`, rate limit, and optional enable confirmation token.

- [ ] **Step 1: Write failing integration tests**

Cover:

```csharp
Assert.DoesNotContain(plaintextRobotId, listJson, StringComparison.Ordinal);
Assert.True(robot.HasWorkToolRobotId);
Assert.Equal(40, robot.SendRateLimitPerMinute);
Assert.Equal(HttpStatusCode.Conflict, rotateWhileEnabled.StatusCode);
Assert.Equal(HttpStatusCode.Conflict, enableWithoutProbe.StatusCode);
Assert.False(created.IsEnabled);
Assert.NotNull(successfulProbe.EnableConfirmationToken);
Assert.True(enableWithMatchingToken.IsSuccessStatusCode);
Assert.Equal(HttpStatusCode.Conflict, enableWithStaleToken.StatusCode);
```

Also assert sanitized `AdministrationAudits` contain no plaintext robot ID.

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test tests\server\WechatRobot.IntegrationTests\WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class WechatRobot.IntegrationTests.WorkTool.RobotSettingsEndpointTests --filter-class WechatRobot.IntegrationTests.WorkTool.RobotCallbackConfigurationTests
```

Expected: FAIL on missing safe metadata and enable-confirmation behavior.

- [ ] **Step 3: Implement minimal backend behavior**

- Make credential input optional for existing robots and mandatory for new robots.
- Reject new robots requested as enabled.
- Reject credential replacement while enabled.
- Validate rate limit `1..60`.
- Issue an HMAC confirmation for canonical JSON containing `robotId` and current `updatedAtUtc`.
- Validate the token only on `false -> true`.
- Preserve existing send-command blocking/unblocking semantics.
- Write audit fields only for changed field names, booleans, rate values, and credential `configured/rotated` state.
- Keep the legacy `/api/admin/robots` endpoint compatible but apply the same enable-confirmation requirement so it cannot bypass governance.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the Step 2 command.

Expected: PASS.

### Task 2: Typed Frontend Robot API

**Files:**
- Modify: `src/web/wechatrobot-admin/src/api/robots.ts`
- Create: `src/web/wechatrobot-admin/src/api/robots.spec.ts`

**Interfaces:**
- Produces: list, create/update, probe, configure message callback, configure command-result callback, and query callback state methods.

- [ ] **Step 1: Write failing API-client tests**

Assert exact calls to:

```text
GET  /api/admin/worktool/robots
PUT  /api/admin/worktool/robots/{id}
POST /api/admin/worktool/robots/{id}/test-connection
POST /api/admin/worktool/robots/{id}/message-callback/configure
POST /api/admin/worktool/robots/{id}/command-result-callback/configure
GET  /api/admin/worktool/robots/{id}/callbacks
```

Verify IDs use `encodeURIComponent`, credential fields appear only in mutation requests, and no response interface exposes plaintext credentials.

- [ ] **Step 2: Run and verify RED**

```powershell
npm test -- --run src/api/robots.spec.ts
```

Expected: FAIL because the current client only supports legacy list/save.

- [ ] **Step 3: Implement the typed client and verify GREEN**

Run the Step 2 command.

Expected: PASS.

### Task 3: Full Robot Administration Page

**Files:**
- Modify: `src/web/wechatrobot-admin/src/views/settings/RobotSettingsView.vue`
- Create: `src/web/wechatrobot-admin/src/views/settings/RobotSettingsView.spec.ts`

**Interfaces:**
- Consumes: the Task 2 `RobotApi`.
- Produces: safe robot create/edit/rotate/probe/callback workflows.

- [ ] **Step 1: Write failing view tests**

Cover:

- empty state retains “新增机器人”;
- list displays configured metadata but never a robot ID;
- create form requires name and robot ID and creates disabled;
- rotation warning requires an enabled robot to be disabled first;
- successful connection test separately shows reachable and online/unknown;
- enable control remains unavailable until the current successful probe returns a confirmation token;
- message and command-result callbacks are separate buttons;
- callback-state query shows checked time and `replyAll`;
- public base URL defaults to `window.location.origin` and requires HTTPS outside local development;
- callback/provider failures remain visible without clearing other robot cards.

- [ ] **Step 2: Run and verify RED**

```powershell
npm test -- --run src/views/settings/RobotSettingsView.spec.ts
```

Expected: FAIL because the current page only edits basic settings.

- [ ] **Step 3: Implement the page and verify GREEN**

Use per-card busy states, explicit confirmation copy, `ElAlert`, `ElTag`, and write-only password inputs with autocomplete disabled. Never populate a credential field from a response.

Run the Step 2 command.

Expected: PASS.

### Task 4: Phase Acceptance

**Files:**
- Modify: `docs/superpowers/plans/2026-07-24-frontend-backend-alignment-roadmap.md`
- Modify: `docs/superpowers/plans/2026-07-25-full-robot-administration.md`

- [ ] **Step 1: Run focused server and frontend tests**

Run the Task 1 and Task 2/3 commands.

- [ ] **Step 2: Run frontend verification**

```powershell
Set-Location src\web\wechatrobot-admin
npm run typecheck
npm test -- --run
npm run build
```

- [ ] **Step 3: Run server build and safe full tests**

```powershell
dotnet build WechatRobot.slnx --no-restore
dotnet test WechatRobot.slnx --no-build --no-restore
```

If another known integration host locks `WechatRobot.IntegrationTests.exe` or the historical Testcontainers combination does not terminate, record the exact process/timeout evidence and do not claim the full integration suite passed. Phase-focused tests must still pass.

- [ ] **Step 4: Security review and roadmap update**

```powershell
rg -n "WorkToolRobotId|CallbackSecret|EncryptedWorkToolRobotId|EncryptedCallbackSecret" src\web\wechatrobot-admin src\server\WechatRobot.Api\WorkTool
git diff --check
```

Confirm every match is write-only input or internal persistence use, update Phase 5 state, and preserve unrelated parallel worktree modifications.

## Completion Evidence — 2026-07-25

- Robot settings and callback configuration integration tests: 6 passed, 0 failed.
- Frontend full suite: 92 passed across 26 files, 0 failed.
- Frontend typecheck: passed.
- Frontend production build: passed.
- `git diff --check`: passed; only existing line-ending conversion warnings were reported.
- Full solution/integration combination is not declared passed: another integration host repeatedly locked `WechatRobot.IntegrationTests.exe`, and the historical Testcontainers combination again did not terminate inside the bounded verification window.
- No real WorkTool mutation was executed during automated verification.
