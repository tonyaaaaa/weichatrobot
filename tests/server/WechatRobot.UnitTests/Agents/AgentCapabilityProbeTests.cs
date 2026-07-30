using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using WechatRobot.Application.Agents;
using WechatRobot.Infrastructure.Agents;

namespace WechatRobot.UnitTests.Agents;

public sealed class AgentCapabilityProbeTests
{
    [Fact]
    public async Task Probe_reports_tool_support_only_after_tool_call_and_result_loop_succeed()
    {
        var modelId = Guid.NewGuid();
        var client = new ScriptedChatClient(
            Text("chat-ok"),
            ToolCall("call-1"),
            Text("tool-result-accepted"),
            Text("""{"probe":"ok"}"""),
            Text("""{"probe":"ok"}"""));
        var probe = new AgentCapabilityProbe(
            new StubFactory(client),
            new StubModelReader(modelId, 7),
            TimeProvider.System);

        var result = await probe.ProbeAsync(modelId, TestContext.Current.CancellationToken);

        Assert.Equal(7, result.ModelConfigurationVersion);
        Assert.Contains(AgentCapability.Chat, result.Supported);
        Assert.Contains(AgentCapability.FunctionTools, result.Supported);
        Assert.Contains(AgentCapability.ToolResultLoop, result.Supported);
        Assert.Contains(AgentCapability.JsonObject, result.Supported);
        Assert.Contains(AgentCapability.JsonSchema, result.Supported);
        Assert.Null(result.FailureCode);
    }

    [Fact]
    public async Task Probe_does_not_infer_tool_or_schema_support_from_plain_chat()
    {
        var modelId = Guid.NewGuid();
        var client = new ScriptedChatClient(
            Text("chat-ok"),
            Text("tool ignored"),
            Text("response format ignored"),
            Text("response format ignored"));
        var probe = new AgentCapabilityProbe(
            new StubFactory(client),
            new StubModelReader(modelId, 4),
            TimeProvider.System);

        var result = await probe.ProbeAsync(modelId, TestContext.Current.CancellationToken);

        Assert.Contains(AgentCapability.Chat, result.Supported);
        Assert.DoesNotContain(AgentCapability.FunctionTools, result.Supported);
        Assert.DoesNotContain(AgentCapability.ToolResultLoop, result.Supported);
        Assert.DoesNotContain(AgentCapability.JsonObject, result.Supported);
        Assert.DoesNotContain(AgentCapability.JsonSchema, result.Supported);
    }

    [Fact]
    public async Task Probe_keeps_testing_json_when_provider_rejects_tool_options()
    {
        var modelId = Guid.NewGuid();
        var client = new ScriptedChatClient(
            Text("chat-ok"),
            new InvalidOperationException("tools are not supported"),
            Text("""{"probe":"ok"}"""),
            Text("""{"probe":"ok"}"""));
        var probe = new AgentCapabilityProbe(
            new StubFactory(client),
            new StubModelReader(modelId, 5),
            TimeProvider.System);

        var result = await probe.ProbeAsync(modelId, TestContext.Current.CancellationToken);

        Assert.Contains(AgentCapability.Chat, result.Supported);
        Assert.DoesNotContain(AgentCapability.FunctionTools, result.Supported);
        Assert.Contains(AgentCapability.JsonObject, result.Supported);
        Assert.Contains(AgentCapability.JsonSchema, result.Supported);
        Assert.Null(result.FailureCode);
    }

    private static ChatResponse Text(string value) =>
        new(new ChatMessage(ChatRole.Assistant, value));

    private static ChatResponse ToolCall(string callId) =>
        new(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent(callId, "agent_capability_probe", new Dictionary<string, object?>())]));

    private sealed class StubFactory(IChatClient client) : IAgentChatClientFactory
    {
        public Task<IChatClient> CreateAsync(Guid modelConfigurationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(client);
    }

    private sealed class StubModelReader(Guid id, int version) : IAgentModelConfigurationReader
    {
        public Task<AgentModelConfigurationSnapshot> GetAsync(
            Guid modelConfigurationId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(id, modelConfigurationId);
            return Task.FromResult(new AgentModelConfigurationSnapshot(id, version));
        }
    }

    private sealed class ScriptedChatClient(params object[] responses) : IChatClient
    {
        private readonly Queue<object> remaining = new(responses);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var next = remaining.Dequeue();
            return next is Exception exception
                ? Task.FromException<ChatResponse>(exception)
                : Task.FromResult((ChatResponse)next);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
