# WorkTool Group Import and Agent Nickname Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let administrators discover and selectively import WorkTool groups, bind one company-unique WorkTool nickname to each eligible backend user, and prepare group-level agent configuration without inventing stable WorkTool identities.

**Architecture:** Treat the deprecated WorkTool group-list API as a replaceable read/import adapter, keep `GroupProfile.Id` as the local identity, and enforce exact nickname uniqueness in ASP.NET Identity plus MySQL. Build the group-agent table and disabled UI now, but do not enable membership verification until the separate `type=512` evidence plan succeeds.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, EF Core with MySQL 5.7, ASP.NET Core Identity, WorkTool HTTP API, Vue 3, Element Plus, TypeScript, Vitest, xUnit v3.

## Global Constraints

- Plan `2026-07-27-worktool-official-contract-hardening.md` must be complete first.
- WorkTool group list is marked “将废弃”; failure never deletes or disables local groups.
- Do not expose WorkTool `robotId` to the frontend.
- Do not claim WorkTool group name is a stable `chat_id`.
- Import only administrator-selected rows; never silently import all remote groups.
- Compare normalized imported names by trimming only; preserve case, full-width characters, and internal spaces.
- A WorkTool nickname is company-wide unique and uses the database binary collation semantics.
- Never copy `ApplicationUser.DisplayName` into `WorkToolDisplayName` automatically.
- Group-agent configuration remains disabled until a verified member snapshot exists.
- Preserve unrelated dirty-worktree changes.

---

### Task 1: Add Import, Nickname, and Group-Agent Persistence

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/Identity/ApplicationUser.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/GroupProfileEntity.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Entities/GroupHumanAgentEntity.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/WechatRobotDbContext.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Configurations/ApplicationUserConfiguration.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Configurations/MessagingConfigurations.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Configurations/GroupHumanAgentConfiguration.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/WechatRobotDbContextModelSnapshot.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/WorkTool/WorkToolGroupImportMigrationTests.cs`

**Interfaces:**
- Produces:

```csharp
ApplicationUser.WorkToolDisplayName
ApplicationUser.WorkToolDisplayNameUpdatedAtUtc
GroupProfileEntity.RegistrationSource
GroupProfileEntity.WorkToolImportedAtUtc
GroupProfileEntity.WorkToolLastSeenAtUtc
DbSet<GroupHumanAgentEntity>
```

- [ ] **Step 1: Write the failing migration test**

Assert the migrated schema contains:

```csharp
Assert.True(await ColumnExistsAsync(database, "AspNetUsers", "WorkToolDisplayName"));
Assert.True(await ColumnExistsAsync(database, "group_profile", "RegistrationSource"));
Assert.True(await TableExistsAsync(database, "group_human_agent"));
Assert.True(await UniqueIndexExistsAsync(database, "AspNetUsers", "WorkToolDisplayName"));
```

- [ ] **Step 2: Run and verify RED**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~WorkToolGroupImportMigrationTests" --verbosity minimal
```

Expected: fields and table do not exist.

- [ ] **Step 3: Add exact entity fields**

```csharp
public string? WorkToolDisplayName { get; set; }
public DateTime? WorkToolDisplayNameUpdatedAtUtc { get; set; }
```

```csharp
public string RegistrationSource { get; set; } = "Manual";
public DateTime? WorkToolImportedAtUtc { get; set; }
public DateTime? WorkToolLastSeenAtUtc { get; set; }
```

Create:

```csharp
public sealed class GroupHumanAgentEntity
{
    public Guid GroupProfileId { get; set; }
    public Guid ApplicationUserId { get; set; }
    public string WorkToolDisplayNameSnapshot { get; set; } = string.Empty;
    public DateTime? LastVerifiedAtUtc { get; set; }
    public string VerificationStatus { get; set; } = "Stale";
    public bool IsDefault { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
```

- [ ] **Step 4: Configure exact constraints**

Configure `WorkToolDisplayName` max length 128 with a unique index. Configure the group-agent composite key, FKs, one-default-per-group computed index compatible with MySQL 5.7, and status check values `Verified`, `Missing`, `Conflict`, `Stale`.

Use a nullable computed column for the single default:

```csharp
builder.Property<Guid?>("DefaultGroupProfileId")
    .HasComputedColumnSql("CASE WHEN `IsDefault` = 1 AND `IsEnabled` = 1 THEN `GroupProfileId` ELSE NULL END", stored: true);
builder.HasIndex("DefaultGroupProfileId").IsUnique();
```

- [ ] **Step 5: Generate and inspect the migration**

```powershell
dotnet ef migrations add AddWorkToolGroupImportAndAgentNickname --project src/server/WechatRobot.Infrastructure --startup-project src/server/WechatRobot.Api
```

Confirm existing rows receive `RegistrationSource='Manual'` and users remain unbound.

- [ ] **Step 6: Rerun migration tests**

Run Step 2. Expected: PASS on MySQL 5.7.

- [ ] **Step 7: Commit persistence**

```powershell
git add src/server/WechatRobot.Infrastructure/Identity/ApplicationUser.cs src/server/WechatRobot.Infrastructure/Persistence/Entities/GroupProfileEntity.cs src/server/WechatRobot.Infrastructure/Persistence/Entities/GroupHumanAgentEntity.cs src/server/WechatRobot.Infrastructure/Persistence/WechatRobotDbContext.cs src/server/WechatRobot.Infrastructure/Persistence/Configurations src/server/WechatRobot.Infrastructure/Persistence/Migrations tests/server/WechatRobot.IntegrationTests/WorkTool/WorkToolGroupImportMigrationTests.cs
git commit -m "feat: persist WorkTool group imports and agent nicknames"
```

### Task 2: Implement the Official WorkTool Group-List Contract

**Files:**
- Modify: `src/server/WechatRobot.Application/WorkTool/IWorkToolClient.cs`
- Modify: `src/server/WechatRobot.Infrastructure/WorkTool/WorkToolClient.cs`
- Create: `tests/server/WechatRobot.ContractTests/WorkTool/GroupListContractTests.cs`

**Interfaces:**
- Produces:

```csharp
Task<WorkToolGroupPage> ListGroupsAsync(
    Guid robotConfigId,
    string? groupName,
    int page,
    int pageSize,
    CancellationToken cancellationToken);

public sealed record WorkToolGroupPage(
    int PageNumber,
    int PageSize,
    int TotalPages,
    int Total,
    IReadOnlyList<WorkToolGroupSummary> Items);

public sealed record WorkToolGroupSummary(
    string GroupName,
    string? MasterName,
    int MembersCount,
    string? GroupAnnouncement,
    DateTime? CreatedAt,
    DateTime? UpdatedAt);
```

- [ ] **Step 1: Write the official sample contract test**

Use the documented response containing `pageNum`, `pageSize`, `totalPage`, `total`, and list fields. Assert:

```csharp
Assert.Equal(
    "/robot/wework/group/list?robotId=robot-7&groupName=Support&page=1&size=50",
    handler.RequestUri!.PathAndQuery);
Assert.Equal("Support", Assert.Single(result.Items).GroupName);
Assert.Equal(12, result.Items[0].MembersCount);
```

Also assert the returned DTO has no `RobotId` property.

- [ ] **Step 2: Run and verify RED**

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore --filter "FullyQualifiedName~GroupListContractTests" --verbosity minimal
```

Expected: `ListGroupsAsync` is missing.

- [ ] **Step 3: Implement the GET mapping**

Call:

```text
robot/wework/group/list?robotId={escaped}&groupName={escaped-or-empty}&page={page}&size={pageSize}
```

Accept only HTTP 2xx, `code=0`, non-null `data`, and bounded pagination. Map `membersNum` to `MembersCount`; do not expose `robotId`, `parentId`, or unused raw fields.

- [ ] **Step 4: Add invalid-response and pagination tests**

Cover nonzero code, missing data, null list, negative totals, and page sizes above 100. Return safe `WorkToolGroupListException` codes without response messages.

- [ ] **Step 5: Run all WorkTool contract tests**

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore --filter "FullyQualifiedName~WorkTool" --verbosity minimal
```

- [ ] **Step 6: Commit the client**

```powershell
git add src/server/WechatRobot.Application/WorkTool/IWorkToolClient.cs src/server/WechatRobot.Infrastructure/WorkTool/WorkToolClient.cs tests/server/WechatRobot.ContractTests/WorkTool/GroupListContractTests.cs
git commit -m "feat: read WorkTool remote groups"
```

### Task 3: Build Selective and Idempotent Group Import

**Files:**
- Create: `src/server/WechatRobot.Infrastructure/WorkTool/WorkToolGroupImportService.cs`
- Create: `src/server/WechatRobot.Api/WorkTool/WorkToolGroupImportEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Program.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/WorkTool/WorkToolGroupImportServiceTests.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/WorkTool/WorkToolGroupImportEndpointTests.cs`

**Interfaces:**
- Produces:

```csharp
Task<RemoteGroupPage> DiscoverAsync(Guid robotConfigId, string? query, int page, int pageSize, CancellationToken token);
Task<IReadOnlyList<GroupImportResult>> ImportAsync(Guid robotConfigId, IReadOnlyList<GroupImportSelection> groups, string actor, CancellationToken token);
```

- [ ] **Step 1: Write failing discovery-state tests**

Seed one local group and fake three remote summaries. Assert:

```csharp
Assert.Equal("Imported", page.Items.Single(x => x.GroupName == "Existing").ImportState);
Assert.Equal("Available", page.Items.Single(x => x.GroupName == "New").ImportState);
Assert.Equal("Conflict", page.Items.Single(x => x.GroupName == "Duplicate").ImportState);
```

Conflict means more than one local candidate matches the same trimmed exact name for that robot.

- [ ] **Step 2: Write failing import tests**

Cover:

- one selected available group creates one `GroupProfile`;
- rerunning the same selection returns the existing ID;
- an unselected remote group is not imported;
- one conflict does not roll back another valid item;
- WorkTool failure leaves all local groups unchanged;
- audit contains local IDs and names but no WorkTool `robotId`.

- [ ] **Step 3: Run and verify RED**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~WorkToolGroupImport" --verbosity minimal
```

- [ ] **Step 4: Implement discovery and per-item transactions**

For a created row set:

```csharp
RegistrationSource = "WorkToolImport";
WorkToolImportedAtUtc = now;
WorkToolLastSeenAtUtc = now;
IsEnabled = true;
```

Use `(RobotConfigId, Name)` exact matching after trim only. Requery the remote page before import and reject a name absent from the current WorkTool result.

- [ ] **Step 5: Map authenticated admin endpoints**

Routes:

```csharp
GET  /api/admin/worktool/robots/{robotConfigId}/groups
POST /api/admin/worktool/robots/{robotConfigId}/groups/import
```

Return remote DTOs without `robotId`. Use 502 for safe upstream failure, 409 per conflict item, and 200 for mixed batch results.

- [ ] **Step 6: Verify endpoints and authorization**

Run Step 3 and assert anonymous 401, HumanAgent 403, Admin success.

- [ ] **Step 7: Commit group import backend**

```powershell
git add src/server/WechatRobot.Infrastructure/WorkTool/WorkToolGroupImportService.cs src/server/WechatRobot.Api/WorkTool/WorkToolGroupImportEndpoints.cs src/server/WechatRobot.Api/Program.cs tests/server/WechatRobot.IntegrationTests/WorkTool/WorkToolGroupImportServiceTests.cs tests/server/WechatRobot.IntegrationTests/WorkTool/WorkToolGroupImportEndpointTests.cs
git commit -m "feat: selectively import WorkTool groups"
```

### Task 4: Bind a Company-Unique WorkTool Nickname to Users

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/Identity/UserAdministrationService.cs`
- Modify: `src/server/WechatRobot.Api/Users/UserAdministrationEndpoints.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Identity/UserAdministrationServiceTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Identity/UserAdministrationEndpointTests.cs`

**Interfaces:**
- Changes `ManagedUser` to include `string? WorkToolDisplayName`
- Produces:

```csharp
Task<ManagedUser> SetWorkToolDisplayNameAsync(string actor, Guid userId, string displayName, CancellationToken token);
Task<ManagedUser> ClearWorkToolDisplayNameAsync(string actor, Guid userId, CancellationToken token);
```

- [ ] **Step 1: Write failing service tests**

Cover:

```csharp
Assert.Equal("客服-王小明", bound.WorkToolDisplayName);
await Assert.ThrowsAsync<UserAdministrationException>(() =>
    service.SetWorkToolDisplayNameAsync(actor, secondUserId, "客服-王小明", token));
```

Also cover trim, blank rejection, 129 characters, disabled user, and a user without `Admin` or `HumanAgent`.

- [ ] **Step 2: Run and verify RED**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~UserAdministrationServiceTests" --verbosity minimal
```

- [ ] **Step 3: Implement binding and conflict handling**

Rules:

```csharp
var normalized = request.Trim();
if (normalized.Length is < 1 or > 128) throw new("worktool-display-name-invalid");
if (!user.IsEnabled || !eligibleRole) throw new("worktool-agent-ineligible");
if (await users.Users.AnyAsync(x => x.Id != userId && x.WorkToolDisplayName == normalized, token))
    throw new("worktool-display-name-conflict");
```

Catch the unique-index `DbUpdateException` race and return the same conflict code. When the nickname changes, set all that user’s `GroupHumanAgent.VerificationStatus` to `Stale` and `IsEnabled=false`.

- [ ] **Step 4: Add admin endpoints**

```http
PUT    /api/admin/users/{id}/worktool-display-name
DELETE /api/admin/users/{id}/worktool-display-name
```

Map conflict to HTTP 409, invalid/ineligible to 400, missing user to 404.

- [ ] **Step 5: Verify audit and no implicit binding**

Assert new users have null nickname, list responses expose only the explicit bound nickname, and audit action is `user_worktool_display_name_changed` without credentials.

- [ ] **Step 6: Run identity tests**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~UserAdministration" --verbosity minimal
```

- [ ] **Step 7: Commit nickname backend**

```powershell
git add src/server/WechatRobot.Infrastructure/Identity/UserAdministrationService.cs src/server/WechatRobot.Api/Users/UserAdministrationEndpoints.cs tests/server/WechatRobot.IntegrationTests/Identity/UserAdministrationServiceTests.cs tests/server/WechatRobot.IntegrationTests/Identity/UserAdministrationEndpointTests.cs
git commit -m "feat: bind unique WorkTool agent nicknames"
```

### Task 5: Add Remote Group Selection and Batch Import UI

**Files:**
- Modify: `src/web/wechatrobot-admin/src/api/worktool.ts`
- Modify: `src/web/wechatrobot-admin/src/api/worktool.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupListView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupListView.spec.ts`

**Interfaces:**
- Produces `RemoteWorkToolGroup`, `RemoteWorkToolGroupPage`, and `importGroups(robotId, names)`

- [ ] **Step 1: Write failing component tests**

Assert:

- robot selector loads and contains readable names;
- “读取远程群” calls the selected robot route;
- checkboxes exist only for `Available`;
- `Imported` and `Conflict` rows are disabled;
- clicking “导入所选群” sends only checked names;
- no GUID input exists;
- deprecation notice is visible.

Example:

```ts
expect(wrapper.text()).toContain('WorkTool 已将该群列表接口标记为将废弃');
expect(wrapper.find('input[placeholder*=\"机器人配置 ID\"]').exists()).toBe(false);
```

- [ ] **Step 2: Run and verify RED**

```powershell
Set-Location src/web/wechatrobot-admin
npm test -- src/api/worktool.spec.ts src/views/groups/GroupListView.spec.ts
Set-Location ../../..
```

- [ ] **Step 3: Add API types and methods**

Use:

```ts
listRemoteGroups(robotId: string, params: { query?: string; page: number; pageSize: number }): Promise<RemoteWorkToolGroupPage>;
importRemoteGroups(robotId: string, groups: { groupName: string; expectedImportState: 'Available' }[]): Promise<GroupImportResult[]>;
```

- [ ] **Step 4: Build the two-section layout**

Keep “已登记群” and add “从 WorkTool 导入” above it with an Element Plus robot select, search, table selection, status tags, and batch primary button. Refresh both remote and local lists after successful import.

- [ ] **Step 5: Verify responsive and keyboard behavior**

Add assertions for table wrapper overflow, button disabled state, labelled selector, and selection persistence after a failed request.

- [ ] **Step 6: Run typecheck and focused tests**

```powershell
Set-Location src/web/wechatrobot-admin
npm run typecheck
npm test -- src/api/worktool.spec.ts src/views/groups/GroupListView.spec.ts
Set-Location ../../..
```

- [ ] **Step 7: Commit group import UI**

```powershell
git add src/web/wechatrobot-admin/src/api/worktool.ts src/web/wechatrobot-admin/src/api/worktool.spec.ts src/web/wechatrobot-admin/src/views/groups/GroupListView.vue src/web/wechatrobot-admin/src/views/groups/GroupListView.spec.ts
git commit -m "feat: import selected WorkTool groups"
```

### Task 6: Add Nickname Binding UI

**Files:**
- Modify: `src/web/wechatrobot-admin/src/api/users.ts`
- Modify: `src/web/wechatrobot-admin/src/api/users.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/users/UserRolesView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/users/UserRolesView.spec.ts`

**Interfaces:**
- Adds `workToolDisplayName?: string` to `ManagedUser`
- Adds `setWorkToolDisplayName` and `clearWorkToolDisplayName`

- [ ] **Step 1: Write failing UI tests**

Assert:

- eligible users show “绑定昵称”;
- ineligible users show “仅 Admin 或 HumanAgent 可绑定”;
- exact nickname is submitted after trim;
- HTTP 409 displays “该昵称已绑定其他账号”;
- clearing requires confirmation;
- the table never assumes `displayName === workToolDisplayName`.

- [ ] **Step 2: Run and verify RED**

```powershell
Set-Location src/web/wechatrobot-admin
npm test -- src/api/users.spec.ts src/views/users/UserRolesView.spec.ts
Set-Location ../../..
```

- [ ] **Step 3: Implement API methods and dialog**

Use Element Plus dialog/form controls. Include this exact helper text:

```text
必须与企业微信中的公司级唯一显示名完全一致；改名后需要重新绑定并验证。
```

- [ ] **Step 4: Run focused tests and typecheck**

Use Step 2 plus `npm run typecheck`. Expected: PASS.

- [ ] **Step 5: Commit nickname UI**

```powershell
git add src/web/wechatrobot-admin/src/api/users.ts src/web/wechatrobot-admin/src/api/users.spec.ts src/web/wechatrobot-admin/src/views/users/UserRolesView.vue src/web/wechatrobot-admin/src/views/users/UserRolesView.spec.ts
git commit -m "feat: manage WorkTool agent nicknames"
```

### Task 7: Add Disabled Group-Agent Configuration Boundary

**Files:**
- Modify: `src/server/WechatRobot.Api/Groups/GroupEndpoints.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Groups/GroupHumanAgentEndpointTests.cs`
- Modify: `src/web/wechatrobot-admin/src/api/groups.ts`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts`

**Interfaces:**
- Produces:

```http
GET /api/admin/groups/{id}/eligible-human-agents
PUT /api/admin/groups/{id}/human-agents
```

- [ ] **Step 1: Write failing backend boundary tests**

Until Plan C creates a verified snapshot:

```csharp
Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
Assert.Equal(
    "worktool-member-snapshot-unavailable",
    problem.RootElement.GetProperty("error").GetString());
```

Assert no `GroupHumanAgentEntity` is enabled by the failed request.

- [ ] **Step 2: Implement safe disabled endpoints**

`GET` returns bound eligible users with `verificationStatus="Stale"` for display only. `PUT` returns 409 until a verified snapshot provider reports current data.

- [ ] **Step 3: Write and implement disabled frontend state**

Display nickname-bound candidates but disable the selector and save button with:

```text
需要先完成 WorkTool 群成员昵称结果验证，当前不能启用群客服。
```

- [ ] **Step 4: Run backend and frontend tests**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~GroupHumanAgentEndpointTests" --verbosity minimal
Set-Location src/web/wechatrobot-admin
npm test -- src/views/groups/GroupRulesView.spec.ts
npm run typecheck
Set-Location ../../..
```

- [ ] **Step 5: Commit the explicit boundary**

```powershell
git add src/server/WechatRobot.Api/Groups/GroupEndpoints.cs tests/server/WechatRobot.IntegrationTests/Groups/GroupHumanAgentEndpointTests.cs src/web/wechatrobot-admin/src/api/groups.ts src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts
git commit -m "feat: gate group agents on verified members"
```

### Task 8: Reconcile Successful Create and Rename Operations

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/WorkToolOperationAuditEntity.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Configurations/MessagingConfigurations.cs`
- Create: `src/server/WechatRobot.Worker/Jobs/WorkToolGroupReconciliationWorker.cs`
- Modify: `src/server/WechatRobot.Worker/Program.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/WorkTool/WorkToolGroupReconciliationWorkerTests.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/WechatRobotDbContextModelSnapshot.cs`

**Interfaces:**
- Adds `ReconciliationStatus`, `ReconciliationAttemptCount`, `ReconciliationNextAttemptAtUtc`, and optional `ReconciledGroupProfileId`

- [ ] **Step 1: Write failing worker tests**

Cover:

- successful `Create` plus one remote name creates one local imported group;
- zero or multiple matches records `NeedsConfirmation`;
- successful `Rename` plus one remote match updates the local name and increments configuration version;
- failed WorkTool result never reconciles;
- transient group-list failure schedules bounded backoff without changing local names.

- [ ] **Step 2: Run and verify RED**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~WorkToolGroupReconciliationWorkerTests" --verbosity minimal
```

- [ ] **Step 3: Add reconciliation state and migration**

Use statuses:

```text
Pending
Retrying
Reconciled
NeedsConfirmation
Failed
```

Only `executedSucceeded` Create/Rename audits become `Pending`.

- [ ] **Step 4: Implement the worker**

Decrypt the stored command, query the same robot’s remote group list, require exactly one trimmed exact-name match, and call the import service. For rename, identify the existing local group by robot plus original name/remark; never update multiple matches.

- [ ] **Step 5: Run worker and migration tests**

Run Step 2 plus the migration suite. Expected: PASS.

- [ ] **Step 6: Commit reconciliation**

```powershell
git add src/server/WechatRobot.Infrastructure/Persistence/Entities/WorkToolOperationAuditEntity.cs src/server/WechatRobot.Infrastructure/Persistence/Configurations/MessagingConfigurations.cs src/server/WechatRobot.Worker/Jobs/WorkToolGroupReconciliationWorker.cs src/server/WechatRobot.Worker/Program.cs src/server/WechatRobot.Infrastructure/Persistence/Migrations tests/server/WechatRobot.IntegrationTests/WorkTool/WorkToolGroupReconciliationWorkerTests.cs
git commit -m "feat: reconcile WorkTool group changes"
```

### Task 9: Full Verification

**Files:**
- Modify only for in-scope failures

- [ ] **Step 1: Verify no manual internal-ID controls remain in the changed flows**

```powershell
rg -n -S "机器人配置 ID|客服用户 ID|群 ID.*input|WorkToolDisplayName.*DisplayName =" src/web/wechatrobot-admin/src
```

Expected: no editable internal-ID controls and no implicit nickname assignment.

- [ ] **Step 2: Run backend build and tests**

```powershell
dotnet build WechatRobot.slnx --no-restore
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore --verbosity minimal
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore --verbosity minimal
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore --verbosity minimal
```

- [ ] **Step 3: Run frontend verification**

```powershell
Set-Location src/web/wechatrobot-admin
npm run typecheck
npm test
npm run build
Set-Location ../../..
```

- [ ] **Step 4: Verify MySQL migration and preservation**

Apply migrations to the disposable integration database. Confirm pre-existing group rules, tags, sessions, and audits retain the same `GroupProfileId`.

- [ ] **Step 5: Record final evidence**

```powershell
git status --short
git log -10 --oneline
```

Confirm unrelated pre-existing modifications remain untouched.
