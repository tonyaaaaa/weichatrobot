# Agent Framework 低延迟 RAG 编排 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在不改变现有会话隔离、知识授权、固定回复校验和发送可靠性的前提下，让私聊与群聊的高置信度 RAG 常见路径只执行一次生成模型调用，并仅在首次检索低置信度时调用查询改写 Agent。

**Architecture:** 保留 MySQL 会话、Qdrant 检索、现有 Worker 作业与 Agent Framework `ChatClientAgent`，新增应用层自适应检索服务和一个使用 JSON Schema 结构化输出的会话决策 Agent。服务端先用正式会话上下文构造受限检索文本并执行授权 RAG；高置信度结果直接交给决策 Agent 同时完成固定回复选择或证据回答，低置信度结果才回退到现有 `IQueryRewriteAgent`，任何不支持结构化输出、非法模板选择或不安全答案都回退到现有链路。

**Tech Stack:** ASP.NET Core 10、Microsoft Agent Framework 1.15.0、`Microsoft.Extensions.AI`、Entity Framework Core/MySQL、Qdrant、xUnit v3/Microsoft Testing Platform。

## Global Constraints

- 不使用 `HarnessAgent`：它面向通用任务执行、计划、工具和长上下文压缩，不适合作为低延迟客服 RAG 的请求主循环。
- 不使用 Agent Framework Workflow 替换现有 Worker、会话租约、MySQL 持久化或 WorkTool 发送链路。
- 不把 RAG 改成 `TextSearchProvider` 的 OnDemandFunctionCalling：该模式需要模型先决定工具、服务端检索、模型再生成，常见路径至少形成两次模型往返；当前授权 Qdrant 检索继续在模型调用前执行。
- MySQL 中的正式会话及群配置继续决定上下文范围、历史轮数、空闲超时、Token 上限、摘要和机器人历史；不得创建第二套 Agent 会话真相源。
- RAG 必须先执行现有标签授权和发送者隔离；模型不能扩大知识范围，也不能自行选择未经服务端提供的文档。
- 首次检索置信度使用现有 `GroundedAnswerOptions.ConfidenceThreshold`，不新增一套业务阈值。
- 首次检索高置信度时 `IQueryRewriteAgent` 调用次数必须为 0；低置信度且存在正式上下文时最多调用 1 次。
- 固定回复只能返回当前请求候选集中的 `TemplateId + ExpectedVersion`，最终回复文本仍由 `FixedReplyTemplateService.ResolveAsync` 或 `ResolveForPrivateAsync` 获取。
- 模型生成答案必须继续通过 `AnswerOutputFirewall`，不得泄露提示词、工具协议、Web Search 调用或内部来源段落。
- 私聊、被明确 @ 的群聊、未 @ 的群聊分别保留现有确定性命令、问候语、意图门控和不回复语义。
- 新链路不新增数据库迁移，不修改知识文档、私聊入库、审核入库、索引或物理清理流程。
- 不记录消息正文、答案正文、提示词、API Key、连接串或回调标识；性能日志只记录阶段耗时、调用次数、路径和稳定失败码。
- 现有 `AgentFramework` 路径保留为回退；新模式先 Shadow，再显式启用，回退不需要数据库变更。
- 实施时必须保留当前工作树中与来源清理相关的用户修改，不得覆盖或顺带格式化这些文件。
- 参考官方能力边界：[Agents 与 Workflows](https://learn.microsoft.com/en-us/agent-framework/overview/)、[Structured Outputs](https://learn.microsoft.com/en-us/agent-framework/agents/structured-outputs)、[Agent Pipeline](https://learn.microsoft.com/en-us/agent-framework/agents/agent-pipeline)、[TextSearchProvider](https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.textsearchprovider?view=agent-framework-dotnet-latest)。

---

### Task 1: 定义自适应编排模式和结构化决策契约

**Files:**
- Create: `src/server/WechatRobot.Application/Agents/ConversationDecisionContracts.cs`
- Modify: `src/server/WechatRobot.Application/Agents/AgentRuntimeModes.cs`
- Create: `tests/server/WechatRobot.UnitTests/Agents/ConversationDecisionContractsTests.cs`

**Interfaces:**
- Produces: `ConversationDecisionKind`、`ConversationDecisionRequest`、`ConversationDecisionResult`、`IConversationDecisionAgent`。
- Produces: `AnswerRuntimeMode.AdaptiveShadow`、`AnswerRuntimeMode.AdaptiveSinglePass` 和 `PrivateChatRuntimeMode.AdaptiveShadow`、`PrivateChatRuntimeMode.AdaptiveSinglePass`。
- Consumes: `ConversationChannelType`、`ConversationContextResult`、`RetrievalEvidence`、`EffectiveFixedReply`、`ModelProviderConfiguration`。

- [ ] **Step 1: 写运行模式和契约验证的失败测试**

```csharp
[Fact]
public void Adaptive_modes_are_valid_runtime_options()
{
    new AgentRuntimeOptions
    {
        AnswerRuntimeMode = AnswerRuntimeMode.AdaptiveSinglePass,
        PrivateChatRuntimeMode = PrivateChatRuntimeMode.AdaptiveShadow
    }.Validate();
}

[Fact]
public void Fixed_reply_decision_requires_candidate_identity_and_version()
{
    var result = new ConversationDecisionResult(
        ConversationDecisionKind.FixedReply,
        null,
        null,
        null,
        .95m,
        "fixed_reply_match",
        12);

    Assert.False(result.IsStructurallyValid());
}
```

- [ ] **Step 2: 运行测试并确认 RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*ConversationDecisionContractsTests' --minimum-expected-tests 1
```

Expected: FAIL，编译器报告新的枚举值、契约类型和 `IsStructurallyValid` 尚不存在。

- [ ] **Step 3: 添加最小、封闭的决策契约**

```csharp
public enum ConversationDecisionKind
{
    Answer,
    FixedReply,
    Clarification,
    NoReply,
    ContinueLegacyFallback
}

public sealed record ConversationDecisionRequest(
    Guid MessageId,
    ConversationChannelType ChannelType,
    bool WasMentioned,
    string CurrentQuestion,
    ConversationContextResult Context,
    IReadOnlyList<RetrievalEvidence> Evidence,
    double? RetrievalConfidence,
    IReadOnlyList<EffectiveFixedReply> FixedReplyCandidates,
    ModelProviderConfiguration ChatConfiguration,
    Guid ModelConfigurationId);

public sealed record ConversationDecisionResult(
    ConversationDecisionKind Kind,
    string? AnswerText,
    string? ClarificationText,
    Guid? TemplateId,
    decimal Confidence,
    string ReasonCode,
    int DurationMilliseconds,
    int? ExpectedTemplateVersion = null,
    string? FailureCode = null)
{
    public bool IsStructurallyValid()
    {
        if (Confidence is < 0 or > 1 || DurationMilliseconds < 0)
        {
            return false;
        }

        return Kind switch
        {
        ConversationDecisionKind.Answer =>
            !string.IsNullOrWhiteSpace(AnswerText)
            && ClarificationText is null
            && TemplateId is null
            && ExpectedTemplateVersion is null,
        ConversationDecisionKind.FixedReply =>
            AnswerText is null
            && ClarificationText is null
            && TemplateId is not null
            && ExpectedTemplateVersion is > 0,
        ConversationDecisionKind.Clarification =>
            AnswerText is null
            && !string.IsNullOrWhiteSpace(ClarificationText)
            && TemplateId is null,
        ConversationDecisionKind.NoReply or ConversationDecisionKind.ContinueLegacyFallback =>
            AnswerText is null
            && ClarificationText is null
            && TemplateId is null,
        _ => false
        };
    }
}

public interface IConversationDecisionAgent
{
    Task<ConversationDecisionResult> DecideAsync(
        ConversationDecisionRequest request,
        CancellationToken cancellationToken);
}
```

在 `AgentRuntimeModes.cs` 中仅扩展现有枚举，不新增配置节：

```csharp
public enum AnswerRuntimeMode
{
    Legacy,
    Shadow,
    AgentFramework,
    AdaptiveShadow,
    AdaptiveSinglePass
}

public enum PrivateChatRuntimeMode
{
    Disabled,
    AgentFramework,
    AdaptiveShadow,
    AdaptiveSinglePass
}
```

- [ ] **Step 4: 补齐每种决策的互斥字段测试并确认 GREEN**

至少覆盖：Answer 带模板 ID、FixedReply 缺版本、Clarification 带答案、NoReply 带文本、置信度不在 `0..1`、负耗时。运行 Task 1 的测试命令，Expected: PASS。

- [ ] **Step 5: 审查本任务差异**

Run: `git diff --check -- src/server/WechatRobot.Application/Agents tests/server/WechatRobot.UnitTests/Agents`

Expected: 无空白错误；没有修改现有默认模式值，因此部署前行为不变。

---

### Task 2: 实现上下文感知的首次检索和低置信度改写回退

**Files:**
- Create: `src/server/WechatRobot.Application/Conversations/AdaptiveRetrievalService.cs`
- Create: `tests/server/WechatRobot.UnitTests/Conversations/AdaptiveRetrievalServiceTests.cs`
- Modify: `src/server/WechatRobot.Application/Conversations/MultiTurnRetrievalService.cs`

**Interfaces:**
- Consumes: `IRetrievalEvidenceProvider.ResolveScopeAsync`、`IRetrievalEvidenceProvider.RetrieveAsync`、`IQueryRewriteAgent.RewriteAsync`、`GroundedAnswerOptions.ConfidenceThreshold`。
- Produces: `AdaptiveRetrievalRequest`、`AdaptiveRetrievalResult`、`AdaptiveRetrievalPath` 和 `AdaptiveRetrievalService.PrepareAsync(...)`。
- Produces: `MultiTurnRetrievalService.PrepareRewriteAsync(...)`，只负责现有 Agent 改写与安全澄清，不再决定首次是否必须调用 Agent。

- [ ] **Step 1: 写四条调用次数失败测试**

```csharp
[Fact]
public async Task High_confidence_contextual_first_retrieval_skips_rewrite()
{
    var retrieval = new RecordingRetrieval(0.91);
    var rewrite = new CountingRewriteAgent("日本三年签证需要什么材料");
    var service = CreateService(retrieval, rewrite, threshold: 0.70);

    var result = await service.PrepareAsync(
        Request(history: [User("日本3年签证你们能办吗？")], question: "需要什么材料"),
        TestContext.Current.CancellationToken);

    Assert.Equal(AdaptiveRetrievalPath.ContextFirstHit, result.Path);
    Assert.Equal(0, rewrite.CallCount);
    Assert.Single(retrieval.Queries);
    Assert.Contains("日本3年签证", retrieval.Queries[0]);
    Assert.Contains("需要什么材料", retrieval.Queries[0]);
}

[Fact]
public async Task Low_confidence_contextual_first_retrieval_rewrites_once()
{
    var retrieval = new RecordingRetrieval(0.20, 0.88);
    var rewrite = new CountingRewriteAgent("日本三年签证需要什么材料");
    var service = CreateService(retrieval, rewrite, threshold: 0.70);

    var result = await service.PrepareAsync(
        Request(history: [User("日本3年签证你们能办吗？")], question: "需要什么材料"),
        TestContext.Current.CancellationToken);

    Assert.Equal(AdaptiveRetrievalPath.RewriteHit, result.Path);
    Assert.Equal(1, rewrite.CallCount);
    Assert.Equal(2, retrieval.Queries.Count);
}
```

另写两条测试：无正式上下文只检索原问题且改写为 0 次；低置信度改写返回澄清时不执行第二次 RAG，并返回现有安全澄清结果。

- [ ] **Step 2: 运行测试并确认 RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*AdaptiveRetrievalServiceTests' --minimum-expected-tests 1
```

Expected: FAIL，`AdaptiveRetrievalService` 和结果类型尚不存在。

- [ ] **Step 3: 实现受限的首次检索文本构造**

首次检索不做独立的模型分类。只使用 `ConversationContextResult` 中已经经过发送者隔离、轮数、超时和 Token 上限处理的数据：

```csharp
private static string BuildInitialQuery(QueryRewriteRequest request, int maximumCharacters)
{
    var parts = request.Context.Messages
        .Where(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
        .TakeLast(2)
        .Select(message => message.Content.Trim())
        .Where(value => value.Length > 0)
        .Append(request.CurrentQuestion.Trim());
    var combined = string.Join('\n', parts);
    return combined.Length <= maximumCharacters
        ? combined
        : combined[^maximumCharacters..];
}
```

不得读取处理器中的原始未过滤历史；不得把发送者姓名、ScopeHash 或摘要之外的内部标识放入检索文本。

- [ ] **Step 4: 实现两阶段检索状态机**

```csharp
public enum AdaptiveRetrievalPath
{
    OriginalQuestionHit,
    ContextFirstHit,
    RewriteHit,
    LowConfidence,
    Clarification,
    Failure
}

public sealed record AdaptiveRetrievalRequest(
    QueryRewriteRequest RewriteRequest,
    IReadOnlyList<Guid> AllowedTagIds);

public sealed record AdaptiveRetrievalResult(
    RetrievalQueryResult? Query,
    KnowledgeTagScope Scope,
    IReadOnlyList<RetrievalEvidence> Evidence,
    double? Confidence,
    AdaptiveRetrievalPath Path,
    QueryRewriteAudit? RewriteAudit,
    AnswerDecision? TerminalAnswer,
    int InitialRetrievalMilliseconds,
    int RewriteMilliseconds,
    int SecondRetrievalMilliseconds);
```

`PrepareAsync` 的固定顺序为：解析授权标签范围；构造首次查询；RAG；若最高相似度达到现有阈值立即返回；若没有正式上下文则返回低置信度；若有上下文则调用一次 `PrepareRewriteAsync`；只有改写结果为 Search 才执行第二次 RAG。所有异常映射为现有稳定失败码，取消令牌必须透传。

- [ ] **Step 5: 保持旧 `MultiTurnRetrievalService.PrepareAsync` 行为兼容**

提取 `PrepareRewriteAsync` 时保留现有公开 `PrepareAsync`，使旧 `AgentFramework` 模式和既有测试保持原语义。新增服务调用提取后的方法，禁止复制澄清安全验证和查询审计逻辑。

- [ ] **Step 6: 增加隔离、边界和异常测试**

覆盖：只接收 `ConversationContextResult.Messages`；最多最近两条用户消息；组合文本受 `RetrievalQueryOptions.TokenCap * 4` 限制；首检索超时不会调用改写；改写失败不使用原始低置信度证据生成确定性答案；两次检索沿用同一个已授权 `KnowledgeTagScope`。

- [ ] **Step 7: 运行聚焦测试并确认 GREEN**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*AdaptiveRetrievalServiceTests' '*MultiTurnRetrievalServiceTests' --minimum-expected-tests 1
```

Expected: PASS，且已有“有上下文必改写”的旧测试只验证兼容入口，新测试验证自适应入口。

---

### Task 3: 实现单次 JSON Schema 会话决策 Agent 和服务端验证器

**Files:**
- Create: `src/server/WechatRobot.Infrastructure/Agents/ConversationDecisionAgent.cs`
- Create: `src/server/WechatRobot.Application/Conversations/ConversationDecisionValidator.cs`
- Create: `tests/server/WechatRobot.UnitTests/Agents/ConversationDecisionAgentTests.cs`
- Create: `tests/server/WechatRobot.UnitTests/Conversations/ConversationDecisionValidatorTests.cs`

**Interfaces:**
- Consumes: `IAgentChatClientFactory.CreateAsync(Guid, CancellationToken)`、`ChatClientAgent`、`KnowledgeEvidenceProvider`、`ChatResponseFormat.ForJsonSchema<T>()`。
- Consumes: `FixedReplyTemplateService.ResolveAsync/ResolveForPrivateAsync`、`AnswerOutputFirewall`。
- Produces: `ConversationDecisionAgent : IConversationDecisionAgent`。
- Produces: `ConversationDecisionValidator.ValidateAndResolveAsync(...) -> GroundedAnswerResult?`；返回 `null` 表示必须走旧链路。

- [ ] **Step 1: 写结构化请求与单次调用失败测试**

```csharp
[Fact]
public async Task Decision_agent_uses_json_schema_and_one_provider_call()
{
    var client = new RecordingChatClient(
        """{"kind":"answer","answerText":"需要护照和申请表。","confidence":0.94,"reasonCode":"knowledge_answer"}""");
    var agent = CreateAgent(client);

    var result = await agent.DecideAsync(HighConfidenceRequest(), TestContext.Current.CancellationToken);

    Assert.Equal(ConversationDecisionKind.Answer, result.Kind);
    Assert.Equal(1, client.CallCount);
    Assert.NotNull(client.LastOptions?.ResponseFormat);
    Assert.Empty(client.LastOptions?.Tools ?? []);
}
```

同时断言：请求没有函数工具，因此不存在“模型→工具→模型”的二次循环；知识证据通过 `KnowledgeEvidenceProvider` 注入；固定回复候选只含 ID、版本、意图、示例和优先级，不含最终回复文本。

- [ ] **Step 2: 运行 Agent 测试并确认 RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*ConversationDecisionAgentTests' --minimum-expected-tests 1
```

Expected: FAIL，Agent 实现尚不存在。

- [ ] **Step 3: 使用 Agent Framework Structured Outputs 实现 Agent**

核心配置必须是一次无工具调用：

```csharp
var agent = new ChatClientAgent(
    client,
    new ChatClientAgentOptions
    {
        Name = "ConversationDecisionAgent",
        Description = "Selects a validated fixed reply or answers from authorized evidence.",
        ChatOptions = new ChatOptions
        {
            Instructions = Instructions,
            MaxOutputTokens = 2048,
            ResponseFormat = ChatResponseFormat.ForJsonSchema<DecisionPayload>(
                schemaName: "conversation_decision")
        },
        AIContextProviders =
        [
            new KnowledgeEvidenceProvider(request.Evidence)
        ]
    });
```

`DecisionPayload` 使用字符串枚举 `answer|fixed_reply|clarification|no_reply|continue_legacy_fallback`。反序列化必须大小写明确、拒绝多余顶层文本、限制 Answer/Clarification 为 4000/500 字符，并把超时、HTTP、模型不可用、JSON 无效分别映射为 `decision_timeout`、`decision_provider_failure`、`decision_provider_unavailable`、`decision_invalid_output`。提示词必须规定：只有 `RetrievalConfidence` 达到现有阈值时才能返回 Answer；低置信度时只能选择 FixedReply、Clarification 或 ContinueLegacyFallback。

- [ ] **Step 4: 写服务端决策验证失败测试**

覆盖以下结果都返回 `null` 并要求旧链路回退：模板 ID 不在候选集；版本不同；私聊返回 NoReply；被 @ 群聊返回 NoReply；低于 `GroundedAnswerOptions.ConfidenceThreshold` 却返回 Answer；Answer 未通过 `SanitizeGrounded + Validate(evidence)`；Clarification 未通过 `ValidateUngrounded`；模型返回非法 JSON。有效 FixedReply 必须通过服务重新 Resolve，不能采用模型提供的文本。

- [ ] **Step 5: 实现决策验证器**

```csharp
public Task<GroundedAnswerResult?> ValidateAndResolveAsync(
    ConversationDecisionResult decision,
    ConversationDecisionRequest request,
    GroupContextSettings contextPolicy,
    CancellationToken cancellationToken);
```

有效 Answer 的审计设置 `AnswerSource = "knowledge"`；有效 FixedReply 设置 `AnswerSource = "fixed_template"` 和真实 ID/版本；安全澄清设置 `AnswerSource = "clarification"`；验证失败不发送任何模型文本。

- [ ] **Step 6: 运行 Agent 与验证器测试并确认 GREEN**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*ConversationDecisionAgentTests' '*ConversationDecisionValidatorTests' --minimum-expected-tests 1
```

Expected: PASS；记录客户端确认每次 `DecideAsync` 恰好调用供应商一次。

---

### Task 4: 组装应用层自适应会话编排服务

**Files:**
- Create: `src/server/WechatRobot.Application/Conversations/AdaptiveConversationOrchestrator.cs`
- Create: `tests/server/WechatRobot.UnitTests/Conversations/AdaptiveConversationOrchestratorTests.cs`

**Interfaces:**
- Consumes: `AdaptiveRetrievalService`、`IConversationDecisionAgent`、`ConversationDecisionValidator`、`FixedReplyTemplateService`、现有 `IAnswerAgent`。
- Produces: `AdaptiveConversationRequest`、`AdaptiveConversationResult`、`IAdaptiveConversationOrchestrator.ProcessAsync(...)`。
- Produces: 统一指标字段 `InitialRetrievalMilliseconds`、`RewriteMilliseconds`、`DecisionMilliseconds`、`ModelCallCount`、`RetrievalPath`、`FallbackReasonCode`。

编排边界使用以下固定类型，处理器只负责把已有请求映射进去：

```csharp
public sealed record AdaptiveConversationRequest(
    GroundedAnswerRequest LegacyAnswerRequest,
    ConversationChannelType ChannelType,
    bool WasMentioned);

public sealed record AdaptiveConversationResult(
    GroundedAnswerResult Result,
    AdaptiveRetrievalPath RetrievalPath,
    int InitialRetrievalMilliseconds,
    int RewriteMilliseconds,
    int DecisionMilliseconds,
    int ModelCallCount,
    bool UsedLegacyFallback,
    string? FallbackReasonCode);

public interface IAdaptiveConversationOrchestrator
{
    Task<AdaptiveConversationResult> ProcessAsync(
        AdaptiveConversationRequest request,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 1: 写常见路径模型调用预算失败测试**

```csharp
[Theory]
[InlineData(ConversationChannelType.Private, false)]
[InlineData(ConversationChannelType.Group, true)]
public async Task High_confidence_rag_uses_one_decision_call_and_no_rewrite(
    ConversationChannelType channel,
    bool wasMentioned)
{
    var fixture = CreateFixture(initialSimilarity: 0.92);

    var result = await fixture.Subject.ProcessAsync(
        fixture.Request(channel, wasMentioned),
        TestContext.Current.CancellationToken);

    Assert.Equal(1, fixture.DecisionAgent.CallCount);
    Assert.Equal(0, fixture.RewriteAgent.CallCount);
    Assert.Equal(0, fixture.LegacyAnswerAgent.CallCount);
    Assert.Equal(1, result.ModelCallCount);
}
```

再写测试证明：低置信度上下文只改写一次；低置信度但命中 FixedReply 时仍只调用一次决策 Agent；结构化输出不支持时调用一次旧 `IAnswerAgent`；FixedReply 有效时不调用旧 Answer；Shadow 模式计算新结果但返回旧结果；Shadow 结果正文不得写日志。

- [ ] **Step 2: 运行测试并确认 RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*AdaptiveConversationOrchestratorTests' --minimum-expected-tests 1
```

Expected: FAIL，编排服务不存在。

- [ ] **Step 3: 实现编排服务固定顺序**

```text
1. AdaptiveRetrievalService 解析授权范围并执行首次 RAG，必要时只改写一次
2. 加载当前渠道有效固定回复候选；即使 RAG 低置信度也必须加载
3. IConversationDecisionAgent 单次结构化决策；低置信度时禁止生成知识答案，但仍可选择固定回复
4. ConversationDecisionValidator 服务端复核并返回结果
5. 任一步不支持/无效：使用最终安全 RetrievalQuery 调用旧 IAnswerAgent
6. 低置信度且未选择模板：进入现有 Web Search/模型知识/澄清回退链
```

固定回复候选查询与首次 RAG 可以在授权范围解析完成后并行，但不得并发使用同一个 EF `DbContext`。实现时由候选存储使用独立 scope，或保持顺序查询；不要为了约 10ms 的数据库耗时引入线程安全风险。

- [ ] **Step 4: 保留旧链路的失败语义**

结构化输出能力缺失、Agent 超时、JSON 非法、模板并发版本变化或输出防火墙拒绝时，设置稳定 `FallbackReasonCode` 并调用现有 `IAnswerAgent`。取消请求时不得回退；必须立即传播调用者取消。

- [ ] **Step 5: 添加无正文的结构化日志**

使用 `ILogger<AdaptiveConversationOrchestrator>` 记录一个完成事件：

```csharp
logger.LogInformation(
    "Adaptive conversation completed. Channel={Channel} Path={Path} ModelCalls={ModelCalls} InitialRetrievalMs={InitialRetrievalMs} RewriteMs={RewriteMs} DecisionMs={DecisionMs} Fallback={Fallback}",
    request.ChannelType,
    result.RetrievalPath,
    result.ModelCallCount,
    result.InitialRetrievalMilliseconds,
    result.RewriteMilliseconds,
    result.DecisionMilliseconds,
    result.FallbackReasonCode);
```

- [ ] **Step 6: 运行测试并确认 GREEN**

Run Task 4 的测试命令。Expected: PASS，并断言日志参数中不存在问题、回答、证据正文、用户名和 ScopeHash。

---

### Task 5: 接入私聊且不影响命令、入库和固定回复安全边界

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/Agents/PrivateChatProcessor.cs`
- Modify: `src/server/WechatRobot.Worker/Program.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateChatProcessorTests.cs`

**Interfaces:**
- Consumes: `IAdaptiveConversationOrchestrator.ProcessAsync(...)`。
- Preserves: `PrivateChatCommandParser`、直接知识入库、问候语、正式会话创建、现有审计和发送幂等键。

- [ ] **Step 1: 写私聊接入失败测试**

新增以下用例：

```csharp
[Fact]
public async Task Adaptive_private_follow_up_uses_context_first_rag_and_one_model_call()
```

场景为第一轮“日本3年签证你们能办吗？”、第二轮“需要什么材料”。断言第二轮只读取同一私聊 ScopeHash 的正式上下文，改写 Agent 为 0 次，决策 Agent 为 1 次，旧 Answer Agent 为 0 次，回复和 RetrievalAudit 均已持久化。

再新增：`AdaptiveShadow` 仍发送旧链路结果；结构化输出失败安全回退；直接知识入库和问候语均不调用自适应服务。

- [ ] **Step 2: 运行私聊聚焦测试并确认 RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*PrivateChatProcessorTests' --minimum-expected-tests 1
```

Expected: 新测试 FAIL，因为处理器尚未分派自适应模式。

- [ ] **Step 3: 在现有上下文构建之后接入编排器**

保持处理器前半段顺序完全不变：Disabled、会话、命令、入库、问候和模型配置。非自适应模式继续使用当前“模板路由→上下文→改写→回答”顺序；自适应模式跳过前置旧模板路由，先完成历史过滤和 `ConversationContextService.Build`，再在旧模板路由/`MultiTurnRetrievalService`/Answer 段前增加：

```csharp
if (runtime.PrivateChatRuntimeMode is
    PrivateChatRuntimeMode.AdaptiveShadow or
    PrivateChatRuntimeMode.AdaptiveSinglePass)
{
    var adaptive = await adaptiveOrchestrator.ProcessAsync(
        BuildAdaptiveRequest(..., ConversationChannelType.Private, wasMentioned: false),
        cancellationToken);
    if (runtime.PrivateChatRuntimeMode == PrivateChatRuntimeMode.AdaptiveSinglePass)
    {
        await ReplyAsync(message, adaptive.Result.Decision.GroupText,
            cancellationToken, adaptive.Result, model.Id);
        return;
    }
}
```

Shadow 模式必须在计算新结果后继续执行并发送完整旧链路（包括旧模板路由）结果，只记录决策类别、来源类别和耗时差异，不比较或记录正文。

- [ ] **Step 4: 注册依赖**

在 `WechatRobot.Worker/Program.cs` 使用现有生命周期：

```csharp
builder.Services.AddScoped<IConversationDecisionAgent, ConversationDecisionAgent>();
builder.Services.AddScoped<AdaptiveRetrievalService>();
builder.Services.AddScoped<ConversationDecisionValidator>();
builder.Services.AddScoped<IAdaptiveConversationOrchestrator, AdaptiveConversationOrchestrator>();
```

- [ ] **Step 5: 运行私聊回归并确认 GREEN**

Run Task 5 的聚焦测试。Expected: PASS；既有私聊固定回复、模糊澄清、普通回答和入库测试全部通过。

---

### Task 6: 接入群聊并对明确 @ 消息跳过冗余意图调用

**Files:**
- Modify: `src/server/WechatRobot.Application/Messaging/InboundMessageProcessor.cs`
- Modify: `tests/server/WechatRobot.UnitTests/Conversations/InboundMessageProcessorTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Conversations/InboundGroupRulePipelineTests.cs`

**Interfaces:**
- Consumes: `IAdaptiveConversationOrchestrator.ProcessAsync(...)`。
- Preserves: `EvaluateInboundPolicyAsync`、会话租约、摘要、NoReply 持久化和 `PersistAnswerAndEnqueueAsync`。

- [ ] **Step 1: 写群聊调用预算失败测试**

新增三条核心用例：

```csharp
[Fact]
public async Task Adaptive_mentioned_group_message_skips_intent_and_rewrite()

[Fact]
public async Task Adaptive_unmentioned_group_message_keeps_intent_gate()

[Fact]
public async Task Adaptive_unmentioned_no_reply_stops_before_rag_and_decision()
```

第一条断言：`WasMentioned=true`、Intent Agent 0 次、Rewrite 0 次、Decision 1 次。第二条断言：未 @ 消息仍由 Intent Agent 判断，Reply 后再进入自适应编排。第三条断言：NoReply 时 RAG、Decision 和发送均为 0 次。

- [ ] **Step 2: 运行群聊测试并确认 RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*InboundMessageProcessorTests' --minimum-expected-tests 1
```

Expected: 新测试 FAIL，当前 `AgentFramework` 意图模式对所有消息都会调用 Intent Agent。

- [ ] **Step 3: 调整意图门控条件**

仅在自适应模式下把平台已经明确提供的 `WasMentioned=true` 当作确定性 DirectedToBot；现有 `Legacy`、`Shadow`、`AgentFramework` 模式不变：

```csharp
var adaptiveMode = runtime.AnswerRuntimeMode is
    AnswerRuntimeMode.AdaptiveShadow or
    AnswerRuntimeMode.AdaptiveSinglePass;
var requiresIntentAgent = !payload.WasMentioned || !adaptiveMode;
```

被 @ 消息仍要经过 `EvaluateInboundPolicyAsync`，不得绕过群启用状态、授权、速率限制或会话租约。

- [ ] **Step 4: 在摘要和正式上下文完成后接入编排器**

非自适应模式保持当前旧模板路由位置。自适应模式先跳过前置旧模板路由，把入口放在 `ConversationContextService.Build` 和可选摘要完成之后、旧模板路由和旧查询改写之前。`AdaptiveSinglePass` 使用新结果持久化并返回；`AdaptiveShadow` 在计算新结果后继续完整旧链路。固定回复由新决策 Agent 选择后仍由服务端 Resolve。

- [ ] **Step 5: 运行群聊单元与集成回归**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*InboundMessageProcessorTests' --minimum-expected-tests 1
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*InboundGroupRulePipelineTests' --minimum-expected-tests 1
```

Expected: PASS；明确 @ 的常见路径减少一次意图模型调用，未 @ 的群消息仍保持原安全门控。

---

### Task 7: 验证 Agent Framework 能力回退、完整回归和本地性能证据

**Files:**
- Modify: `tests/server/WechatRobot.UnitTests/Agents/AgentCapabilityProbeTests.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Conversations/AdaptiveConversationCallBudgetTests.cs`
- Create: `docs/runbooks/agent-framework-adaptive-rag-rollout.md`
- Modify: `src/server/WechatRobot.Worker/Program.cs`

**Interfaces:**
- Consumes: 现有 `AgentCapability.JsonSchema` 探测结果与稳定失败码。
- Produces: Shadow→启用→回退运行手册和可重复调用预算测试。

- [ ] **Step 1: 写能力回退测试**

扩展探测测试，证明支持 JSON Schema 时报告 `AgentCapability.JsonSchema`；供应商拒绝 schema 时不把整个 Chat 能力标记失败。编排测试证明实际请求遇到 schema 不支持异常时返回 `decision_json_schema_unsupported` 并执行旧路径，不向用户发送原始异常。

- [ ] **Step 2: 运行能力测试并确认预期**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class '*AgentCapabilityProbeTests' '*ConversationDecisionAgentTests' --minimum-expected-tests 1
```

Expected: 新回退断言在实现前 FAIL，完成映射后 PASS。

- [ ] **Step 3: 添加不依赖墙钟的调用预算集成测试**

`AdaptiveConversationCallBudgetTests` 使用记录型 Fake，不用“必须小于 N 秒”的脆弱断言：

| 场景 | Intent | Rewrite | Decision | Legacy fallback | 总模型调用上限 |
|---|---:|---:|---:|---:|---:|
| 私聊，高置信度 RAG | 0 | 0 | 1 | 0 | 1 |
| 被 @ 群聊，高置信度 RAG | 0 | 0 | 1 | 0 | 1 |
| 未 @ 群聊，高置信度 RAG | 1 | 0 | 1 | 0 | 2 |
| 上下文首次低置信度、改写后命中 | 0 | 1 | 1 | 0 | 2 |
| Schema 不支持 | 0 | 0 | 1 次失败 | 1 | 2 |

- [ ] **Step 4: 编写部署与回退运行手册**

运行手册必须包含：先保持当前模式；将私聊和群聊分别切到 `AdaptiveShadow`；观察至少一个业务高峰窗口的 `ModelCalls`、`RetrievalPath`、失败码、P50/P95；确认无安全回退异常后切到 `AdaptiveSinglePass`；异常时恢复 `AgentFramework`。明确无需 MySQL 迁移、无需重建 Qdrant 集合；由于 API 和 Worker 都引用变更后的 Application 程序集并绑定 `AgentRuntimeOptions`，两者都必须重新发布和重启。

- [ ] **Step 5: 运行完整后端验证**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore
dotnet build src/server/WechatRobot.Api/WechatRobot.Api.csproj -c Release --no-restore
dotnet build src/server/WechatRobot.Worker/WechatRobot.Worker.csproj -c Release --no-restore
git diff --check
```

Expected: 所有测试和 Release 构建通过，`git diff --check` 无输出。若存在与本改动无关的基线失败，单独记录完整测试名和错误，不得隐藏。

- [ ] **Step 6: 使用 `.local` 做真实 Shadow 验收**

按仓库启动约束设置 `WECHATROBOT_ENV_FILE` 为 `.local/.env` 的绝对路径，并以 `.local` 为工作目录启动新编译 Worker；不输出任何环境变量值。验证同一组问题：

```text
第一轮：日本3年签证你们能办吗？
第二轮：需要什么材料？
独立问题：韩国签证需要什么材料？
固定回复：美国签证多少钱？
未 @ 群聊：两个人之间的普通对话
```

记录每条的端到端耗时、`InitialRetrievalMs`、`RewriteMs`、`DecisionMs`、`ModelCalls` 和路径。验收目标：私聊及被 @ 群聊的高置信度 RAG 路径模型调用数为 1；第二轮只有首次组合检索低置信度时才改写；未 @ 普通群聊仍 NoReply；固定回复文本与服务端模板完全一致。

- [ ] **Step 7: 最终安全审查**

检查 Git 差异、日志和测试快照，确认没有 `.local` 内容、凭据、问题正文、用户身份、内部工具调用或知识来源正文。确认没有修改数据库迁移、知识入库、审核、索引和物理清理代码。

---

## 完成定义

- 私聊和被明确 @ 的群聊在高置信度 RAG 命中时：首次 RAG 1 次、QueryRewrite 0 次、结构化 Decision Agent 1 次、旧 Answer Agent 0 次。
- 未 @ 群聊继续先执行意图门控；NoReply 不进入 RAG，Reply 才进入自适应链路。
- “日本3年签证你们能办吗？”→“需要什么材料？”能够由正式上下文组成首次检索意图；只有该检索低置信度时才改写成完整问题。
- 固定回复、RAG 答案、澄清和 NoReply 都经过服务端业务规则复核；模型没有最终授权决定权。
- 不采用 `HarnessAgent`，不增加工具调用循环，不迁移 MySQL，不重建 Qdrant，不改变知识入库流程。
- Shadow、启用和回退均有可操作运行手册，且有调用次数与真实 `.local` 耗时证据。
