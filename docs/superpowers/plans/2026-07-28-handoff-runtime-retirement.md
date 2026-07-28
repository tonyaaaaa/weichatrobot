# 人工转接运行功能退役实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 彻底移除人工转接运行链路、管理 API 和前端入口，同时原样保留历史人工转接表、关系、角色数据和已有记录。

**Architecture:** 先用回归测试锁定退役后的外部行为，再从消息处理链和依赖注入移除人工转接服务，随后收缩群配置、用户角色、审计契约与前端。历史 EF 实体和映射继续存在，但不注册运行服务、不暴露 API、不被新消息读写。本计划不修改 `KnowledgeCandidate` 结构；该非破坏性解耦由长期记忆计划统一实施。

**Tech Stack:** ASP.NET Core 10 Minimal APIs、EF Core/MySQL 5.7、xUnit v3、Vue 3、TypeScript、Element Plus、Vitest。

## Global Constraints

- 设计依据：`docs/superpowers/specs/2026-07-28-handoff-runtime-retirement-design.md`。
- 不删除或重写任何已应用迁移。
- 不删除 `HandoffCaseEntity`、`HandoffMessageEntity`、`HandoffTransitionEntity`、`GroupHumanAgentEntity`、对应 EF 配置、`DbSet` 或历史表。
- 不删除数据库中的 `HumanAgent` 角色或已有用户角色关系；只让它失去运行权限和新分配入口。
- 不在本计划内修改 `KnowledgeCandidateEntity.HandoffCaseId`；避免与长期记忆迁移冲突。
- 所有测试先失败再实现；每个任务只提交本任务文件，且不得夹带工作树中的既有修改。

---

### Task 1: 锁定消息链退役行为

**Files:**

- Modify: `tests/server/WechatRobot.UnitTests/Conversations/InboundMessageProcessorTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Handoffs/AutomaticHandoffPolicyPipelineTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Conversations/InboundGroupRulePipelineTests.cs`
- Modify: `src/server/WechatRobot.Application/Conversations/IGroundedConversationRepository.cs`
- Modify: `src/server/WechatRobot.Application/Conversations/AnswerDecision.cs`
- Modify: `src/server/WechatRobot.Application/Messaging/InboundMessageProcessor.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs`

- [ ] **Step 1: 写出退役后的失败测试**

将现有自动转接测试改成以下断言：

```csharp
[Fact]
public async Task Explicit_transfer_text_is_processed_as_an_ordinary_question()
{
    await processor.ProcessAsync(Message("转人工"), CancellationToken.None);

    Assert.Empty(await database.HandoffCases.ToListAsync());
    Assert.Contains(database.RetrievalAudits, audit =>
        audit.Decision != "Handoff");
}
```

同时增加信息不足和敏感问题不会创建、读取或更新人工转接记录的用例，并在
`InboundMessageProcessorTests` 中移除 `FakeHandoff` 依赖。

- [ ] **Step 2: 运行测试并确认因旧转接调用失败**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~InboundMessageProcessorTests"
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~AutomaticHandoffPolicyPipelineTests"
```

Expected: FAIL，失败原因是处理器仍调用 `IHandoffOrchestrator` 或产生
`Handoff` 决策。

- [ ] **Step 3: 从应用契约移除人工转接语义**

执行最小修改：

```csharp
public enum AnswerDecisionKind
{
    Answer,
    Clarification,
    InsufficientEvidence,
    SystemFailure
}

public enum NoEvidencePolicy
{
    InsufficientEvidence,
    Clarification
}
```

从 `ConversationProcessingRequest` 移除 `HandoffPausePolicy`，从
`InboundMessageProcessor` 构造函数和处理路径移除 `IHandoffOrchestrator`，
删除暂停查询、显式转接、自动转接和回答提交前的转接竞态检查。

- [ ] **Step 4: 收缩持久化运行接口**

从 `IGroundedConversationRepository` 和
`GroundedConversationRepository` 移除只服务于人工暂停或人工竞态的方法。
保留历史实体和表映射，不对历史行执行更新。

- [ ] **Step 5: 运行聚焦测试**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~Conversations"
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~AutomaticHandoffPolicyPipelineTests|FullyQualifiedName~InboundGroupRulePipelineTests"
```

Expected: PASS。

- [ ] **Step 6: 提交**

```powershell
git add src/server/WechatRobot.Application/Conversations src/server/WechatRobot.Application/Messaging/InboundMessageProcessor.cs src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs tests/server/WechatRobot.UnitTests/Conversations tests/server/WechatRobot.IntegrationTests/Handoffs/AutomaticHandoffPolicyPipelineTests.cs tests/server/WechatRobot.IntegrationTests/Conversations/InboundGroupRulePipelineTests.cs
git commit -m "refactor: retire handoff message processing"
```

### Task 2: 停止注册人工转接服务和 API

**Files:**

- Modify: `src/server/WechatRobot.Api/Program.cs`
- Modify: `src/server/WechatRobot.Worker/Program.cs`
- Delete: `src/server/WechatRobot.Api/Handoffs/HandoffEndpoints.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Auth/RoleAuthorizationTests.cs`
- Delete: `tests/server/WechatRobot.IntegrationTests/Handoffs/HandoffReadEndpointTests.cs`
- Delete: `tests/server/WechatRobot.IntegrationTests/Handoffs/HandoffPipelineTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Persistence/MigrationTests.cs`

- [ ] **Step 1: 增加路由不存在和历史表仍存在的测试**

```csharp
[Theory]
[InlineData("/api/handoffs")]
[InlineData("/api/handoffs/00000000-0000-0000-0000-000000000000")]
public async Task Retired_handoff_routes_return_not_found(string path)
{
    using var response = await Client.GetAsync(path);
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
}
```

在迁移测试中继续断言历史四类表存在，已有行数在迁移前后保持一致。

- [ ] **Step 2: 运行并确认路由测试失败**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~RoleAuthorizationTests|FullyQualifiedName~MigrationTests"
```

Expected: FAIL，`/api/handoffs` 仍有路由。

- [ ] **Step 3: 移除 API 和 Worker 运行注册**

从 `Api/Program.cs` 移除：

```csharp
builder.Services.AddScoped<IHandoffStore, EfHandoffStore>();
builder.Services.AddScoped<HandoffService>();
app.MapHandoffEndpoints();
```

从 `Worker/Program.cs` 移除 `IHandoffStore`、`HandoffService`、
`HandoffTriggerOptions`、`HandoffTriggerEvaluator` 和
`IHandoffOrchestrator` 注册。删除 `HandoffEndpoints.cs`，但保留
Application、Domain 和 Infrastructure 中的历史模型与映射。

- [ ] **Step 4: 移除只验证已退役操作的测试**

删除端点读写和状态机流水线测试；保留迁移兼容、历史实体映射和历史表存在性
测试。`RoleAuthorizationTests` 改为断言任何角色访问退役路径都得到 `404`。

- [ ] **Step 5: 运行集成测试**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~RoleAuthorizationTests|FullyQualifiedName~MigrationTests"
```

Expected: PASS。

- [ ] **Step 6: 提交**

```powershell
git add src/server/WechatRobot.Api/Program.cs src/server/WechatRobot.Worker/Program.cs src/server/WechatRobot.Api/Handoffs tests/server/WechatRobot.IntegrationTests
git commit -m "refactor: remove handoff runtime endpoints"
```

### Task 3: 从群配置和用户管理中移除客服能力

**Files:**

- Modify: `src/server/WechatRobot.Application/Groups/GroupConfigurationService.cs`
- Modify: `src/server/WechatRobot.Api/Groups/GroupEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Users/UserAdministrationEndpoints.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Identity/UserAdministrationService.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Groups/GroupConfigurationTests.cs`
- Delete: `tests/server/WechatRobot.IntegrationTests/Groups/GroupHumanAgentEndpointTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Identity/UserAdministrationEndpointTests.cs`

- [ ] **Step 1: 写收缩契约测试**

断言群配置 JSON 不包含：

```text
handoffPausePolicy
eligibleHumanAgents
humanAgents
defaultHumanAgent
```

并断言角色创建/更新请求中的 `HumanAgent` 被稳定返回 `400`，角色选项响应不再
包含 `HumanAgent`。

- [ ] **Step 2: 运行测试并确认失败**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~GroupConfigurationTests|FullyQualifiedName~UserAdministration"
```

Expected: FAIL，现有响应仍包含人工转接字段或允许分配角色。

- [ ] **Step 3: 收缩群配置契约**

删除 `HandoffPausePolicy` 应用枚举、请求字段、响应字段和验证逻辑；停止注册：

```text
GET /api/groups/{id}/eligible-human-agents
PUT /api/groups/{id}/human-agents
```

保留 `GroupProfileEntity.HandoffPausePolicy` 和 `GroupHumanAgentEntity` 的 EF
映射，不读取、不写入。

- [ ] **Step 4: 收缩用户角色分配**

角色列表只返回 `Admin` 和 `KnowledgeOperator`。对新增 `HumanAgent` 的请求
返回稳定验证错误；已经拥有该历史角色的用户仍可登录，但该角色不映射到任何
菜单或端点权限。

- [ ] **Step 5: 运行测试**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~Groups|FullyQualifiedName~UserAdministration"
```

Expected: PASS。

- [ ] **Step 6: 提交**

```powershell
git add src/server/WechatRobot.Application/Groups src/server/WechatRobot.Api/Groups src/server/WechatRobot.Api/Users src/server/WechatRobot.Infrastructure/Identity tests/server/WechatRobot.IntegrationTests/Groups tests/server/WechatRobot.IntegrationTests/Identity
git commit -m "refactor: remove human agent configuration"
```

### Task 4: 从会话审计和工作台移除人工转接投影

**Files:**

- Modify: `src/server/WechatRobot.Application/Audit/IConversationAuditQuery.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Conversations/ConversationAuditQuery.cs`
- Modify: `src/server/WechatRobot.Api/Audit/ConversationAuditEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Dashboard/DashboardSummaryService.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Conversations/ConversationAuditQueryTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Conversations/ConversationAuditEndpointTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Operations/DashboardSummaryEndpointTests.cs`

- [ ] **Step 1: 写无人工转接投影测试**

创建包含历史转接数据的会话，断言新的会话审计和工作台响应中不存在
`handoff`、`handoffCount`、`waitingHandoffCount` 字段。

- [ ] **Step 2: 运行并确认失败**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~ConversationAudit|FullyQualifiedName~Dashboard"
```

Expected: FAIL，旧投影仍返回人工转接对象或计数。

- [ ] **Step 3: 删除运行投影**

从审计查询 DTO、SQL/EF 投影和 API 响应删除人工转接对象；工作台删除待转接
统计。查询不得再联接人工转接表。

- [ ] **Step 4: 运行测试**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~ConversationAudit|FullyQualifiedName~Dashboard"
```

Expected: PASS，历史表仍存在，但应用响应不再暴露历史转接数据。

- [ ] **Step 5: 提交**

```powershell
git add src/server/WechatRobot.Application/Audit src/server/WechatRobot.Infrastructure/Conversations/ConversationAuditQuery.cs src/server/WechatRobot.Api/Audit src/server/WechatRobot.Api/Dashboard tests/server/WechatRobot.IntegrationTests
git commit -m "refactor: remove handoff audit projections"
```

### Task 5: 删除前端入口和类型

**Files:**

- Delete: `src/web/wechatrobot-admin/src/api/handoffs.ts`
- Delete: `src/web/wechatrobot-admin/src/api/handoffs.spec.ts`
- Delete: `src/web/wechatrobot-admin/src/views/handoffs/HandoffQueueView.vue`
- Modify: `src/web/wechatrobot-admin/src/api/groups.ts`
- Modify: `src/web/wechatrobot-admin/src/api/users.ts`
- Modify: `src/web/wechatrobot-admin/src/router/index.ts`
- Modify: `src/web/wechatrobot-admin/src/router/index.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/users/UserRolesView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/users/UserRolesView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/audit/ConversationAuditView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/audit/ConversationAuditView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/element-plus-operational.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/task16-operational.spec.ts`

- [ ] **Step 1: 先修改前端测试**

路由矩阵只保留 `Admin` 和 `KnowledgeOperator` 的有效菜单；断言：

```ts
expect(menuFor(['Admin']).map(item => item.label)).not.toContain('人工转接')
expect(router.getRoutes().some(route => route.path.includes('handoffs'))).toBe(false)
```

群配置不显示客服和暂停策略，用户角色选项不显示 `HumanAgent`，会话审计不显示
人工转接区块。

- [ ] **Step 2: 运行测试并确认失败**

Run:

```powershell
npm test -- --run src/router/index.spec.ts src/views/groups/GroupRulesView.spec.ts src/views/users/UserRolesView.spec.ts src/views/audit/ConversationAuditView.spec.ts
```

Workdir: `src/web/wechatrobot-admin`

Expected: FAIL，旧菜单、页面和字段仍存在。

- [ ] **Step 3: 删除前端实现**

删除人工转接 API 和页面；移除动态路由、菜单、客服选择、暂停策略、角色选项和
审计区块。不要留下“暂未支持”占位页面。

- [ ] **Step 4: 全局搜索确认无运行引用**

Run:

```powershell
rg -n "/api/handoffs|人工转接|HandoffQueueView|eligible-human-agents|human-agents" src/web/wechatrobot-admin/src
```

Expected: 无运行代码命中；若测试或历史说明仍需保留，必须逐项确认其用途。

- [ ] **Step 5: 运行前端验证**

Run:

```powershell
npm run typecheck
npm test -- --run
npm run build
```

Workdir: `src/web/wechatrobot-admin`

Expected: PASS。

- [ ] **Step 6: 提交**

```powershell
git add src/web/wechatrobot-admin/src
git commit -m "refactor: remove handoff administration ui"
```

### Task 6: 完成跨边界验证

**Files:**

- Modify: `docs/runbooks/local-development.md`（仅当其中仍宣称人工转接可用）
- Modify: `docs/superpowers/specs/2026-07-28-handoff-runtime-retirement-design.md`（仅修正实现中发现的事实差异）

- [ ] **Step 1: 运行服务端完整验证**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj
```

- [ ] **Step 2: 运行前端完整验证**

```powershell
npm run typecheck
npm test -- --run
npm run build
```

Workdir: `src/web/wechatrobot-admin`

- [ ] **Step 3: 启动本地栈并验收**

按 `AGENTS.md` 使用 `.local` 启动 API、Worker 和前端，验证：

```text
GET  http://127.0.0.1:5268/health/live              -> 200
GET  /api/admin/health/ready                        -> required dependencies ready
GET  /api/handoffs                                  -> 404
POST 普通知识问答                                   -> 正常进入发送队列
POST 文本“转人工”                                   -> 按普通问题处理
```

同时通过数据库只读查询确认历史人工转接表仍存在，基线行数未变化。

- [ ] **Step 4: 检查差异和秘密**

```powershell
git diff --check
rg -n "password=|AccessKeySecret|SigningKey=|Qdrant__ApiKey=" src tests docs
```

Expected: 差异检查通过；秘密扫描无新增真实值。

- [ ] **Step 5: 提交文档修订**

```powershell
git add docs/runbooks/local-development.md docs/superpowers/specs/2026-07-28-handoff-runtime-retirement-design.md
git commit -m "docs: align runtime with handoff retirement"
```
