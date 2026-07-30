# 私聊匹配全部固定回复模板实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 `roomType=2/4` 普通私聊在知识回答前通过 Agent Framework 匹配全部已启用、未删除固定回复模板。

**Architecture:** 为固定模板存储和路由 Agent 增加显式私聊入口，私聊入口不接收或伪造 `GroupProfileId`，并忽略模板的群包含、排除和群启用状态。`PrivateChatProcessor` 在普通问答前调用私聊模板路由，命中后重新解析模板并复用现有私聊回复、审计和发送链；其他结果继续 `IAnswerAgent`。

**Tech Stack:** ASP.NET Core 10、EF Core、Microsoft Agent Framework、xUnit v3 / Microsoft Testing Platform。

## Global Constraints

- 私聊候选包含全部已启用、未删除的全局和指定群模板。
- 私聊不应用群包含、排除或群启用状态。
- 不改变群聊模板作用域和现有 `RouteAsync(Guid groupProfileId, ...)` 行为。
- 不使用 `Guid.Empty` 伪造群 ID。
- `#知识入库` 和外部联系人不支持入库提示不进入模板路由。
- 模板命中后必须重新校验 ID、版本、启用和删除状态。
- 固定回复审计与群聊一致使用 `AnswerSource = fixed_template`。
- 路由未命中或失败时继续现有 `IAnswerAgent`、全知识、Web Search 和大模型知识链。
- 不新增数据库迁移或后台配置。
- 未经用户明确授权，不提交、不暂存、不推送。

---

## File Structure

- Modify: `src/server/WechatRobot.Application/FixedReplies/IFixedReplyTemplateStore.cs`
  - 声明私聊候选和私聊解析存储合同。
- Modify: `src/server/WechatRobot.Application/FixedReplies/FixedReplyTemplateService.cs`
  - 对私聊存储合同应用现有候选数和示例数上限。
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/FixedReplyTemplateStore.cs`
  - 查询全部有效模板并按 ID、版本重新解析，不检查群状态或群规则。
- Create: `tests/server/WechatRobot.IntegrationTests/FixedReplies/PrivateFixedReplyTemplateStoreTests.cs`
  - 验证私聊候选范围和重新解析安全边界。
- Modify: `src/server/WechatRobot.Application/FixedReplies/ITemplateRoutingAgent.cs`
  - 声明无群 ID 的 `RoutePrivateAsync`。
- Modify: `src/server/WechatRobot.Infrastructure/Agents/TemplateRoutingAgent.cs`
  - 复用现有 Agent Framework 路由，实现私聊候选与私聊解析分支。
- Create: `tests/server/WechatRobot.IntegrationTests/FixedReplies/PrivateTemplateRoutingAgentTests.cs`
  - 验证私聊路由调用私聊候选入口而不是群入口。
- Modify: `src/server/WechatRobot.Infrastructure/Agents/PrivateChatProcessor.cs`
  - 在普通私聊回答前接入模板路由、解析、固定回复审计和安全降级。
- Modify: `tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateChatProcessorTests.cs`
  - 覆盖两种房间类型、命中、未命中、命令旁路和审计。

### Task 1: 增加私聊模板候选与解析合同

**Files:**

- Modify: `src/server/WechatRobot.Application/FixedReplies/IFixedReplyTemplateStore.cs`
- Modify: `src/server/WechatRobot.Application/FixedReplies/FixedReplyTemplateService.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/FixedReplyTemplateStore.cs`
- Modify: `tests/server/WechatRobot.UnitTests/FixedReplies/FixedReplyTemplateServiceTests.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/FixedReplies/PrivateFixedReplyTemplateStoreTests.cs`

**Interfaces:**

- Produces:

```csharp
Task<IReadOnlyList<EffectiveFixedReply>> ListEffectiveForPrivateAsync(
    int maximumCandidates,
    int examplesPerTemplate,
    CancellationToken cancellationToken);

Task<ResolvedFixedReply?> ResolveForPrivateAsync(
    Guid templateId,
    int expectedVersion,
    CancellationToken cancellationToken);
```

- [ ] **Step 1: 写存储失败测试**

在 InMemory 数据库中创建：

- 已启用全局模板。
- 已启用指定群模板。
- 被当前群排除的已启用全局模板。
- 仅包含停用群的已启用指定群模板。
- 停用模板。
- 已删除模板。

调用 `ListEffectiveForPrivateAsync`，断言前四个全部返回，后两个不返回，并验证
优先级降序、ID 稳定排序和每个模板示例数量上限。

再调用 `ResolveForPrivateAsync`，断言：

```csharp
Assert.NotNull(await store.ResolveForPrivateAsync(enabled.Id, enabled.Version, token));
Assert.Null(await store.ResolveForPrivateAsync(enabled.Id, enabled.Version + 1, token));
Assert.Null(await store.ResolveForPrivateAsync(disabled.Id, disabled.Version, token));
Assert.Null(await store.ResolveForPrivateAsync(deleted.Id, deleted.Version, token));
```

- [ ] **Step 2: 运行并确认 RED**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*PrivateFixedReplyTemplateStoreTests' --minimum-expected-tests 1
```

Expected: 编译失败，因为私聊存储合同尚不存在。

- [ ] **Step 3: 实现最小私聊查询**

`FixedReplyTemplateStore.ListEffectiveForPrivateAsync` 只使用：

```csharp
.Where(item => item.IsEnabled && item.DeletedAtUtc == null)
.OrderByDescending(item => item.Priority)
.ThenBy(item => item.Id)
.Take(maximumCandidates)
```

复用现有示例读取和 `EffectiveFixedReply` 映射。

`ResolveForPrivateAsync` 直接按模板 ID、预期版本、启用和未删除条件查询，并返回
`ResolvedFixedReply`；不得调用 `GroupIsActiveAsync` 或群规则查询。

- [ ] **Step 4: 在服务层转发并限制参数**

`FixedReplyTemplateService` 增加同名方法，分别把候选数限制到 `1..64`、示例数
限制到 `1..10`。更新 `RecordingStore` 以实现新合同。

- [ ] **Step 5: 运行并确认 GREEN**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*PrivateFixedReplyTemplateStoreTests' --minimum-expected-tests 1
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*FixedReplyTemplateServiceTests' --minimum-expected-tests 1
```

Expected: 私聊存储和服务测试通过。

### Task 2: 增加 Template Routing Agent 私聊入口

**Files:**

- Modify: `src/server/WechatRobot.Application/FixedReplies/ITemplateRoutingAgent.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Agents/TemplateRoutingAgent.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/FixedReplies/PrivateTemplateRoutingAgentTests.cs`

**Interfaces:**

- Consumes: Task 1 的 `ListEffectiveForPrivateAsync` 和
  `ResolveForPrivateAsync`。
- Produces:

```csharp
Task<TemplateRouteDecision> RoutePrivateAsync(
    string message,
    CancellationToken cancellationToken);
```

- [ ] **Step 1: 写路由入口失败测试**

使用记录型 `IFixedReplyTemplateStore`，让私聊候选返回空数组。创建
`TemplateRoutingAgent` 后调用 `RoutePrivateAsync`，断言：

```csharp
Assert.True(store.PrivateListCalled);
Assert.False(store.GroupListCalled);
Assert.Equal(
    "fixed_reply_no_candidates",
    Assert.IsType<ContinueKnowledgeAnswer>(result).FailureCode);
```

模型客户端工厂使用抛错替身，以证明无候选时不会调用模型。

- [ ] **Step 2: 运行并确认 RED**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*PrivateTemplateRoutingAgentTests' --minimum-expected-tests 1
```

Expected: 编译失败，因为 `RoutePrivateAsync` 尚不存在。

- [ ] **Step 3: 实现共享路由核心**

保留现有群入口。增加私聊入口，并让两个入口把各自候选与解析委托传入同一个
私有 Agent Framework 路由方法。私聊分支只能调用：

```csharp
templates.ListEffectiveForPrivateAsync(...)
templates.ResolveForPrivateAsync(...)
```

现有工具数量、参数、候选 ID/版本、防异常和取消语义保持不变。

- [ ] **Step 4: 运行并确认 GREEN**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*PrivateTemplateRoutingAgentTests' --minimum-expected-tests 1
```

Expected: 私聊路由入口测试通过。

### Task 3: 在 PrivateChatProcessor 中优先处理固定模板

**Files:**

- Modify: `src/server/WechatRobot.Infrastructure/Agents/PrivateChatProcessor.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateChatProcessorTests.cs`

**Interfaces:**

- Consumes: `ITemplateRoutingAgent.RoutePrivateAsync`。
- Consumes: `FixedReplyTemplateService.ResolveForPrivateAsync`。
- Produces: 私聊固定回复、审计与知识回答降级。

- [ ] **Step 1: 写两种房间类型的模板命中失败测试**

为 `roomType=2/4` 创建普通私聊、默认模型和一个有效模板。路由替身返回
`MatchFixedTemplate(template.Id, template.Version)`，`IAnswerAgent` 使用抛错
替身。

断言：

```csharp
Assert.Equal("固定回复正文", outbound.Text);
Assert.Equal("fixed_template", audit.AnswerSource);
Assert.Equal(template.Id, audit.FixedReplyTemplateId);
Assert.Equal(template.Version, audit.FixedReplyTemplateVersion);
Assert.Equal(1, router.PrivateCallCount);
```

- [ ] **Step 2: 写未命中和命令旁路失败测试**

- 普通私聊路由返回 `ContinueKnowledgeAnswer` 时，断言继续调用
  `IAnswerAgent`。
- `#知识入库` 命令和 `roomType=2` 不支持入库提示断言
  `router.PrivateCallCount == 0`。

- [ ] **Step 3: 运行并确认 RED**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*PrivateChatProcessorTests' --minimum-expected-tests 1
```

Expected: 编译失败或行为失败，因为处理器尚未依赖模板路由和模板服务。

- [ ] **Step 4: 接入私聊模板路由**

在 `PrivateChatProcessor` 的可选依赖中增加 `ITemplateRoutingAgent` 和
`FixedReplyTemplateService`。普通问题取得默认模型后，在读取标签和调用
`IAnswerAgent` 前执行：

```csharp
if (runtime.TemplateRoutingRuntimeMode == TemplateRoutingRuntimeMode.AgentFramework
    && templateRouter is not null
    && fixedReplies is not null)
{
    var route = await templateRouter.RoutePrivateAsync(command.Body, cancellationToken);
    if (route is MatchFixedTemplate match)
    {
        var fixedReply = await fixedReplies.ResolveForPrivateAsync(
            match.TemplateId,
            match.ExpectedVersion,
            cancellationToken);
        if (fixedReply is not null)
        {
            // 使用固定正文、fixed_template 来源和模板 ID/版本调用 ReplyAsync。
            return;
        }
    }
}
```

固定结果使用现有群聊审计字段：

```csharp
new RetrievalAuditDraft(
    [],
    0,
    1,
    "fixed_template",
    "answer",
    AnswerSource: "fixed_template",
    FixedReplyTemplateId: fixedReply.Id,
    FixedReplyTemplateVersion: fixedReply.Version)
```

- [ ] **Step 5: 运行并确认 GREEN**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*PrivateChatProcessorTests' --minimum-expected-tests 1
```

Expected: 两种房间类型的模板命中、未命中降级、审计、发送幂等和命令旁路测试
全部通过。

### Task 4: 回归验证

**Files:**

- Verify: Task 1–3 的全部文件。
- Verify: `tests/server/WechatRobot.IntegrationTests/Messaging/FixedReplyPipelineTests.cs`

- [ ] **Step 1: 运行固定回复与私聊相关测试**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*FixedReply*' '*PrivateChat*' '*AnswerAgentEquivalenceTests' --minimum-expected-tests 1
```

- [ ] **Step 2: 运行完整 Unit 和 Contract**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore
```

- [ ] **Step 3: 构建 IntegrationTests 项目**

```powershell
dotnet build tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore
```

- [ ] **Step 4: 检查差异与秘密**

```powershell
git diff --check
git status --short
```

只检查本任务文件是否出现真实凭据、令牌、连接字符串或上游原始响应。

- [ ] **Step 5: 报告真实模型边界**

如果没有使用 `.local` 的已授权默认模型执行私聊模板匹配，明确报告“真实 Agent
模型路由未在本次验证”，不得用替身测试宣称外部能力已验收。
