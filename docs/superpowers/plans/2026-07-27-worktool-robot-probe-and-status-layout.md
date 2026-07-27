# WorkTool Robot Probe and Status Layout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复停用机器人无法执行 WorkTool 连接测试的问题，并将机器人启用 checkbox 改成易理解的运行状态开关和紧凑操作区。

**Architecture:** 将 WorkTool 凭据解析分成“运行操作”和“管理操作”两条路径。发送消息和群操作继续要求机器人已启用；连接测试及回调管理允许读取停用机器人已经保存的加密标识。API 在标识缺失时返回明确的配置冲突，前端据此阻止无效测试并展示可操作提示。

**Tech Stack:** .NET / ASP.NET Core Minimal APIs / Entity Framework Core / xUnit / Vue 3 / TypeScript / Element Plus / Vitest

## Global Constraints

- 不允许连接测试读取或返回 WorkTool 机器人 ID 明文。
- 发送消息和群操作仍然只能使用已启用机器人。
- 新输入或轮换的 WorkTool 机器人 ID 必须先保存，再执行连接测试。
- WorkTool 标识缺失属于本地配置错误，返回 HTTP 409，不返回 HTTP 502。
- 机器人运行状态使用 `ElSwitch`，并明确说明停用影响。

---

### Task 1: 分离管理操作与运行操作的凭据解析

**Files:**
- Modify: `src/server/WechatRobot.Application/WorkTool/IWorkToolClient.cs`
- Modify: `src/server/WechatRobot.Infrastructure/WorkTool/WorkToolCredentialResolver.cs`
- Modify: `src/server/WechatRobot.Infrastructure/WorkTool/WorkToolClient.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/WorkTool/WorkToolCredentialResolverTests.cs`

**Interfaces:**
- Consumes: `ISecretProtector.Unprotect(string)`
- Produces: `ResolveEnabledRobotIdAsync(Guid, CancellationToken)` 与 `ResolveConfiguredRobotIdAsync(Guid, CancellationToken)`

- [ ] **Step 1: Write the failing test**

新增真实 EF Core 凭据解析测试：

```csharp
[Fact]
public async Task Configured_resolution_allows_disabled_robot_but_enabled_resolution_rejects_it()
{
    // 保存 IsEnabled=false 且具有 EncryptedWorkToolRobotId 的机器人。
    // 管理解析返回明文标识；运行解析抛出 WorkToolCredentialUnavailableException。
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter FullyQualifiedName~WorkToolCredentialResolverTests
```

Expected: FAIL，因为接口尚未区分两种解析模式。

- [ ] **Step 3: Write minimal implementation**

`IWorkToolCredentialResolver` 提供两个明确方法。`WorkToolClient` 的 `SendTextAsync`、`ExecuteGroupOperationAsync` 使用启用解析；机器人查询、在线查询和回调管理使用已配置解析。缺少标识时抛出不携带密钥内容的专用异常。

- [ ] **Step 4: Run test to verify it passes**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter FullyQualifiedName~WorkToolCredentialResolverTests
```

Expected: PASS。

### Task 2: 将配置缺失映射为明确的 API 响应

**Files:**
- Modify: `src/server/WechatRobot.Api/WorkTool/WorkToolGroupOperationEndpoints.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/WorkTool/RobotSettingsEndpointTests.cs`

**Interfaces:**
- Consumes: `WorkToolCredentialUnavailableException`
- Produces: HTTP 409 `{ "error": "worktool-credential-required" }`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Probe_returns_configuration_conflict_when_robot_identifier_is_missing()
{
    // 创建无 EncryptedWorkToolRobotId 的停用机器人并调用 test-connection。
    // 断言 409 和 error=worktool-credential-required。
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter FullyQualifiedName~Probe_returns_configuration_conflict
```

Expected: FAIL，当前响应为 502。

- [ ] **Step 3: Write minimal implementation**

端点单独捕获凭据缺失异常并返回 409；网络、超时和 WorkTool 上游异常仍返回 502。

- [ ] **Step 4: Run test to verify it passes**

重复运行该过滤测试，Expected: PASS。

### Task 3: 修正机器人运行状态与操作区布局

**Files:**
- Modify: `src/web/wechatrobot-admin/src/views/settings/RobotSettingsView.vue`
- Test: `src/web/wechatrobot-admin/src/views/settings/RobotSettingsView.spec.ts`

**Interfaces:**
- Consumes: `RobotSettings.hasWorkToolRobotId`、本地 `credentials[item.id]`
- Produces: `ElSwitch` 运行状态行、明确说明、受控的连接测试按钮

- [ ] **Step 1: Write the failing tests**

新增行为断言：

```ts
it('renders a labeled runtime switch instead of a standalone checkbox', async () => {
  expect(wrapper.get('[data-testid="enabled-robot-1"]').attributes('role')).toBe('switch');
  expect(wrapper.text()).toContain('停用后不会用于消息发送和群操作');
});

it('requires saving a missing or newly entered robot id before probing', async () => {
  expect(wrapper.get('[data-testid="probe-robot-1"]').attributes('disabled')).toBeDefined();
});
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```powershell
npm --prefix src/web/wechatrobot-admin test -- RobotSettingsView.spec.ts
```

Expected: FAIL，因为当前页面使用原生 checkbox 且测试按钮始终可点击。

- [ ] **Step 3: Write minimal implementation**

使用 `ElSwitch` 渲染一行“机器人运行状态”，显示“已启用/已停用”；增加停用影响说明。将测试和保存放入主要操作组，回调查询放入回调配置区。标识缺失或输入框存在尚未保存的新值时禁用测试，并展示“请先保存机器人 ID”提示。

- [ ] **Step 4: Run tests to verify they pass**

重复运行页面测试，Expected: PASS。

### Task 4: 回归验证与本地重启

**Files:**
- Verify only: server and frontend projects

**Interfaces:**
- Consumes: Tasks 1-3
- Produces: 可运行的本地 API、Worker 和前端

- [ ] **Step 1: Run focused server tests**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~WorkToolCredentialResolverTests|FullyQualifiedName~RobotSettingsEndpointTests"
```

- [ ] **Step 2: Run frontend tests and type check**

```powershell
npm --prefix src/web/wechatrobot-admin test -- RobotSettingsView.spec.ts
npm --prefix src/web/wechatrobot-admin run type-check
```

- [ ] **Step 3: Run solution build**

```powershell
dotnet build WechatRobot.sln
```

- [ ] **Step 4: Restart and verify**

重启本地 API、Worker 和 Vite 前端；验证 `/health/live` 返回 200，前端页面可访问，并确认标识缺失机器人不再发起无意义的 WorkTool 请求。
