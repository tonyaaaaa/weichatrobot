# Agent Framework GLM Request Content Length Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复官方 GLM Agent Framework 请求改写后的 `Content-Length` 不一致，并证明所有共享 Agent 调用使用同一个已修复传输边界。

**Architecture:** 保持 `ChatClientAgent -> IChatClient -> OpenAI .NET SDK -> HttpClient` 调用链不变。只在共享 `OpenAiCompatibleRequestTuningHandler` 替换 JSON 内容时保留 `Content-Type`，由新 `ByteArrayContent` 计算真实长度；通过真实回环 TCP 服务器覆盖 `SocketsHttpHandler` 的请求写入行为。

**Tech Stack:** .NET 10、Microsoft Agent Framework、Microsoft.Extensions.AI、OpenAI .NET SDK、System.Net.Http、xUnit v3/Microsoft Testing Platform。

## Global Constraints

- 不改变 Agent Framework、模型选择、Web Search、Embedding、Qdrant 或 WorkTool 调用路径。
- 不新增依赖、配置项、数据库迁移或数据修改。
- 仅官方 GLM 主机和 `glm-*` 模型继续应用 `thinking.type=disabled`。
- 不复制 `Content-Length`、`Content-MD5`、`Content-Encoding` 或 `Content-Range`。
- 生产发布包保持 API、Worker、Web 的现有目录结构。

---

### Task 1: Reproduce the real transport framing failure

**Files:**
- Modify: `tests/server/WechatRobot.UnitTests/Models/OpenAiCompatibleModelClientTests.cs`

**Interfaces:**
- Consumes: `OpenAiCompatibleRequestTuningHandler(HttpMessageHandler, string, string, bool)`
- Produces: 回归测试 `Agent_framework_transport_sends_rewritten_body_with_computed_content_length`

- [ ] **Step 1: Add a loopback HTTP server test**

测试使用 `TcpListener` 接收真实 HTTP/1.1 请求，解析 `Content-Length`，读取同等
数量的 UTF-8 正文字节并返回 200。客户端使用真实 `SocketsHttpHandler`，外层包裹
`OpenAiCompatibleRequestTuningHandler`：

```csharp
[Fact]
public async Task Agent_framework_transport_sends_rewritten_body_with_computed_content_length()
{
    await using var server = new SingleRequestServer();
    using var client = new HttpClient(new OpenAiCompatibleRequestTuningHandler(
        new SocketsHttpHandler(),
        "https://api.z.ai/api/coding/paas/v4",
        "glm-5.2",
        removeAuthorization: false));

    var send = client.PostAsync(
        $"http://127.0.0.1:{server.Port}/chat/completions",
        new StringContent(
            """{"model":"glm-5.2","messages":[]}""",
            Encoding.UTF8,
            "application/json"),
        TestContext.Current.CancellationToken);
    var captured = await server.ReceiveAndReplyAsync(
        TestContext.Current.CancellationToken);
    using var response = await send;

    Assert.Equal(captured.Body.Length, captured.ContentLength);
    Assert.Equal("application/json", captured.ContentType);
    using var json = JsonDocument.Parse(captured.Body);
    Assert.Equal("disabled", json.RootElement
        .GetProperty("thinking").GetProperty("type").GetString());
    Assert.Equal(2048, json.RootElement.GetProperty("max_tokens").GetInt32());
}
```

- [ ] **Step 2: Run the test and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-method "WechatRobot.UnitTests.Models.OpenAiCompatibleModelClientTests.Agent_framework_transport_sends_rewritten_body_with_computed_content_length"
```

Expected: FAIL with `Unable to write content to request stream; content would exceed Content-Length.`

### Task 2: Preserve only valid representation metadata

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/Models/OpenAiCompatibleRequestTuning.cs:65-68`
- Test: `tests/server/WechatRobot.UnitTests/Models/OpenAiCompatibleModelClientTests.cs`

**Interfaces:**
- Consumes: modified JSON UTF-8 bytes and original `HttpContent.Headers.ContentType`
- Produces: replacement `ByteArrayContent` with computed length and preserved media type

- [ ] **Step 1: Implement the minimal fix**

Replace the all-header copy loop with explicit `Content-Type` preservation:

```csharp
var originalContentType = request.Content.Headers.ContentType;
var replacement = new ByteArrayContent(
    Encoding.UTF8.GetBytes(root.ToJsonString()));
replacement.Headers.ContentType = originalContentType;
request.Content = replacement;
```

- [ ] **Step 2: Run the focused test and verify GREEN**

Run the Task 1 command.

Expected: PASS; the loopback server receives the complete modified JSON with matching length.

- [ ] **Step 3: Run all model transport unit tests**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class "WechatRobot.UnitTests.Models.OpenAiCompatibleModelClientTests" "WechatRobot.UnitTests.Agents.OpenAiCompatibleAgentChatClientFactoryTests"
```

Expected: all selected tests pass.

### Task 3: Verify shared call paths and release output

**Files:**
- Verify only: `src/server/WechatRobot.Infrastructure/Agents/*.cs`
- Generated, ignored output: `artifacts/staging/wechatrobot-<timestamp>/`
- Generated, ignored output: `artifacts/wechatrobot-<timestamp>.zip`

**Interfaces:**
- Consumes: fixed shared `OpenAiCompatibleAgentChatClientFactory`
- Produces: verified Release package containing API, Worker and Web

- [ ] **Step 1: Confirm every Agent call site shares the factory**

```powershell
rg -n "IAgentChatClientFactory|clients\.CreateAsync|clientFactory\.CreateAsync" src/server/WechatRobot.Infrastructure/Agents -g "*.cs"
```

Expected: Answer、QueryRewrite、MessageIntent、TemplateRouting、PrivateKnowledgeProposal、AgentCapabilityProbe 均指向共享工厂；没有第二个官方 GLM Agent 传输实现。

- [ ] **Step 2: Run backend verification**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class "WechatRobot.IntegrationTests.PrivateChat.PrivateChatProcessorTests"
dotnet build WechatRobot.slnx -c Release --no-restore
git diff --check
```

Expected: all tests pass; Release build has zero warnings and zero errors; diff check is clean.

- [ ] **Step 3: Build and validate the release archive**

Publish API and Worker in Release mode, copy the freshly built Web `dist`, create the timestamped
zip, and verify:

```text
required entries: api/WechatRobot.Api.dll, worker/WechatRobot.Worker.dll, web/index.html
forbidden entries: .env, .local
API and Worker WechatRobot.Infrastructure.dll hashes equal the Release build hash
```

- [ ] **Step 4: Commit the implementation**

```powershell
git add -- src/server/WechatRobot.Infrastructure/Models/OpenAiCompatibleRequestTuning.cs tests/server/WechatRobot.UnitTests/Models/OpenAiCompatibleModelClientTests.cs docs/superpowers/specs/2026-08-04-agent-http-content-length-fix-design.md docs/superpowers/plans/2026-08-04-agent-http-content-length-fix.md
git commit -m "fix: recompute GLM agent request content length"
```
