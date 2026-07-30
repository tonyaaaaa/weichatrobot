# Agent Framework 私聊入库、固定回复与智能路由实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在不改写现有可靠 RAG、Qdrant、Durable Job 和 WorkTool 发送链的前提下，交付可验证的 Agent Framework 兼容层、私聊问答与直接知识入库、群固定回复模板，并按 Shadow 和灰度门槛渐进接管群消息意图判断与回答执行。

**Architecture:** Application 层定义框架无关合同，Infrastructure 层适配 `Microsoft.Extensions.AI.IChatClient` 与 Microsoft Agent Framework，API 只负责验证、鉴权和快速入站，Worker 继续负责模型调用、知识处理、索引和发送。新增能力分别由四个运行模式控制；私聊和模板可以先正式启用，群意图判断先 Shadow，普通回答保持 Legacy，任何 Agent 工具都不能直接写 EF Core、Qdrant 或发送队列。

**Tech Stack:** ASP.NET Core 10、EF Core 10、MySQL 5.7、Microsoft Agent Framework 1.15.0、Microsoft.Extensions.AI、Vue 3、TypeScript、Element Plus、Vitest、xUnit v3、Qdrant、WorkTool。

## Global Constraints

- `Microsoft.Agents.AI` 和 `Microsoft.Agents.AI.OpenAI` 固定使用已复核的稳定版 `1.15.0`，版本只写入 `Directory.Packages.props`。
- WorkTool 只依赖官方文档和已脱敏真实样例确认的字段；不得虚构引用消息 ID、稳定成员 ID 或群成员目录。
- `roomType=2` 和 `roomType=4` 支持私聊问答；只有 `roomType=4` 且首行严格为 `#知识入库` 才能直接入库。
- 一次私聊入库最多生成 20 条问答；无可靠标签时绑定系统管理的“全局知识”标签。
- 固定模板正文为纯文本；Agent 只能选择模板 ID 或继续知识问答，不能生成、改写或读取模板正文。
- `IntentRuntimeMode`、`AnswerRuntimeMode`、`PrivateChatRuntimeMode`、`TemplateRoutingRuntimeMode` 独立配置。
- 意图正式接管后的安全回退是 `Paused`，不得自动回退为 Legacy、仅 `@` 或全部回复。
- 所有数据库迁移兼容 MySQL 5.7，不依赖 `CHECK` 约束，不修改已应用迁移。
- 现有发送 FIFO、限流、幂等、租约、重试和死信语义保持不变。
- 前端复用现有 API 类型、群名称选择器和 Element Plus；组件逻辑导入、独立样式导入和浏览器视觉验收属于同一交付。
- 全部新增写操作使用现有管理授权策略并写 `AdministrationAudits`。
- 不记录或返回模型密钥、机器人标识明文、回调令牌、连接字符串、原始模型响应和完整提示词。

---

## Milestone A：Agent Framework 兼容层和真实能力探测

### Task 1: 固化现有模型、RAG 和发送行为基线

**Files:**
- Modify: `tests/server/WechatRobot.ContractTests/Models/OpenAiCompatibleClientTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Conversations/RagReplyPipelineTests.cs`
- Create: `tests/server/WechatRobot.UnitTests/Models/OpenAiCompatibleEndpointResolverRegressionTests.cs`

**Interfaces:**
- Consumes: `IChatCompletionClient.CompleteAsync(ModelProviderConfiguration, ChatCompletionRequest, CancellationToken)`
- Produces: 迁移期间必须持续通过的 Legacy 特征测试，覆盖知识、Web Search、模型知识降级、端点拼接和单次发送。

- [ ] **Step 1: 写端点、回答来源和发送幂等回归测试**

```csharp
[Theory]
[InlineData("https://api.example.com", "https://api.example.com/v1/chat/completions")]
[InlineData("https://api.example.com/v4", "https://api.example.com/v4/chat/completions")]
[InlineData("https://api.example.com/v4/chat/completions", "https://api.example.com/v4/chat/completions")]
public void Resolve_preserves_existing_resource_and_appends_once(string value, string expected)
{
    OpenAiCompatibleEndpointResolver.Resolve(value, "chat/completions")
        .Should().Be(new Uri(expected));
}
```

在 `RagReplyPipelineTests` 增加断言：知识命中、Web Search、模型知识降级分别写入原有 `AnswerSource`，同一入站消息最终只有一条 `SendCommand`。

- [ ] **Step 2: 运行测试并确认基线**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj -- --filter-class "*OpenAiCompatibleEndpointResolverRegressionTests"
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj -- --filter-class "*OpenAiCompatibleClientTests"
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*RagReplyPipelineTests"
```

Expected: 新增测试通过；若现状失败，只修正测试对真实现状的错误假设，不改变生产行为。

- [ ] **Step 3: 提交基线测试**

```powershell
git add tests/server/WechatRobot.UnitTests/Models/OpenAiCompatibleEndpointResolverRegressionTests.cs tests/server/WechatRobot.ContractTests/Models/OpenAiCompatibleClientTests.cs tests/server/WechatRobot.IntegrationTests/Conversations/RagReplyPipelineTests.cs
git commit -m "test: lock intelligent reply legacy behavior"
```

### Task 2: 引入稳定依赖和框架无关 Agent 合同

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/server/WechatRobot.Infrastructure/WechatRobot.Infrastructure.csproj`
- Create: `src/server/WechatRobot.Application/Agents/AgentRuntimeContracts.cs`
- Create: `src/server/WechatRobot.Application/Agents/IAgentChatClientFactory.cs`
- Test: `tests/server/WechatRobot.UnitTests/Agents/AgentRuntimeContractTests.cs`

**Interfaces:**
- Produces:

```csharp
public enum AgentCapability { Chat, FunctionTools, ToolResultLoop, JsonObject, JsonSchema }
public sealed record AgentCapabilityReport(
    Guid ModelConfigurationId,
    int ModelConfigurationVersion,
    IReadOnlySet<AgentCapability> Supported,
    string? FailureCode,
    DateTime TestedAtUtc);
public interface IAgentChatClientFactory
{
    Task<IChatClient> CreateAsync(Guid modelConfigurationId, CancellationToken cancellationToken);
}
```

- [ ] **Step 1: 写合同枚举和失败码测试**

```csharp
[Fact]
public void Capability_report_does_not_treat_chat_as_tool_support()
{
    var report = new AgentCapabilityReport(Guid.NewGuid(), 3,
        new HashSet<AgentCapability> { AgentCapability.Chat }, null, DateTime.UtcNow);
    Assert.DoesNotContain(AgentCapability.FunctionTools, report.Supported);
}
```

- [ ] **Step 2: 运行测试并确认缺少合同**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj -- --filter-class "*AgentRuntimeContractTests"
```

Expected: FAIL，原因是 `AgentCapabilityReport` 尚不存在。

- [ ] **Step 3: 添加中央包版本和最小合同**

```xml
<PackageVersion Include="Microsoft.Agents.AI" Version="1.15.0" />
<PackageVersion Include="Microsoft.Agents.AI.OpenAI" Version="1.15.0" />
<PackageVersion Include="Microsoft.Extensions.AI" Version="10.8.3" />
<PackageVersion Include="Microsoft.Extensions.AI.OpenAI" Version="10.8.3" />
```

Infrastructure 只添加无版本的 `PackageReference`。Application 如需引用
`IChatClient`，添加 `Microsoft.Extensions.AI` 包引用，不引用
`Microsoft.Agents.AI.OpenAI`。

- [ ] **Step 4: 运行合同测试和解决方案构建**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj -- --filter-class "*AgentRuntimeContractTests"
dotnet build WechatRobot.sln
```

Expected: PASS，0 编译错误。

- [ ] **Step 5: 提交合同和依赖**

```powershell
git add Directory.Packages.props src/server/WechatRobot.Application/WechatRobot.Application.csproj src/server/WechatRobot.Infrastructure/WechatRobot.Infrastructure.csproj src/server/WechatRobot.Application/Agents tests/server/WechatRobot.UnitTests/Agents
git commit -m "feat: add agent runtime contracts"
```

### Task 3: 实现 `IChatClient` 适配器、工厂和真实能力探针

**Files:**
- Create: `src/server/WechatRobot.Infrastructure/Agents/OpenAiCompatibleAgentChatClientFactory.cs`
- Create: `src/server/WechatRobot.Infrastructure/Agents/AgentCapabilityProbe.cs`
- Create: `src/server/WechatRobot.Infrastructure/Agents/AgentMiddleware.cs`
- Modify: `src/server/WechatRobot.Api/Models/ModelConfigurationEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Program.cs`
- Modify: `src/server/WechatRobot.Worker/Program.cs`
- Modify: `src/web/wechatrobot-admin/src/api/models.ts`
- Modify: `src/web/wechatrobot-admin/src/views/models/ModelSettingsView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/models/ModelConfigurationDialog.vue`
- Test: `tests/server/WechatRobot.ContractTests/Models/AgentCapabilityProbeTests.cs`
- Test: `tests/server/WechatRobot.UnitTests/Agents/AgentMiddlewareTests.cs`
- Test: `src/web/wechatrobot-admin/src/views/models/ModelSettingsView.spec.ts`

**Interfaces:**
- Consumes: `IAgentChatClientFactory`
- Produces:

```csharp
public interface IAgentCapabilityProbe
{
    Task<AgentCapabilityReport> ProbeAsync(Guid modelConfigurationId, CancellationToken cancellationToken);
}
```

API: `POST /api/admin/model-configurations/{id}/test-agent-capabilities`.

- [ ] **Step 1: 写真实工具循环、JSON Schema 被忽略和密钥脱敏合约测试**

测试服务器按请求次序返回一次 `tool_calls`、一次工具结果后的最终文本；断言探针
只有在两次请求均符合合同时才报告 `FunctionTools` 和 `ToolResultLoop`。另一个
样例忽略 `response_format` 并返回自由文本，断言不报告 `JsonSchema`。

```csharp
Assert.Contains(AgentCapability.FunctionTools, report.Supported);
Assert.Contains(AgentCapability.ToolResultLoop, report.Supported);
Assert.DoesNotContain(AgentCapability.JsonSchema, ignoredFormatReport.Supported);
Assert.DoesNotContain("Bearer", serializedAudit);
```

- [ ] **Step 2: 运行测试并确认能力探针不存在**

Run:

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj -- --filter-class "*AgentCapabilityProbeTests"
```

Expected: FAIL，原因是工厂和探针未实现。

- [ ] **Step 3: 实现工厂、探针和中间件**

工厂必须复用 `OpenAiCompatibleEndpointResolver`、现有模型配置读取和
`ISecretProtector`。中间件只记录模型配置 ID、版本、Agent 名称、耗时、终态工具
名称和稳定失败码：

```csharp
public sealed record AgentInvocationAudit(
    string AgentName,
    Guid ModelConfigurationId,
    int ModelConfigurationVersion,
    string? TerminalTool,
    string? FailureCode,
    long ElapsedMilliseconds);
```

探针按 `Chat -> FunctionTools -> ToolResultLoop -> JsonObject -> JsonSchema` 逐项验证；
前一项失败不推断后一项成功。超时返回 `agent_probe_timeout`，无效格式返回
`agent_probe_invalid_output`。

- [ ] **Step 4: 接入模型管理 API 和前端能力面板**

前端显示每项“支持/不支持/未检测”，不得把“连接成功”显示为“支持工具调用”。
新增按钮必须使用现有弹框和消息组件，并在 `main.ts` 确认相关 Element Plus 样式
已加载。

- [ ] **Step 5: 运行后端和前端测试**

Run:

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj -- --filter-class "*AgentCapabilityProbeTests"
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj -- --filter-class "*AgentMiddlewareTests"
Push-Location src/web/wechatrobot-admin
npm test -- --run src/views/models/ModelSettingsView.spec.ts
npm run typecheck
Pop-Location
```

Expected: PASS；探针不泄漏上游正文和凭据。

- [ ] **Step 6: 提交兼容层**

```powershell
git add src/server/WechatRobot.Infrastructure/Agents src/server/WechatRobot.Api/Models/ModelConfigurationEndpoints.cs src/server/WechatRobot.Api/Program.cs src/server/WechatRobot.Worker/Program.cs src/web/wechatrobot-admin/src/api/models.ts src/web/wechatrobot-admin/src/views/models/ModelSettingsView.vue src/web/wechatrobot-admin/src/views/models/ModelConfigurationDialog.vue tests/server/WechatRobot.ContractTests/Models/AgentCapabilityProbeTests.cs tests/server/WechatRobot.UnitTests/Agents/AgentMiddlewareTests.cs src/web/wechatrobot-admin/src/views/models/ModelSettingsView.spec.ts
git commit -m "feat: probe agent model capabilities"
```

## Milestone B：群固定回复模板

### Task 4: 建立固定模板领域合同、持久化和 MySQL 5.7 迁移

**Files:**
- Create: `src/server/WechatRobot.Application/FixedReplies/FixedReplyContracts.cs`
- Create: `src/server/WechatRobot.Application/FixedReplies/FixedReplyTemplateService.cs`
- Create: `src/server/WechatRobot.Application/FixedReplies/IFixedReplyTemplateStore.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Entities/FixedReplyTemplateEntity.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Entities/FixedReplyTemplateExampleEntity.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Entities/FixedReplyTemplateGroupRuleEntity.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Configurations/FixedReplyConfigurations.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/WechatRobotDbContext.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/FixedReplyTemplateStore.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/20260729010000_AddFixedReplyTemplates.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/WechatRobotDbContextModelSnapshot.cs`
- Test: `tests/server/WechatRobot.UnitTests/FixedReplies/FixedReplyTemplateServiceTests.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/FixedReplies/FixedReplyTemplateMySqlTests.cs`

**Interfaces:**
- Produces:

```csharp
public enum FixedReplyScopeType { Global, SelectedGroups }
public enum FixedReplyGroupEffect { Include, Exclude }
public sealed record EffectiveFixedReply(
    Guid Id, int Version, string Name, string IntentDescription,
    IReadOnlyList<string> Examples, int Priority, bool IsGroupSpecific);
public interface IFixedReplyTemplateStore
{
    Task<IReadOnlyList<EffectiveFixedReply>> ListEffectiveAsync(
        Guid groupProfileId, int maximumCandidates, int examplesPerTemplate,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 1: 写作用域、排序、并发和受控删除测试**

覆盖：指定群 Include 优先于 Global；Global Exclude 后当前群不可见；同层按
Priority 降序再按 ID；Global 不接受 Include；SelectedGroups 不接受 Exclude；
版本冲突抛稳定并发异常；有审计引用时删除转软删除。

- [ ] **Step 2: 运行测试并确认失败**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj -- --filter-class "*FixedReplyTemplateServiceTests"
```

Expected: FAIL，原因是固定模板合同不存在。

- [ ] **Step 3: 实现实体、映射、服务和 Store**

约束在应用服务和 `WechatRobotDbContext.ValidatePersistenceInvariants` 双层执行。
`TemplateId + GroupProfileId`、`TemplateId + NormalizedText` 建唯一索引；软删除
字段为 `DeletedAtUtc`。正文只在最终匹配校验结果中读取，候选列表不包含正文。

- [ ] **Step 4: 生成并检查迁移**

Run:

```powershell
dotnet ef migrations add AddFixedReplyTemplates --project src/server/WechatRobot.Infrastructure --startup-project src/server/WechatRobot.Api
rg -n "CHECK|DROP TABLE|DROP COLUMN" src/server/WechatRobot.Infrastructure/Persistence/Migrations/*AddFixedReplyTemplates.cs
```

Expected: 没有 `CHECK`、意外删除表或删除列；唯一索引和外键存在。

- [ ] **Step 5: 运行单元和 MySQL 集成测试**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj -- --filter-class "*FixedReplyTemplateServiceTests"
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*FixedReplyTemplateMySqlTests"
```

Expected: PASS，MySQL 5.7 容器迁移和作用域查询通过。

- [ ] **Step 6: 提交模板数据层**

```powershell
git add src/server/WechatRobot.Application/FixedReplies src/server/WechatRobot.Infrastructure/Persistence/Entities/FixedReplyTemplate* src/server/WechatRobot.Infrastructure/Persistence/Configurations/FixedReplyConfigurations.cs src/server/WechatRobot.Infrastructure/Persistence/WechatRobotDbContext.cs src/server/WechatRobot.Infrastructure/Persistence/FixedReplyTemplateStore.cs src/server/WechatRobot.Infrastructure/Persistence/Migrations tests/server/WechatRobot.UnitTests/FixedReplies tests/server/WechatRobot.IntegrationTests/FixedReplies
git commit -m "feat: persist fixed reply templates"
```

### Task 5: 实现模板管理 API、审计和双向群作用域

**Files:**
- Create: `src/server/WechatRobot.Api/FixedReplies/FixedReplyTemplateEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Program.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/FixedReplies/FixedReplyTemplateEndpointTests.cs`

**Interfaces:**
- Produces:
  - `GET/POST /api/admin/fixed-reply-templates`
  - `GET/PUT/DELETE /api/admin/fixed-reply-templates/{id}`
  - `POST /api/admin/fixed-reply-templates/{id}/enable`
  - `POST /api/admin/fixed-reply-templates/{id}/disable`
  - `PUT /api/admin/fixed-reply-templates/{id}/group-rules`
  - `GET /api/admin/groups/{groupId}/fixed-reply-templates`
  - 群视角 include/exclude 增删端点

- [ ] **Step 1: 写授权、验证、并发和共享服务端点测试**

```csharp
response.StatusCode.Should().Be(HttpStatusCode.Conflict);
audit.Operation.Should().Be("fixed_reply_template.updated");
groupView.TemplateId.Should().Be(templateView.Id);
```

测试 UUID 不存在、指定群空集合、非法 Include/Exclude、版本冲突、Admin 权限和
管理审计。

- [ ] **Step 2: 运行端点测试并确认 404**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*FixedReplyTemplateEndpointTests"
```

Expected: FAIL，端点尚未映射。

- [ ] **Step 3: 实现薄端点并复用同一应用服务**

端点只负责 DTO、授权、验证错误映射和审计；模板视角与群视角均调用
`FixedReplyTemplateService`，不得直接操作 `DbContext`。

- [ ] **Step 4: 运行端点测试**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*FixedReplyTemplateEndpointTests"
```

Expected: PASS。

- [ ] **Step 5: 提交管理 API**

```powershell
git add src/server/WechatRobot.Api/FixedReplies src/server/WechatRobot.Api/Program.cs tests/server/WechatRobot.IntegrationTests/FixedReplies/FixedReplyTemplateEndpointTests.cs
git commit -m "feat: add fixed reply administration api"
```

### Task 6: 实现 Template Routing Agent 和现有 RAG 安全降级

**Files:**
- Create: `src/server/WechatRobot.Application/FixedReplies/TemplateRouteDecision.cs`
- Create: `src/server/WechatRobot.Application/FixedReplies/ITemplateRoutingAgent.cs`
- Create: `src/server/WechatRobot.Infrastructure/Agents/TemplateRoutingAgent.cs`
- Modify: `src/server/WechatRobot.Application/Messaging/InboundMessageProcessor.cs`
- Modify: `src/server/WechatRobot.Application/Conversations/AnswerDecision.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/RetrievalAuditEntity.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Configurations/ConversationConfigurations.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/20260729020000_AddFixedReplyAudit.cs`
- Test: `tests/server/WechatRobot.UnitTests/FixedReplies/TemplateRoutingAgentTests.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Conversations/FixedReplyPipelineTests.cs`

**Interfaces:**
- Produces:

```csharp
public abstract record TemplateRouteDecision;
public sealed record MatchFixedTemplate(Guid TemplateId, int ExpectedVersion)
    : TemplateRouteDecision;
public sealed record ContinueKnowledgeAnswer(string? FailureCode = null)
    : TemplateRouteDecision;
public interface ITemplateRoutingAgent
{
    Task<TemplateRouteDecision> RouteAsync(
        Guid groupProfileId, string message, CancellationToken cancellationToken);
}
```

- [ ] **Step 1: 写互斥终态工具和降级测试**

覆盖明确命中、模糊问题、自由文本、未知工具、多工具、未知模板、停用模板、版本
冲突、不支持 Function Tool、超时。除明确有效匹配外均断言进入现有 RAG。

- [ ] **Step 2: 运行测试并确认失败**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj -- --filter-class "*TemplateRoutingAgentTests"
```

Expected: FAIL，Agent 尚不存在。

- [ ] **Step 3: 实现候选裁剪、Agent 工具和后端二次校验**

Agent 输入只包含模板 ID、版本、名称、意图、有限示例、层级和优先级。注册两个
互斥终态工具：

```csharp
match_fixed_template(Guid templateId, int expectedVersion)
continue_knowledge_answer()
```

`match_fixed_template` 必须重新查询版本、状态、群状态和作用域后才读取正文。
任何异常返回稳定失败码并继续 RAG。

- [ ] **Step 4: 在现有允许回复策略之后接入**

`InboundMessageProcessor` 保留 `EvaluateInboundPolicyAsync` 为第一道技术过滤；
只有允许回复且 `TemplateRoutingRuntimeMode=AgentFramework` 才调用模板 Agent。
固定回复继续通过现有会话租约、`PersistAnswerAndEnqueueAsync` 和稳定幂等键创建
唯一发送命令。

- [ ] **Step 5: 运行单元、集成和迁移检查**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj -- --filter-class "*TemplateRoutingAgentTests"
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*FixedReplyPipelineTests"
git diff --check
```

Expected: PASS；固定回复进入会话，审计 `AnswerSource=fixed_template`，发送命令一条。

- [ ] **Step 6: 提交模板运行链**

```powershell
git add src/server/WechatRobot.Application/FixedReplies src/server/WechatRobot.Infrastructure/Agents/TemplateRoutingAgent.cs src/server/WechatRobot.Application/Messaging/InboundMessageProcessor.cs src/server/WechatRobot.Application/Conversations/AnswerDecision.cs src/server/WechatRobot.Infrastructure/Persistence/Entities/RetrievalAuditEntity.cs src/server/WechatRobot.Infrastructure/Persistence/Configurations src/server/WechatRobot.Infrastructure/Persistence/Migrations tests/server/WechatRobot.UnitTests/FixedReplies tests/server/WechatRobot.IntegrationTests/Conversations/FixedReplyPipelineTests.cs
git commit -m "feat: route group messages to fixed replies"
```

### Task 7: 交付模板独立页面和群详情管理

**Files:**
- Create: `src/web/wechatrobot-admin/src/api/fixedReplies.ts`
- Create: `src/web/wechatrobot-admin/src/api/fixedReplies.spec.ts`
- Create: `src/web/wechatrobot-admin/src/views/fixed-replies/FixedReplyTemplatesView.vue`
- Create: `src/web/wechatrobot-admin/src/views/fixed-replies/FixedReplyTemplatesView.spec.ts`
- Create: `src/web/wechatrobot-admin/src/views/fixed-replies/FixedReplyTemplateDialog.vue`
- Create: `src/web/wechatrobot-admin/src/components/groups/GroupFixedRepliesPanel.vue`
- Create: `src/web/wechatrobot-admin/src/components/groups/GroupFixedRepliesPanel.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/components/groups/GroupKnowledgeAnswerPanel.vue`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue`
- Modify: `src/web/wechatrobot-admin/src/router/index.ts`
- Modify: `src/web/wechatrobot-admin/src/layouts/AdminLayout.vue`
- Modify: `src/web/wechatrobot-admin/src/main.ts`
- Test: `tests/e2e/fixed-reply-templates.spec.ts`

**Interfaces:**
- Consumes: Task 5 API。
- Produces: 菜单“固定回复模板”、Element Plus 新增/编辑/测试弹框、群详情共享管理面板。

- [ ] **Step 1: 写 API、页面和群面板组件测试**

断言加载/空/失败状态、群名称多选、并发版本、Global 排除群、SelectedGroups
Include、`ElMessageBox` 删除确认、路由预览和两个入口刷新一致。

- [ ] **Step 2: 运行前端测试并确认组件缺失**

Run:

```powershell
Push-Location src/web/wechatrobot-admin
npm test -- --run src/api/fixedReplies.spec.ts src/views/fixed-replies/FixedReplyTemplatesView.spec.ts src/components/groups/GroupFixedRepliesPanel.spec.ts
Pop-Location
```

Expected: FAIL，模块尚不存在。

- [ ] **Step 3: 实现 API 类型和弹框**

模板表单字段固定为名称、意图说明、固定正文、示例问法、作用域、群名称、
优先级、启用状态和版本。群选择复用 `GroupProfileSelect.vue`，禁止 UUID 输入框。

- [ ] **Step 4: 实现独立页面和群详情面板**

独立页面支持筛选、分页、CRUD 和真实路由预览；群面板区分全局、群专属、已排除
全局模板，并支持绑定、解除、排除、恢复。高影响操作统一使用 `ElMessageBox`。

- [ ] **Step 5: 检查 Element Plus 逻辑、样式和视觉**

在 `main.ts` 同时导入实际使用组件和对应样式。以 1440×900、1024×768 和
390×844 检查无样式丢失、弹框不溢出、焦点可见、关闭后焦点返回触发按钮。

- [ ] **Step 6: 运行前端完整验证**

Run:

```powershell
Push-Location src/web/wechatrobot-admin
npm run typecheck
npm test -- --run
npm run build
Pop-Location
```

Expected: PASS。

- [ ] **Step 7: 提交模板管理前端**

```powershell
git add src/web/wechatrobot-admin/src/api/fixedReplies* src/web/wechatrobot-admin/src/views/fixed-replies src/web/wechatrobot-admin/src/components/groups/GroupFixedRepliesPanel* src/web/wechatrobot-admin/src/components/groups/GroupKnowledgeAnswerPanel.vue src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue src/web/wechatrobot-admin/src/router/index.ts src/web/wechatrobot-admin/src/layouts/AdminLayout.vue src/web/wechatrobot-admin/src/main.ts tests/e2e/fixed-reply-templates.spec.ts
git commit -m "feat: manage fixed replies in admin ui"
```

## Milestone C：私聊问答和直接知识入库

### Task 8: 扩展入站、会话和 WorkTool 私聊发送合同

**Files:**
- Modify: `src/server/WechatRobot.Application/WorkTool/WorkToolCallbackDto.cs`
- Modify: `src/server/WechatRobot.Api/WorkTool/WorkToolCallbackEndpoints.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/ConversationSessionEntity.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/ConversationMessageEntity.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Configurations/ConversationConfigurations.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/GroundedConversationRepository.cs`
- Create: `src/server/WechatRobot.Application/Conversations/PrivateConversationScope.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/20260729030000_AddPrivateConversationScopes.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/WechatRobotDbContextModelSnapshot.cs`
- Test: `tests/server/WechatRobot.ContractTests/WorkTool/WorkToolPrivateCallbackContractTests.cs`
- Test: `tests/server/WechatRobot.UnitTests/Conversations/PrivateConversationScopeTests.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Conversations/PrivateConversationMySqlTests.cs`

**Interfaces:**
- Produces:

```csharp
public enum ConversationChannelType { Group, Private }
public sealed record PrivateConversationScope(
    Guid RobotConfigId, int RoomType, string PeerDisplayName, string ScopeHash);
```

- [ ] **Step 1: 写 `roomType=2/4` 回调和作用域哈希测试**

使用官方字段样例断言 `receivedName` 被保存为显示名，不被描述为稳定用户 ID；
同机器人、同 roomType、规范化同名生成同 `ScopeHash`，不同 roomType 不共享会话。

- [ ] **Step 2: 运行测试并确认当前群名校验拒绝私聊**

Run:

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj -- --filter-class "*WorkToolPrivateCallbackContractTests"
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj -- --filter-class "*PrivateConversationScopeTests"
```

Expected: FAIL，当前 `InboundMessageProcessor` 要求 `GroupName` 非空。

- [ ] **Step 3: 扩展实体、索引和迁移**

`ConversationSessionEntity.GroupProfileId` 改为可空，并增加 `ChannelType`、
`RoomType`、`RobotConfigId`、`PeerDisplayName`、`ScopeHash`。现有群会话回填
`ChannelType=Group` 和稳定群作用域；私聊唯一索引为
`RobotConfigId + RoomType + ScopeHash`。

- [ ] **Step 4: 按房间类型路由 Durable Job**

群消息继续创建 `ProcessInboundMessage`；`roomType=2/4` 文本创建
`ProcessPrivateMessage`；`roomType=3` 和未验证消息类型成功确认并记录忽略原因，
不创建回复任务。

- [ ] **Step 5: 验证 MySQL 迁移和回调幂等**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*PrivateConversationMySqlTests"
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj -- --filter-class "*WorkToolPrivateCallbackContractTests"
```

Expected: PASS；同一回调只创建一条消息和一个 Durable Job。

- [ ] **Step 6: 提交私聊入站和会话**

```powershell
git add src/server/WechatRobot.Application/WorkTool/WorkToolCallbackDto.cs src/server/WechatRobot.Api/WorkTool/WorkToolCallbackEndpoints.cs src/server/WechatRobot.Application/Conversations/PrivateConversationScope.cs src/server/WechatRobot.Infrastructure/Persistence/Entities/ConversationSessionEntity.cs src/server/WechatRobot.Infrastructure/Persistence/Entities/ConversationMessageEntity.cs src/server/WechatRobot.Infrastructure/Persistence/Configurations src/server/WechatRobot.Infrastructure/Persistence/GroundedConversationRepository.cs src/server/WechatRobot.Infrastructure/Persistence/Migrations tests/server/WechatRobot.ContractTests/WorkTool/WorkToolPrivateCallbackContractTests.cs tests/server/WechatRobot.UnitTests/Conversations/PrivateConversationScopeTests.cs tests/server/WechatRobot.IntegrationTests/Conversations/PrivateConversationMySqlTests.cs
git commit -m "feat: accept worktool private conversations"
```

### Task 9: 实现私聊普通问答 Agent

**Files:**
- Create: `src/server/WechatRobot.Application/PrivateChat/PrivateChatCommandParser.cs`
- Create: `src/server/WechatRobot.Application/PrivateChat/PrivateChatProcessor.cs`
- Create: `src/server/WechatRobot.Infrastructure/Agents/PrivateChatAgent.cs`
- Modify: `src/server/WechatRobot.Application/Knowledge/IKnowledgeTagScopeResolver.cs`
- Modify: `src/server/WechatRobot.Application/Conversations/GroundedAnswerService.cs`
- Modify: `src/server/WechatRobot.Worker/Jobs/DurableJobWorker.cs`
- Modify: `src/server/WechatRobot.Worker/Program.cs`
- Test: `tests/server/WechatRobot.UnitTests/PrivateChat/PrivateChatCommandParserTests.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateChatAnswerPipelineTests.cs`

**Interfaces:**
- Produces:

```csharp
public enum PrivateChatMessageKind { Question, DirectKnowledgeIngest, UnsupportedIngest }
public sealed record PrivateChatCommand(PrivateChatMessageKind Kind, string Body);
public interface IPrivateChatProcessor
{
    Task ProcessAsync(LeasedDurableJob job, CancellationToken cancellationToken);
}
```

- [ ] **Step 1: 写严格首行和全知识范围测试**

覆盖 roomType 4 严格首行命令、roomType 2 不支持说明、正文中间标记按问题处理、
空正文不入库、普通私聊不使用群标签过滤、机器人回复进入同一私聊上下文。

- [ ] **Step 2: 运行测试并确认失败**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj -- --filter-class "*PrivateChatCommandParserTests"
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*PrivateChatAnswerPipelineTests"
```

Expected: FAIL，私聊处理器尚不存在。

- [ ] **Step 3: 实现解析、全知识检索和 Agent 问答**

`PrivateChatRuntimeMode=AgentFramework` 时调用 Private Chat Agent；检索范围使用
全部启用且已发布知识，不读取群绑定。输出继续经过现有防火墙、会话持久化、
发送队列和安全失败文本。

- [ ] **Step 4: 接入 Durable Job Worker**

`DurableJobWorker` 对 `ProcessPrivateMessage` 调用 `IPrivateChatProcessor`。模型调用
和发送均在 Worker，不在回调请求中执行。

- [ ] **Step 5: 运行测试**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj -- --filter-class "*PrivateChatCommandParserTests"
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*PrivateChatAnswerPipelineTests"
```

Expected: PASS；一次私聊问题只产生一次回复。

- [ ] **Step 6: 提交私聊问答**

```powershell
git add src/server/WechatRobot.Application/PrivateChat src/server/WechatRobot.Infrastructure/Agents/PrivateChatAgent.cs src/server/WechatRobot.Application/Knowledge/IKnowledgeTagScopeResolver.cs src/server/WechatRobot.Application/Conversations/GroundedAnswerService.cs src/server/WechatRobot.Worker/Jobs/DurableJobWorker.cs src/server/WechatRobot.Worker/Program.cs tests/server/WechatRobot.UnitTests/PrivateChat tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateChatAnswerPipelineTests.cs
git commit -m "feat: answer worktool private messages"
```

### Task 10: 增加知识来源、版本沿革和私聊入库批次

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/KnowledgeDocumentVersionEntity.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/KnowledgeTagEntity.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Entities/PrivateKnowledgeIngestBatchEntity.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Entities/PrivateKnowledgeIngestItemEntity.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Configurations/PrivateKnowledgeIngestConfigurations.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/WechatRobotDbContext.cs`
- Create: `src/server/WechatRobot.Application/PrivateChat/PrivateKnowledgeIngestContracts.cs`
- Create: `src/server/WechatRobot.Application/PrivateChat/IPrivateKnowledgeIngestStore.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/PrivateKnowledgeIngestStore.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/20260729040000_AddPrivateKnowledgeIngest.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/WechatRobotDbContextModelSnapshot.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateKnowledgeIngestMySqlTests.cs`

**Interfaces:**
- Produces:

```csharp
public enum KnowledgeSourceKind { DocumentUpload, ConversationReview, PrivateChatDirect, LegacyUnknown }
public enum KnowledgeChangeKind { New, Duplicate, Supplement, Correction }
public enum PrivateKnowledgeIngestStatus
{
    Received, Extracting, Comparing, Staged, Indexing, Activated, Retryable, Failed
}
```

- [ ] **Step 1: 写来源回填、唯一批次和版本沿革测试**

断言 `SourceConversationMessageId` 唯一；Supplement/Correction 必须有
`SupersedesVersionId`；系统全局标签按 `SystemKind=GlobalKnowledge` 幂等创建，
不能依赖显示名称。

- [ ] **Step 2: 运行测试并确认失败**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*PrivateKnowledgeIngestMySqlTests"
```

Expected: FAIL，表和字段尚不存在。

- [ ] **Step 3: 实现实体、映射、Store 和迁移**

知识版本增加 `SourceKind`、`SourceConversationMessageId`、`ChangeKind`、
`SupersedesVersionId`；批次和条目字段按批准规格完整落库。已有版本可证明来源时
回填 DocumentUpload 或 ConversationReview，否则使用 `LegacyUnknown`。

- [ ] **Step 4: 检查迁移并运行 MySQL 测试**

Run:

```powershell
rg -n "CHECK|DROP TABLE|DROP COLUMN" src/server/WechatRobot.Infrastructure/Persistence/Migrations/*AddPrivateKnowledgeIngest.cs
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*PrivateKnowledgeIngestMySqlTests"
```

Expected: 无破坏性 SQL，测试 PASS。

- [ ] **Step 5: 提交入库数据模型**

```powershell
git add src/server/WechatRobot.Infrastructure/Persistence/Entities src/server/WechatRobot.Infrastructure/Persistence/Configurations/PrivateKnowledgeIngestConfigurations.cs src/server/WechatRobot.Infrastructure/Persistence/WechatRobotDbContext.cs src/server/WechatRobot.Infrastructure/Persistence/PrivateKnowledgeIngestStore.cs src/server/WechatRobot.Infrastructure/Persistence/Migrations src/server/WechatRobot.Application/PrivateChat tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateKnowledgeIngestMySqlTests.cs
git commit -m "feat: persist private knowledge ingestion"
```

### Task 11: 实现自动拆分、标签解析、相似比较、索引和原子激活

**Files:**
- Create: `src/server/WechatRobot.Application/PrivateChat/PrivateKnowledgeIngestService.cs`
- Create: `src/server/WechatRobot.Application/PrivateChat/IPrivateKnowledgeProposalAgent.cs`
- Create: `src/server/WechatRobot.Infrastructure/Agents/PrivateKnowledgeProposalAgent.cs`
- Create: `src/server/WechatRobot.Worker/Jobs/PrivateKnowledgeIngestWorker.cs`
- Modify: `src/server/WechatRobot.Worker/Jobs/DurableJobWorker.cs`
- Modify: `src/server/WechatRobot.Application/Knowledge/KnowledgeIndexService.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/KnowledgeDocumentStore.cs`
- Test: `tests/server/WechatRobot.UnitTests/PrivateChat/PrivateKnowledgeIngestServiceTests.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateKnowledgeActivationTests.cs`

**Interfaces:**
- Consumes: `IPrivateKnowledgeIngestStore`、现有向量检索、标签 Store 和索引任务。
- Produces:

```csharp
public sealed record ProposedKnowledgeItem(
    string Question, string Answer, IReadOnlyList<string> ExplicitTags,
    Guid? SuggestedTagId, Guid? SimilarVersionId, KnowledgeChangeKind ChangeKind);
public interface IPrivateKnowledgeProposalAgent
{
    Task<IReadOnlyList<ProposedKnowledgeItem>> ProposeAsync(
        string sourceText, CancellationToken cancellationToken);
}
```

- [ ] **Step 1: 写最多 20 条、标签策略和四类变更测试**

覆盖显式标签精确匹配、语义匹配、新建显式标签、无标签全局回退、Duplicate 不发布、
New 新建、Supplement/Correction 新版本、目标版本变化后重新比较或安全失败。

- [ ] **Step 2: 写批量激活失败保护测试**

模拟第二条索引失败；断言旧版本保持有效、所有新版本不可见、暂存和失败码保留。
全部成功时断言一个 MySQL 事务中统一激活并建立 `SupersedesVersionId`。

- [ ] **Step 3: 运行测试并确认失败**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj -- --filter-class "*PrivateKnowledgeIngestServiceTests"
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*PrivateKnowledgeActivationTests"
```

Expected: FAIL，服务和 Worker 尚不存在。

- [ ] **Step 4: 实现提议 Agent 和后端重校验**

Agent 只能调用 `list_active_knowledge_tags`、`find_similar_knowledge`、
`propose_knowledge_items`、`propose_tag_matches`。应用服务重新验证数量、长度、
标签状态、当前版本、相似目标和批次幂等；Agent 不直接写数据库。

- [ ] **Step 5: 实现两阶段通知和可靠 Worker**

收到合法命令后先通过现有发送队列发送“已收到，正在整理”；最终激活后发送
New/Duplicate/Supplement/Correction 统计。失败通知不得写“已入库”；通知失败不
回滚已激活知识，由现有发送重试/死信观察。

- [ ] **Step 6: 运行单元和集成测试**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj -- --filter-class "*PrivateKnowledgeIngestServiceTests"
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*PrivateKnowledgeActivationTests"
```

Expected: PASS。

- [ ] **Step 7: 提交私聊入库流水线**

```powershell
git add src/server/WechatRobot.Application/PrivateChat src/server/WechatRobot.Infrastructure/Agents/PrivateKnowledgeProposalAgent.cs src/server/WechatRobot.Worker/Jobs/PrivateKnowledgeIngestWorker.cs src/server/WechatRobot.Worker/Jobs/DurableJobWorker.cs src/server/WechatRobot.Application/Knowledge/KnowledgeIndexService.cs src/server/WechatRobot.Infrastructure/Persistence/KnowledgeDocumentStore.cs tests/server/WechatRobot.UnitTests/PrivateChat tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateKnowledgeActivationTests.cs
git commit -m "feat: ingest knowledge from private chat"
```

### Task 12: 增加私聊批次、会话和知识来源管理界面

**Files:**
- Create: `src/server/WechatRobot.Api/PrivateChat/PrivateKnowledgeIngestEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Audit/ConversationAuditEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Knowledge/DocumentEndpoints.cs`
- Create: `src/web/wechatrobot-admin/src/api/privateKnowledgeIngest.ts`
- Create: `src/web/wechatrobot-admin/src/views/knowledge/PrivateKnowledgeIngestView.vue`
- Create: `src/web/wechatrobot-admin/src/views/knowledge/PrivateKnowledgeIngestView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/audit/ConversationAuditView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentManagementView.vue`
- Modify: `src/web/wechatrobot-admin/src/router/index.ts`
- Modify: `src/web/wechatrobot-admin/src/layouts/AdminLayout.vue`
- Test: `tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateKnowledgeIngestEndpointTests.cs`

**Interfaces:**
- Produces: 私聊批次分页/脱敏详情/受控重试 API，会话渠道筛选和知识版本来源字段。

- [ ] **Step 1: 写后端脱敏、授权和重试端点测试**

断言列表不包含原始提示词和模型响应；只有 Retryable/Failed 批次可受控重试；
同一来源消息重试复用同一批次。

- [ ] **Step 2: 写前端加载、筛选、来源和错误状态测试**

会话审计显示“私聊兼容身份”说明；知识版本显示来源、来源时间、变更类型和被替代
版本；批次页显示统计、失败码和重试。

- [ ] **Step 3: 实现薄 API 和管理页面**

所有群筛选继续用群名称选择器；私聊身份不显示稳定成员 ID。页面包含加载、空、
失败和重试成功状态。

- [ ] **Step 4: 运行后端和前端测试**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*PrivateKnowledgeIngestEndpointTests"
Push-Location src/web/wechatrobot-admin
npm test -- --run src/views/knowledge/PrivateKnowledgeIngestView.spec.ts src/views/audit/ConversationAuditView.spec.ts
npm run typecheck
npm run build
Pop-Location
```

Expected: PASS。

- [ ] **Step 5: 提交运营界面**

```powershell
git add src/server/WechatRobot.Api/PrivateChat src/server/WechatRobot.Api/Audit/ConversationAuditEndpoints.cs src/server/WechatRobot.Api/Knowledge/DocumentEndpoints.cs src/web/wechatrobot-admin/src/api/privateKnowledgeIngest.ts src/web/wechatrobot-admin/src/views/knowledge/PrivateKnowledgeIngestView* src/web/wechatrobot-admin/src/views/audit/ConversationAuditView.vue src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentManagementView.vue src/web/wechatrobot-admin/src/router/index.ts src/web/wechatrobot-admin/src/layouts/AdminLayout.vue tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateKnowledgeIngestEndpointTests.cs
git commit -m "feat: add private knowledge operations ui"
```

## Milestone D：群消息意图判断和回答执行渐进迁移

### Task 13: 建立独立原始群消息窗口和 MessageIntentAgent

**Files:**
- Create: `src/server/WechatRobot.Application/Agents/MessageIntentContracts.cs`
- Create: `src/server/WechatRobot.Application/Agents/IMessageIntentAgent.cs`
- Create: `src/server/WechatRobot.Application/Conversations/IntentContextWindowBuilder.cs`
- Create: `src/server/WechatRobot.Infrastructure/Agents/MessageIntentAgent.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/ConversationMessageEntity.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/RetrievalAuditEntity.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/20260729050000_AddIntentDecisionAudit.cs`
- Test: `tests/server/WechatRobot.UnitTests/Agents/MessageIntentAgentTests.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Conversations/MessageIntentIsolationTests.cs`

**Interfaces:**
- Produces:

```csharp
public enum IntentDecision { Reply, NoReply, Uncertain }
public enum IntentCategory
{
    DirectedToBot, FollowUpToBot, HumanConversation, SocialChatter, Uncertain
}
public sealed record MessageIntentResult(
    IntentDecision Decision, IntentCategory Category, string ReasonCode,
    decimal Confidence, string? FailureCode);
```

- [ ] **Step 1: 写结构化输出、终态工具和失败关闭测试**

优先验证 JSON Schema；不支持时只注册
`submit_intent_decision(decision, category, reasonCode, confidence)`。两者都不支持、
自由文本、非法枚举、低置信、超时和未知异常全部归一化为不回复。

- [ ] **Step 2: 写原始窗口隔离测试**

断言窗口只含当前群有限原始消息，不含知识、记忆、摘要和网页；未回复成员消息可
进入窗口但不进入正式 AI 会话；不得出现 `quotesBotMessage` 或
`replyToMessageRef`。

- [ ] **Step 3: 运行测试并确认失败**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj -- --filter-class "*MessageIntentAgentTests"
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*MessageIntentIsolationTests"
```

Expected: FAIL，意图 Agent 尚不存在。

- [ ] **Step 4: 实现有限窗口和无业务工具 Agent**

输入字段固定为消息内部引用、当前文本、atMe、有限最近消息及服务端计算信号。
Agent 不注册知识、搜索、记忆、配置、模板或发送工具；自由解释在持久化前丢弃。

- [ ] **Step 5: 运行测试**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj -- --filter-class "*MessageIntentAgentTests"
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*MessageIntentIsolationTests"
```

Expected: PASS。

- [ ] **Step 6: 提交意图 Agent**

```powershell
git add src/server/WechatRobot.Application/Agents src/server/WechatRobot.Application/Conversations/IntentContextWindowBuilder.cs src/server/WechatRobot.Infrastructure/Agents/MessageIntentAgent.cs src/server/WechatRobot.Infrastructure/Persistence/Entities/ConversationMessageEntity.cs src/server/WechatRobot.Infrastructure/Persistence/Entities/RetrievalAuditEntity.cs src/server/WechatRobot.Infrastructure/Persistence/Migrations tests/server/WechatRobot.UnitTests/Agents/MessageIntentAgentTests.cs tests/server/WechatRobot.IntegrationTests/Conversations/MessageIntentIsolationTests.cs
git commit -m "feat: add isolated message intent agent"
```

### Task 14: 增加四类运行模式、Shadow 诊断和灰度接管

**Files:**
- Create: `src/server/WechatRobot.Application/Agents/AgentRuntimeModes.cs`
- Modify: `src/server/WechatRobot.Application/Messaging/InboundMessageProcessor.cs`
- Modify: `src/server/WechatRobot.Api/Groups/GroupEndpoints.cs`
- Create: `src/server/WechatRobot.Api/Agents/AgentDiagnosticsEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Program.cs`
- Modify: `src/web/wechatrobot-admin/src/api/groups.ts`
- Create: `src/web/wechatrobot-admin/src/api/agentDiagnostics.ts`
- Modify: `src/web/wechatrobot-admin/src/components/groups/GroupAdvancedSettingsPanel.vue`
- Create: `src/web/wechatrobot-admin/src/views/operations/AgentDiagnosticsView.vue`
- Create: `src/web/wechatrobot-admin/src/views/operations/AgentDiagnosticsView.spec.ts`
- Test: `tests/server/WechatRobot.IntegrationTests/Conversations/IntentRuntimeModeTests.cs`

**Interfaces:**
- Produces:

```csharp
public enum IntentRuntimeMode { Legacy, Shadow, AgentFramework, Paused }
public enum AnswerRuntimeMode { Legacy, Shadow, AgentFramework }
public enum PrivateChatRuntimeMode { Disabled, AgentFramework }
public enum TemplateRoutingRuntimeMode { Disabled, Shadow, AgentFramework }
```

- [ ] **Step 1: 写四模式独立性和 Shadow 不发送测试**

断言私聊/模板可 AgentFramework、Intent 可 Shadow、Answer 可 Legacy；Shadow
只写差异审计；Paused 保存入站但不调用模板/RAG/发送；Intent 正式失败不回退。

- [ ] **Step 2: 运行测试并确认失败**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*IntentRuntimeModeTests"
```

Expected: FAIL，运行模式尚不存在。

- [ ] **Step 3: 实现运行模式和处理顺序**

正式顺序固定为：技术过滤 -> MessageIntentAgent -> ReplySelected ->
TemplateRoutingAgent -> 现有 RAG。Intent 的 NoReply/Uncertain/Failure 终止链路；
接管后的运维回退只能设置 Paused。

- [ ] **Step 4: 实现诊断 API 和页面**

诊断仅显示稳定 reasonCode、category、confidence、失败码、模式、模型配置版本和
耗时，不显示提示词、原始响应和凭据。页面支持群名称、模式、判断和时间筛选。

- [ ] **Step 5: 运行验证**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*IntentRuntimeModeTests"
Push-Location src/web/wechatrobot-admin
npm test -- --run src/views/operations/AgentDiagnosticsView.spec.ts
npm run typecheck
npm run build
Pop-Location
```

Expected: PASS。

- [ ] **Step 6: 提交运行模式和诊断**

```powershell
git add src/server/WechatRobot.Application/Agents/AgentRuntimeModes.cs src/server/WechatRobot.Application/Messaging/InboundMessageProcessor.cs src/server/WechatRobot.Api/Groups/GroupEndpoints.cs src/server/WechatRobot.Api/Agents src/server/WechatRobot.Api/Program.cs src/web/wechatrobot-admin/src/api/groups.ts src/web/wechatrobot-admin/src/api/agentDiagnostics.ts src/web/wechatrobot-admin/src/components/groups/GroupAdvancedSettingsPanel.vue src/web/wechatrobot-admin/src/views/operations/AgentDiagnosticsView* tests/server/WechatRobot.IntegrationTests/Conversations/IntentRuntimeModeTests.cs
git commit -m "feat: add shadow and gated intent routing"
```

### Task 15: 按回答来源逐支迁移 AnswerAgent

**Files:**
- Create: `src/server/WechatRobot.Application/Agents/IAnswerAgent.cs`
- Create: `src/server/WechatRobot.Application/Agents/AnswerAgentContracts.cs`
- Create: `src/server/WechatRobot.Infrastructure/Agents/AnswerAgent.cs`
- Modify: `src/server/WechatRobot.Application/Conversations/GroundedAnswerService.cs`
- Create: `src/server/WechatRobot.Infrastructure/Agents/ConversationContextProvider.cs`
- Create: `src/server/WechatRobot.Infrastructure/Agents/MemoryContextProvider.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Conversations/AnswerAgentEquivalenceTests.cs`
- Test: `tests/server/WechatRobot.UnitTests/Agents/ContextProviderIsolationTests.cs`

**Interfaces:**
- Produces:

```csharp
public interface IAnswerAgent
{
    Task<AnswerDecision> AnswerAsync(
        GroundedAnswerRequest request, CancellationToken cancellationToken);
}
```

- [ ] **Step 1: 写 Legacy 与 AgentFramework 等价测试**

同一固定数据分别执行两种模式，逐支断言知识命中、Web Search、模型知识降级的
AnswerSource、证据、来源链接、防火墙、失败码和发送幂等一致。

- [ ] **Step 2: 写 Context Provider 隔离测试**

短期上下文和长期记忆仅从服务端提供；作用域不能跨群/跨私聊；token 裁剪沿用
现有规则；记忆提取、晋升和审核 Worker 不迁移。

- [ ] **Step 3: 运行测试并确认失败**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*AnswerAgentEquivalenceTests"
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj -- --filter-class "*ContextProviderIsolationTests"
```

Expected: FAIL，AnswerAgent 尚不存在。

- [ ] **Step 4: 先迁移知识命中分支并运行等价测试**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-method "*AnswerAgentEquivalenceTests*Knowledge*"
```

Expected: PASS 后才继续下一分支。

- [ ] **Step 5: 迁移 Web Search 分支并运行等价测试**

保留 Z.AI 特有 Web Search 合同和来源解析，不以通用 Agent 工具替代未经验证的
提供商行为。

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-method "*AnswerAgentEquivalenceTests*WebSearch*"
```

Expected: PASS。

- [ ] **Step 6: 迁移模型知识降级并运行完整等价测试**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*AnswerAgentEquivalenceTests"
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj -- --filter-class "*ContextProviderIsolationTests"
```

Expected: PASS。

- [ ] **Step 7: 提交 AnswerAgent 等价迁移**

```powershell
git add src/server/WechatRobot.Application/Agents src/server/WechatRobot.Infrastructure/Agents/AnswerAgent.cs src/server/WechatRobot.Infrastructure/Agents/ConversationContextProvider.cs src/server/WechatRobot.Infrastructure/Agents/MemoryContextProvider.cs src/server/WechatRobot.Application/Conversations/GroundedAnswerService.cs tests/server/WechatRobot.IntegrationTests/Conversations/AnswerAgentEquivalenceTests.cs tests/server/WechatRobot.UnitTests/Agents/ContextProviderIsolationTests.cs
git commit -m "feat: migrate answer execution to agent framework"
```

### Task 16: 全量验证、运行验收和运维文档

**Files:**
- Create: `docs/runbooks/agent-framework-private-chat-fixed-replies-rollout.md`
- Modify: `docs/runbooks/deployment.md`
- Modify: `docs/superpowers/specs/2026-07-29-private-chat-direct-knowledge-and-fixed-reply-agent-design.md`
- Modify: `docs/superpowers/specs/2026-07-29-agent-framework-intelligent-reply-migration-design.md`

**Interfaces:**
- Consumes: Tasks 1-15 的全部交付。
- Produces: 可执行的 Shadow、灰度、Paused 回退和上线检查清单。

- [ ] **Step 1: 运行完整后端验证**

Run:

```powershell
dotnet build WechatRobot.sln
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj
```

Expected: 全部 PASS，0 编译错误。

- [ ] **Step 2: 运行完整前端和端到端验证**

Run:

```powershell
Push-Location src/web/wechatrobot-admin
npm run typecheck
npm test -- --run
npm run build
Pop-Location
Push-Location tests/e2e
npm test
Pop-Location
```

Expected: 全部 PASS。

- [ ] **Step 3: 使用 `.local` 重建并启动 API、Worker、前端**

设置 `WECHATROBOT_ENV_FILE` 为 `.local/.env` 绝对路径，并以 `.local` 为 API 和
Worker 工作目录。验证：

```powershell
Invoke-RestMethod http://127.0.0.1:5268/health/live
Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5173/
```

Expected: liveness 为 healthy，前端 HTTP 200；再使用认证调用 readiness，确认
MySQL、Qdrant、OCR、OSS 和 Worker 心跳符合各自必需性。

- [ ] **Step 4: 执行授权测试流量验收**

按顺序验证：明确模板命中、模糊模板走 RAG、roomType 2 私聊问答、roomType 4
私聊入库、重复回调幂等、批次索引失败保护、Intent Shadow 不发送、授权群正式
接管、Intent 失败不回复、Paused 停止新回复。

- [ ] **Step 5: 编写 Runbook**

Runbook 必须写明四种模式的合法组合、能力探针前置条件、Shadow 数据查看方式、
灰度发布门槛、Paused 回退、私聊批次重试、固定模板停用和不可删除的持久化表。

- [ ] **Step 6: 最终差异和敏感信息检查**

Run:

```powershell
git diff --check
git diff --stat
git diff | Select-String -Pattern "api[_-]?key|secret|password|Bearer\s"
```

Expected: 无格式错误，无真实密钥、密码、令牌、连接字符串或机器人标识。

- [ ] **Step 7: 提交验收文档**

```powershell
git add docs/runbooks/agent-framework-private-chat-fixed-replies-rollout.md docs/runbooks/deployment.md docs/superpowers/specs/2026-07-29-private-chat-direct-knowledge-and-fixed-reply-agent-design.md docs/superpowers/specs/2026-07-29-agent-framework-intelligent-reply-migration-design.md
git commit -m "docs: add agent framework rollout runbook"
```

## 发布门槛

- 私聊和模板正式启用前，默认聊天模型的 Function Tool 与工具结果循环探针必须通过。
- MessageIntentAgent 正式接管前，必须有人工标注样本、Shadow 差异报告和授权测试群验证。
- AnswerAgent 每个回答来源分支必须分别通过 Legacy 等价测试后才能扩大灰度。
- 任意正式 Agent 模式发生不可接受异常时使用对应 Disabled 或 Intent Paused；不得改变已有数据、绕过发送队列或自动回退到扩大回复范围的策略。
- 只有所有调用方迁移完成、灰度窗口结束、指标达标且 Runbook 更新后，才允许另立任务删除 Legacy 模型执行代码；本计划不删除 Qdrant、知识库、记忆中心、Durable Job 或发送 Worker。
