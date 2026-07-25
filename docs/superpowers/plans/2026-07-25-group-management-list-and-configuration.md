# Group Management List and Configuration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the free-form group GUID field with a registered-group list at `/groups` and a GUID-backed configuration detail at `/groups/:id/configuration`.

**Architecture:** Extend the existing WorkTool registered-group list response with display metadata, then add a focused Vue list component that routes by the backend-generated ID. Keep `GroupRulesView` responsible only for one route-selected group and preserve the existing GUID-constrained configuration API.

**Tech Stack:** ASP.NET Core 10 Minimal APIs, Entity Framework Core, xUnit, Vue 3, Vue Router, TypeScript, Vitest, Vue Test Utils.

## Global Constraints

- `GroupProfileEntity.Id` remains backend-generated with `Guid.NewGuid()`.
- No name-based group-configuration route is added.
- No browser-generated or operator-entered GUID is permitted.
- Preserve the existing `/groups/operations` registration and mutation workflow.
- Preserve matching rules, knowledge tags, context policy, preview, clear-context, and save behavior.
- Preserve unrelated dirty worktree changes.

---

### Task 1: Enrich the registered-group list contract

**Files:**
- Modify: `tests/server/WechatRobot.IntegrationTests/WorkTool/GroupOperationEndpointTests.cs`
- Modify: `src/server/WechatRobot.Api/WorkTool/WorkToolGroupOperationEndpoints.cs:268-304`

**Interfaces:**
- Consumes: `GroupProfileEntity` and `RobotConfigEntity` rows related by `RobotConfigId`.
- Produces: `KnownGroupResponse(Guid Id, Guid RobotConfigId, string RobotName, string Name, string? WorkToolGroupRemark, bool IsEnabled, DateTime UpdatedAtUtc)`.

- [ ] **Step 1: Write the failing endpoint test**

Add a test that seeds one robot and one group, calls
`GET /api/admin/worktool/groups`, and asserts the generated group `id`,
`robotConfigId`, `robotName`, `name`, `workToolGroupRemark`, `isEnabled`, and
`updatedAtUtc`:

```csharp
[Fact]
public async Task Group_list_returns_display_metadata_with_the_backend_generated_id()
{
    var robot = new RobotConfigEntity
    {
        Name = $"robot-{Guid.NewGuid():N}",
        WorkToolRobotId = $"robot-{Guid.NewGuid():N}",
        CallbackSecretHash = "test"
    };
    var updatedAt = new DateTime(2026, 7, 25, 1, 2, 3, DateTimeKind.Utc);
    var group = new GroupProfileEntity
    {
        RobotConfigId = robot.Id,
        Name = "技术群",
        WorkToolGroupRemark = "tech-east",
        IsEnabled = false,
        UpdatedAtUtc = updatedAt
    };
    using (var scope = _factory.Services.CreateScope())
    {
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        database.AddRange(robot, group);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    using var client = _factory.CreateClient();
    var items = await client.GetFromJsonAsync<JsonElement[]>(
        "/api/admin/worktool/groups",
        TestContext.Current.CancellationToken);
    var item = items!.Single(value => value.GetProperty("id").GetGuid() == group.Id);

    Assert.Equal(robot.Id, item.GetProperty("robotConfigId").GetGuid());
    Assert.Equal(robot.Name, item.GetProperty("robotName").GetString());
    Assert.Equal("技术群", item.GetProperty("name").GetString());
    Assert.Equal("tech-east", item.GetProperty("workToolGroupRemark").GetString());
    Assert.False(item.GetProperty("isEnabled").GetBoolean());
    Assert.Equal(updatedAt, item.GetProperty("updatedAtUtc").GetDateTime());
}
```

- [ ] **Step 2: Run the test and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~GroupOperationEndpointTests.Group_list_returns_display_metadata_with_the_backend_generated_id"
```

Expected: FAIL because `robotName`, `isEnabled`, and `updatedAtUtc` are absent.

- [ ] **Step 3: Implement the minimal joined projection**

Change `ListGroupsAsync` to join `GroupProfiles` with `RobotConfigs`, order by
group name and ID, and project the expanded response:

```csharp
private static async Task<IResult> ListGroupsAsync(
    WechatRobotDbContext database,
    CancellationToken cancellationToken) =>
    Results.Ok(await (
        from group in database.GroupProfiles.AsNoTracking()
        join robot in database.RobotConfigs.AsNoTracking()
            on group.RobotConfigId equals robot.Id
        orderby group.Name, group.Id
        select new KnownGroupResponse(
            group.Id,
            group.RobotConfigId,
            robot.Name,
            group.Name,
            group.WorkToolGroupRemark,
            group.IsEnabled,
            group.UpdatedAtUtc))
        .ToArrayAsync(cancellationToken));
```

Update registration responses to use the same response shape by reading the
robot name already validated for `request.RobotConfigId`.

- [ ] **Step 4: Run focused WorkTool endpoint tests**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~GroupOperationEndpointTests"
```

Expected: PASS.

- [ ] **Step 5: Commit the backend contract**

```powershell
git add src/server/WechatRobot.Api/WorkTool/WorkToolGroupOperationEndpoints.cs tests/server/WechatRobot.IntegrationTests/WorkTool/GroupOperationEndpointTests.cs
git commit -m "feat: enrich registered group list"
```

---

### Task 2: Build the registered-group list page and route

**Files:**
- Modify: `src/web/wechatrobot-admin/src/api/worktool.ts`
- Create: `src/web/wechatrobot-admin/src/views/groups/GroupListView.vue`
- Create: `src/web/wechatrobot-admin/src/views/groups/GroupListView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/router/index.ts`
- Modify: `src/web/wechatrobot-admin/src/router/index.spec.ts`

**Interfaces:**
- Consumes: `WorkToolOperationsApi.listGroups(): Promise<KnownGroup[]>`.
- Produces: `KnownGroup` fields `id`, `robotConfigId`, `robotName`, `name`, `workToolGroupRemark?`, `isEnabled`, and `updatedAtUtc`; named routes `group-list` and `group-configuration`.

- [ ] **Step 1: Write failing list-view and route tests**

Create a component test with a memory router. Resolve one group from
`listGroups()`, assert the visible name/status/robot, click `配置`, and assert
navigation to the generated GUID:

```ts
it('lists registered groups and opens configuration with the generated id', async () => {
  const id = '00000000-0000-0000-0000-000000000801';
  const api = {
    listGroups: vi.fn().mockResolvedValue([{
      id, robotConfigId: 'robot-1', robotName: '客服机器人',
      name: '技术群', workToolGroupRemark: 'tech-east',
      isEnabled: true, updatedAtUtc: '2026-07-25T01:02:03Z'
    }])
  };
  const wrapper = mount(GroupListView, {
    props: { api },
    global: {
      stubs: {
        RouterLink: {
          props: ['to'],
          template: '<a :data-to="JSON.stringify(to)"><slot /></a>'
        }
      }
    }
  });
  await flushPromises();

  expect(wrapper.text()).toContain('技术群');
  expect(wrapper.text()).toContain('客服机器人');
  expect(wrapper.get('[data-testid="configure-group"]').attributes('data-to'))
    .toContain(id);
  expect(wrapper.find('[aria-label="群配置 ID"]').exists()).toBe(false);
});
```

Add empty-list and rejected-list tests. Update `router/index.spec.ts` to require:

```ts
expect(children).toEqual(expect.arrayContaining([
  expect.objectContaining({ path: 'groups', name: 'group-list' }),
  expect.objectContaining({ path: 'groups/:id/configuration', name: 'group-configuration', props: true })
]));
```

- [ ] **Step 2: Run the frontend tests and verify RED**

Run:

```powershell
npm test -- src/views/groups/GroupListView.spec.ts src/router/index.spec.ts
```

Expected: FAIL because `GroupListView.vue` and the two-route structure do not
exist.

- [ ] **Step 3: Implement list types, page, and routes**

Expand `KnownGroup`, create `GroupListView.vue`, and replace the current
`groups` route with:

```ts
{ path: 'groups', name: 'group-list', component: GroupListView, meta: { roles: ['Admin'] } },
{ path: 'groups/:id/configuration', name: 'group-configuration', component: GroupRulesView, props: true, meta: { roles: ['Admin'] } },
{ path: 'groups/operations', name: 'group-operations', component: GroupOperationsView, meta: { roles: ['Admin'] } }
```

Keep the navigation label `群管理`, but point it to `group-list`. The list page
must render loading, empty, failure, and populated states; its only per-row
configuration action must be:

```vue
<RouterLink
  data-testid="configure-group"
  :to="{ name: 'group-configuration', params: { id: group.id } }"
>配置</RouterLink>
```

- [ ] **Step 4: Run list and router tests**

Run:

```powershell
npm test -- src/views/groups/GroupListView.spec.ts src/router/index.spec.ts
```

Expected: PASS.

- [ ] **Step 5: Commit the list page**

```powershell
git add src/web/wechatrobot-admin/src/api/worktool.ts src/web/wechatrobot-admin/src/views/groups/GroupListView.vue src/web/wechatrobot-admin/src/views/groups/GroupListView.spec.ts src/web/wechatrobot-admin/src/router/index.ts src/web/wechatrobot-admin/src/router/index.spec.ts
git commit -m "feat: add registered group list"
```

---

### Task 3: Convert the configuration page into a route-bound detail

**Files:**
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts`

**Interfaces:**
- Consumes: required route prop `id: string` and `GroupApi.getConfiguration(id)`.
- Produces: a non-editable group detail that calls GET and PUT only with the route ID.

- [ ] **Step 1: Write failing detail tests**

Update the component tests to pass `id` instead of `groupId`. Add assertions
that the page displays the loaded group name, has a `返回群列表` link, and has no
free-form ID input. Add a rejected-load test:

```ts
it('does not render an editable form when the route group is unavailable', async () => {
  const api = {
    getConfiguration: vi.fn().mockRejectedValue({ response: { status: 404 } }),
    updateConfiguration: vi.fn(),
    previewRules: vi.fn()
  };
  const wrapper = mount(GroupRulesView, {
    props: { id: '00000000-0000-0000-0000-000000000801', api },
    global: { stubs: { RouterLink: true } }
  });
  await flushPromises();

  expect(wrapper.text()).toContain('群不存在或已删除');
  expect(wrapper.find('[data-testid="save-configuration"]').exists()).toBe(false);
});
```

In the successful-save test, assert:

```ts
expect(api.updateConfiguration).toHaveBeenCalledWith(
  '00000000-0000-0000-0000-000000000801',
  expect.objectContaining({ clearContext: false })
);
expect(wrapper.find('[aria-label="群配置 ID"]').exists()).toBe(false);
```

- [ ] **Step 2: Run the detail tests and verify RED**

Run:

```powershell
npm test -- src/views/groups/GroupRulesView.spec.ts
```

Expected: FAIL because the view still accepts and edits `groupId`.

- [ ] **Step 3: Implement the route-bound detail**

Replace `useRoute()` and `activeGroupId` with a required `id` prop. Track
`loading`, `loadError`, and `configurationLoaded`. On successful load, show
`configuration.name` in the header and render the form. On failure, render
`群不存在或已删除` for 404 and `群配置加载失败，请稍后重试` otherwise. Use
`props.id` for every GET and PUT.

Replace the ID bar with:

```vue
<div class="group-panel group-identity-bar">
  <RouterLink :to="{ name: 'group-list' }">返回群列表</RouterLink>
  <strong v-if="groupName">{{ groupName }}</strong>
</div>
```

Render configuration sections and the save footer only when
`configurationLoaded` is true.

- [ ] **Step 4: Run the focused group frontend tests**

Run:

```powershell
npm test -- src/views/groups/GroupRulesView.spec.ts src/views/groups/GroupListView.spec.ts src/views/groups/GroupOperationsView.spec.ts src/router/index.spec.ts
```

Expected: PASS.

- [ ] **Step 5: Commit the detail page**

```powershell
git add src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts
git commit -m "fix: bind group configuration to route id"
```

---

### Task 4: Verify the complete change

**Files:**
- Verify only; fix only failures caused by Tasks 1-3.

**Interfaces:**
- Consumes: completed backend contract and frontend routes/components.
- Produces: a verified build with no regression in server or web tests.

- [ ] **Step 1: Run backend group and WorkTool tests**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~GroupConfigurationTests|FullyQualifiedName~GroupOperationEndpointTests"
```

Expected: PASS.

- [ ] **Step 2: Run the complete frontend test suite**

```powershell
npm test
```

Expected: PASS.

- [ ] **Step 3: Run frontend type checking and production build**

```powershell
npm run typecheck
npm run build
```

Expected: both commands exit `0`.

- [ ] **Step 4: Check the final diff**

```powershell
git diff --check HEAD~3
git status --short
```

Expected: no whitespace errors; unrelated pre-existing worktree changes remain
untouched and identifiable.
