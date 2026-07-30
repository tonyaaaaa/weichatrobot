# 私聊免配置回答降级实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 `roomType=2` 和 `roomType=4` 的普通私聊始终检索全部启用知识，并在知识未命中时免群配置执行 Web Search 和大模型知识降级。

**Architecture:** 保留 `PrivateChatProcessor` 当前的全启用标签检索，将其回答依赖改为 Worker 已注册的 `IAnswerAgent -> AnswerAgent`，并传入固定私聊降级策略。`AnswerAgent` 使用 Agent Framework 执行知识与大模型知识回答，供应商原生 Web Search 继续使用已验证的 typed client；不修改群配置、全局默认值或供应商能力判断。

**Tech Stack:** ASP.NET Core 10、EF Core InMemory、xUnit v3 / Microsoft Testing Platform。

## Global Constraints

- `roomType=2` 和 `roomType=4` 使用相同的普通问答策略。
- 所有普通私聊问答必须调用 `IAnswerAgent`，不得直接依赖
  `GroundedAnswerService`。
- 私聊不新增配置项、后台开关、群档案或数据库迁移。
- Web Search 仍只在默认模型明确声明 `ZaiChatCompletions` 能力时调用。
- Web Search 必须同时取得安全答案和合法 HTTP/HTTPS 来源才算成功。
- 搜索不支持、失败或无来源时必须继续大模型自身知识。
- 知识命中但输出安全检查失败时不得绕过到 Web Search。
- 保留现有私聊会话、检索审计、发送幂等、重试和死信链路。
- 不修改或纳入当前工作树中既有的全局知识标签修复和知识文档页面改动。
- 未经用户明确授权，不提交、不暂存、不推送。

---

## File Structure

- Modify: `tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateChatProcessorTests.cs`
  - 用真实 `GroundedAnswerService` 验证两类私聊的固定降级策略、调用顺序、全部标签范围和审计结果。
- Modify: `src/server/WechatRobot.Infrastructure/Agents/PrivateChatProcessor.cs`
  - 依赖 `IAnswerAgent`，定义唯一的固定私聊降级策略，并把它传入
    `GroundedAnswerRequest`。
- Existing verified implementation, no behavioral edit:
  `src/server/WechatRobot.Application/Conversations/GroundedAnswerService.cs`
  - 继续负责知识、Web Search、模型知识和最终无证据编排。

### Task 1: 锁定两类私聊的免配置降级行为

**Files:**

- Modify: `tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateChatProcessorTests.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateChatProcessorTests.cs`

**Interfaces:**

- Consumes: `PrivateChatProcessor.ProcessAsync(LeasedDurableJob, CancellationToken)`。
- Consumes: `IAnswerAgent.AnswerAsync(GroundedAnswerRequest, CancellationToken)`。
- Consumes: `IChatCompletionClient.CompleteAsync(ModelProviderConfiguration, ChatCompletionRequest, CancellationToken)`。
- Produces: 回归测试证明两类私聊都把全部启用标签传入检索，并按 Web Search、模型知识顺序降级。

- [ ] **Step 1: 将普通私聊测试扩展为两个房间类型**

把现有 `Ordinary_private_answer_is_session_bound_audited_and_enqueued_once`
改为带有 `[Theory]`、`[InlineData(2)]` 和 `[InlineData(4)]` 的测试。模型配置设置：

```csharp
WebSearchMode = "ZaiChatCompletions"
```

增加两个启用标签和一个停用标签。检索替身保存
`ResolveScopeAsync` 收到的 `requestedTagIds`，并返回零知识证据。

- [ ] **Step 2: 用顺序型聊天替身表达预期降级**

用 `FallbackChatClient` 替换 `UnusedChatClient`。第一次调用断言
`request.WebSearch` 非空并返回无来源的搜索回答；第二次调用断言
`request.WebSearch` 为空并返回大模型知识回答：

```csharp
private sealed class FallbackChatClient : IChatCompletionClient
{
    public List<ChatCompletionRequest> Requests { get; } = [];

    public Task<ChatCompletionResponse> CompleteAsync(
        ModelProviderConfiguration configuration,
        ChatCompletionRequest request,
        CancellationToken token = default)
    {
        Requests.Add(request);
        return Task.FromResult(
            Requests.Count == 1
                ? new ChatCompletionResponse("搜索回答", [])
                : new ChatCompletionResponse("大模型回答"));
    }
}
```

测试最终断言：

```csharp
Assert.Equal(2, chat.Requests.Count);
Assert.NotNull(chat.Requests[0].WebSearch);
Assert.Null(chat.Requests[1].WebSearch);
// 重复执行后仍按相同顺序计算，但发送命令保持唯一。
Assert.Equal(4, chat.Requests.Count);
Assert.NotNull(chat.Requests[2].WebSearch);
Assert.Null(chat.Requests[3].WebSearch);
Assert.Equal("大模型回答", outbound.Text);
Assert.Equal("model_knowledge", audit.AnswerSource);
Assert.Equal("web_search_no_sources", audit.WebSearchFailureCode);
Assert.Equal(enabledTagIds.Order(), retrieval.RequestedTagIds.Order());
Assert.DoesNotContain(disabledTagId, retrieval.RequestedTagIds);
```

保留现有会话绑定、私聊审计、重复执行只产生一个发送命令的断言，并使用记录型
`IAnswerAgent` 断言两次处理都经过 Answer Agent。

- [ ] **Step 3: 运行测试并确认 RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*PrivateChatProcessorTests' --minimum-expected-tests 1
```

Expected: 两个房间类型都失败，因为当前 `PrivateChatProcessor` 没有传入
`AnswerFallback`，聊天替身不会被调用，输出仍为最终无证据文本。

### Task 2: 为私聊传入固定回答降级策略

**Files:**

- Modify: `src/server/WechatRobot.Infrastructure/Agents/PrivateChatProcessor.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateChatProcessorTests.cs`

**Interfaces:**

- Consumes: `GroupAnswerFallbackSettings`。
- Consumes: Worker 中已有的 `IAnswerAgent -> AnswerAgent` 注册。
- Produces: `PrivateChatProcessor` 的回答依赖为 `IAnswerAgent`，且创建的
  `GroundedAnswerRequest.AnswerFallback` 始终为固定私聊策略。

- [ ] **Step 1: 将回答依赖切换为 Answer Agent**

把 `PrivateChatProcessor` 构造函数的 `GroundedAnswerService` 参数改为
`IAnswerAgent`，普通私聊调用：

```csharp
await answerAgent.AnswerAsync(request, cancellationToken);
```

- [ ] **Step 2: 定义固定私聊策略**

在 `PrivateChatProcessor` 类内增加：

```csharp
private static readonly GroupAnswerFallbackSettings PrivateAnswerFallback = new(
    WebSearchEnabled: true,
    ModelKnowledgeFallbackEnabled: true,
    WebSearchShowSources: true,
    WebSearchResultCount: 5,
    WebSearchRecency: "NoLimit",
    WebSearchDomainFilter: null,
    WebSearchContentSize: "Medium",
    FinalNoEvidencePolicy: "InsufficientEvidence");
```

- [ ] **Step 3: 将策略传给 Answer Agent**

在私聊 `GroundedAnswerRequest` 的具名参数中增加：

```csharp
AnswerFallback: PrivateAnswerFallback,
```

不修改 `GroundedAnswerService` 的默认策略，也不读取任何群配置。

- [ ] **Step 4: 运行聚焦测试并确认 GREEN**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*PrivateChatProcessorTests' --minimum-expected-tests 1
```

Expected: `PrivateChatProcessorTests` 全部通过；两种房间类型均执行
Web Search 后模型知识降级，且只产生一条发送命令。

### Task 3: 回归与差异验证

**Files:**

- Verify: `src/server/WechatRobot.Infrastructure/Agents/PrivateChatProcessor.cs`
- Verify: `tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateChatProcessorTests.cs`
- Verify: `docs/superpowers/specs/2026-07-30-private-chat-unconfigured-answer-fallback-design.md`
- Verify: `docs/superpowers/plans/2026-07-30-private-chat-unconfigured-answer-fallback.md`

**Interfaces:**

- Consumes: Tasks 1–2 的实现。
- Produces: 可交付的测试、构建和差异证据。

- [ ] **Step 1: 运行全部私聊集成测试**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*PrivateChat*' --minimum-expected-tests 1
```

Expected: 所有私聊处理、端点和入库流水线测试通过。

- [ ] **Step 2: 运行统一回答单元测试**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*GroundedAnswerTests' '*AnswerOutputFirewallTests' --minimum-expected-tests 1
```

Expected: 统一知识、Web Search、模型知识和安全失败矩阵保持通过。

- [ ] **Step 3: 编译 Infrastructure 和集成测试项目**

Run:

```powershell
dotnet build src/server/WechatRobot.Infrastructure/WechatRobot.Infrastructure.csproj --no-restore
dotnet build tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore
```

Expected: 两个项目均成功，零编译错误。

- [ ] **Step 4: 检查差异卫生和任务边界**

Run:

```powershell
git diff --check
git status --short
git diff -- src/server/WechatRobot.Infrastructure/Agents/PrivateChatProcessor.cs tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateChatProcessorTests.cs docs/superpowers/specs/2026-07-30-private-chat-unconfigured-answer-fallback-design.md docs/superpowers/plans/2026-07-30-private-chat-unconfigured-answer-fallback.md
```

Expected: 无空白错误；当前任务只新增固定私聊降级策略、回归测试和两份文档。
既有未提交文件仍保持用户原状，不被暂存或提交。

- [ ] **Step 5: 报告外部验证边界**

如果没有使用 `.local` 中已授权且明确支持 Web Search 的真实默认模型执行私聊，
交付说明必须写明“真实供应商 Web Search 未在本次验证”，不得用替身测试宣称
外部联网已验收。
