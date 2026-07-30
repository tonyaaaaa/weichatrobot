# Memory Job and Group Selector Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Repair the MySQL memory-job query and replace administrator-facing group UUID inputs with a reusable group-name selector.

**Architecture:** A generic authenticated group-options endpoint exposes only local `GroupProfile` display metadata and is consumed by one shared Vue selector. Memory and audit pages retain UUID request contracts but display names. The memory-job query remains database-side and uses explicit equality predicates that MySQL 5.7 can translate.

**Tech Stack:** ASP.NET Core 10 Minimal APIs, EF Core with MySQL, xUnit v3, Vue 3 Composition API, TypeScript, Element Plus, Vitest.

## Global Constraints

- Local `GroupProfile.Id` remains the stable filter value; group names and remarks are display-only.
- Group options come from MySQL, never from the deprecated WorkTool remote group-list API.
- Include enabled, disabled, and archived groups so historical routes remain resolvable.
- KnowledgeOperator pages must not depend on Admin-only `/api/admin/worktool/groups`.
- MySQL filtering and pagination remain database-side.
- Do not expose robot identifiers, callback data, credentials, or secrets.
- Preserve loading, empty, failure, keyboard, and clear-to-all behavior.

---

### Task 1: MySQL-Compatible Memory Job Query

**Files:**
- Create: `tests/server/WechatRobot.IntegrationTests/Memory/MemoryJobMySqlEndpointTests.cs`
- Modify: `src/server/WechatRobot.Api/Memory/MemoryEndpoints.cs:90-114`

**Interfaces:**
- Consumes: `GET /api/admin/memory/jobs?groupProfileId={id}&status={status}&page=1&pageSize=20`
- Produces: the existing paged memory-job response without changing its JSON shape

- [ ] **Step 1: Write the failing MySQL endpoint test**

Create a MySQL-backed `WebApplicationFactory<Program>`, migrate its isolated database, seed one matching memory job and one unrelated job, then assert:

```csharp
using var response = await client.GetAsync(
    $"/api/admin/memory/jobs?groupProfileId={group.Id:D}&page=1&pageSize=20",
    TestContext.Current.CancellationToken);

Assert.Equal(HttpStatusCode.OK, response.StatusCode);
var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
Assert.Equal(1, body.GetProperty("total").GetInt32());
Assert.Equal("ExtractConversationMemory", body.GetProperty("items")[0].GetProperty("jobType").GetString());
```

- [ ] **Step 2: Run the test and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter-class "*MemoryJobMySqlEndpointTests"
```

Expected: FAIL with `Expression '@types' in the SQL tree does not have a type mapping assigned`.

- [ ] **Step 3: Replace local-array Contains with explicit predicates**

Use a database-translatable predicate:

```csharp
.Where(x =>
    x.JobType == "ExtractConversationMemory" ||
    x.JobType == "MaintainLongTermMemory" ||
    x.JobType == "IndexMemoryEntry" ||
    x.JobType == "RemoveMemoryEntryFromIndex")
```

Keep group, status, count, ordering, skip, and take in the same `IQueryable`.

- [ ] **Step 4: Run the focused test and verify GREEN**

Run the command from Step 2.

Expected: PASS with one returned memory job and no unrelated job.

### Task 2: Generic Group Options Contract

**Files:**
- Create: `src/server/WechatRobot.Application/Groups/GroupOption.cs`
- Create: `src/server/WechatRobot.Infrastructure/Groups/GroupOptionQuery.cs`
- Modify: `src/server/WechatRobot.Api/Groups/GroupEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Audit/ConversationAuditEndpoints.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Groups/GroupOptionEndpointTests.cs`

**Interfaces:**
- Produces: `GET /api/group-options`
- Produces: `GroupOption(Guid Id, string Name, string? WorkToolGroupRemark, string RobotName, string State, bool IsEnabled)`
- Preserves: `GET /api/audit/group-options` by delegating to the same query

- [ ] **Step 1: Write failing endpoint tests**

Seed enabled, disabled, and archived groups with robots. Assert authenticated KnowledgeOperator access, stable name ordering, and this safe response:

```json
{
  "id": "group-guid",
  "name": "技术支持群",
  "workToolGroupRemark": "support-east",
  "robotName": "默认机器人",
  "state": "enabled",
  "isEnabled": true
}
```

Also assert an anonymous request receives `401`.

- [ ] **Step 2: Run tests and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter-class "*GroupOptionEndpointTests"
```

Expected: FAIL because `/api/group-options` does not exist.

- [ ] **Step 3: Implement one shared query**

Create:

```csharp
public sealed record GroupOption(
    Guid Id,
    string Name,
    string? WorkToolGroupRemark,
    string RobotName,
    string State,
    bool IsEnabled);
```

`GroupOptionQuery.ListAsync` joins `GroupProfiles` to `RobotConfigs`, orders by name and ID, includes archived groups, and projects only the six fields above.

- [ ] **Step 4: Map and authorize the endpoint**

Map `GET /api/group-options` with authenticated access for Admin and KnowledgeOperator. Change `/api/audit/group-options` to invoke `GroupOptionQuery.ListAsync` so both routes share one source.

- [ ] **Step 5: Run group option tests and related audit tests**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter-class "*GroupOptionEndpointTests" "*ConversationAuditEndpointTests"
```

Expected: PASS.

### Task 3: Shared Vue Group Selector

**Files:**
- Create: `src/web/wechatrobot-admin/src/api/groupOptions.ts`
- Create: `src/web/wechatrobot-admin/src/api/groupOptions.spec.ts`
- Create: `src/web/wechatrobot-admin/src/components/groups/GroupProfileSelect.vue`
- Create: `src/web/wechatrobot-admin/src/components/groups/GroupProfileSelect.spec.ts`

**Interfaces:**
- Produces: `groupOptionApi.list(): Promise<GroupOption[]>`
- Produces: `GroupProfileSelect` with `modelValue: string`, `update:modelValue`, and `load-error`

- [ ] **Step 1: Write failing API and component tests**

Assert the API calls `/api/group-options`. Mount the component with an injected loader and assert:

```typescript
expect(wrapper.text()).toContain('技术支持群（support-east） · 默认机器人');
expect(wrapper.text()).toContain('历史群 · 默认机器人 · 已归档');
await wrapper.find('[data-testid="group-profile-select"]').setValue('group-1');
expect(wrapper.emitted('update:modelValue')?.[0]).toEqual(['group-1']);
```

Cover loading, clear-to-all, unknown selected ID, and load failure.

- [ ] **Step 2: Run tests and verify RED**

Run:

```powershell
npm test -- --run src/api/groupOptions.spec.ts src/components/groups/GroupProfileSelect.spec.ts
```

Expected: FAIL because the API module and component do not exist.

- [ ] **Step 3: Implement the API and selector**

Use Element Plus:

```vue
<ElSelect
  data-testid="group-profile-select"
  :model-value="modelValue"
  filterable
  clearable
  :loading="loading"
  placeholder="全部群"
  @update:model-value="emit('update:modelValue', $event ?? '')"
>
  <ElOption
    v-for="group in options"
    :key="group.id"
    :value="group.id"
    :label="optionLabel(group)"
  />
</ElSelect>
```

For an unknown non-empty value, add a disabled fallback option labelled `群记录不存在或已删除`.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the command from Step 2.

Expected: PASS.

### Task 4: Replace Page-Level Group UUID Inputs

**Files:**
- Modify: `src/web/wechatrobot-admin/src/views/memory/MemoryCenterView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/memory/MemoryCenterView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/audit/ConversationAuditView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/audit/ConversationAuditView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/api/audit.ts`

**Interfaces:**
- Consumes: `GroupProfileSelect`
- Preserves: memory API parameter `groupProfileId`
- Preserves: audit API parameter `groupId`

- [ ] **Step 1: Write failing page tests**

For memory center, assert:

```typescript
expect(wrapper.text()).not.toContain('群 ID');
expect(wrapper.find('[data-testid="group-profile-select"]').exists()).toBe(true);
expect(api.listCandidates).toHaveBeenCalledWith(
  expect.objectContaining({ groupProfileId: 'group-1' })
);
```

For audit, assert the shared component is rendered and selecting a group submits the same ID as `groupId`. Change the failure copy assertion to `审计查询失败，请检查群筛选和时间范围。`.

- [ ] **Step 2: Run page tests and verify RED**

Run:

```powershell
npm test -- --run src/views/memory/MemoryCenterView.spec.ts src/views/audit/ConversationAuditView.spec.ts
```

Expected: FAIL because memory still renders the UUID input and audit still owns a separate options implementation.

- [ ] **Step 3: Integrate the shared component**

Replace the memory `<ElInput>` with:

```vue
<label>群
  <GroupProfileSelect v-model="groupProfileId" @change="filter" @load-error="onGroupLoadError" />
</label>
```

Keep the selected group across tabs and reset only status/page. Replace audit’s local `groupOptions` loading and `ElSelect` with the same component. Remove `AuditApi.groupOptions` after both pages use the generic API.

- [ ] **Step 4: Run focused and complete frontend verification**

Run:

```powershell
npm test -- --run src/api/groupOptions.spec.ts src/components/groups/GroupProfileSelect.spec.ts src/views/memory/MemoryCenterView.spec.ts src/views/audit/ConversationAuditView.spec.ts
npm run typecheck
npm test -- --run
npm run build
```

Expected: all commands PASS.

### Task 5: Runtime Verification

**Files:**
- No product files

**Interfaces:**
- Verifies current local API, Worker, and Vite processes

- [ ] **Step 1: Build the solution**

Run:

```powershell
dotnet build WechatRobot.slnx --no-restore
```

Expected: 0 warnings and 0 errors.

- [ ] **Step 2: Run backend regression suites**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-build --no-restore
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-build --no-restore
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-build --no-restore --filter-class "*MemoryJobMySqlEndpointTests" "*GroupOptionEndpointTests" "*ConversationAuditEndpointTests"
```

Expected: all selected tests PASS.

- [ ] **Step 3: Restart API, Worker, and frontend with `.local`**

Set `WECHATROBOT_ENV_FILE` to the absolute `.local/.env` path. Start API and Worker with `.local` as working directory; start Vite on `127.0.0.1:5173`.

- [ ] **Step 4: Verify live behavior**

Verify:

```text
GET /health/live                                      -> 200 healthy
GET /api/admin/health/ready                           -> healthy
GET /api/admin/memory/jobs?page=1&pageSize=20         -> 200
GET /api/group-options                                -> 200 and named options
http://127.0.0.1:5173/                                -> 200
```

Open memory center, confirm the selected route UUID renders as a group name, and confirm all three tabs filter without showing a UUID input.
