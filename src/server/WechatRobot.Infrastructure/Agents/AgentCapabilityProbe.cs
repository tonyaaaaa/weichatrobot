using System.Text.Json;
using Microsoft.Extensions.AI;
using WechatRobot.Application.Agents;

namespace WechatRobot.Infrastructure.Agents;

public sealed class AgentCapabilityProbe(
    IAgentChatClientFactory clientFactory,
    IAgentModelConfigurationReader modelReader,
    TimeProvider timeProvider) : IAgentCapabilityProbe
{
    public async Task<AgentCapabilityReport> ProbeAsync(
        Guid modelConfigurationId,
        CancellationToken cancellationToken = default)
    {
        var model = await modelReader.GetAsync(modelConfigurationId, cancellationToken);
        var supported = new HashSet<AgentCapability>();

        try
        {
            using var client = await clientFactory.CreateAsync(modelConfigurationId, cancellationToken);
            var chat = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Reply with the text chat-ok.")],
                cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(chat.Text))
            {
                return Report(model, supported, "agent_probe_invalid_output");
            }
            supported.Add(AgentCapability.Chat);

            await IgnoreUnsupportedAsync(
                () => ProbeToolsAsync(client, supported, cancellationToken),
                cancellationToken);
            await IgnoreUnsupportedAsync(
                () => ProbeJsonObjectAsync(client, supported, cancellationToken),
                cancellationToken);
            await IgnoreUnsupportedAsync(
                () => ProbeJsonSchemaAsync(client, supported, cancellationToken),
                cancellationToken);
            return Report(model, supported, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Report(model, supported, "agent_probe_timeout");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Report(model, supported, "agent_probe_failed");
        }
    }

    private static async Task ProbeToolsAsync(
        IChatClient client,
        HashSet<AgentCapability> supported,
        CancellationToken cancellationToken)
    {
        var tool = AIFunctionFactory.Create(
            (Func<string>)(() => "probe-ok"),
            "agent_capability_probe",
            "Returns the fixed text probe-ok.");
        var user = new ChatMessage(
            ChatRole.User,
            "Call agent_capability_probe exactly once and do not answer directly.");
        var first = await client.GetResponseAsync(
            [user],
            new ChatOptions { Tools = [tool] },
            cancellationToken);
        var call = first.Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .SingleOrDefault(content =>
                string.Equals(content.Name, tool.Name, StringComparison.Ordinal));
        if (call is null)
        {
            return;
        }

        supported.Add(AgentCapability.FunctionTools);
        var messages = new List<ChatMessage> { user };
        messages.AddRange(first.Messages);
        messages.Add(new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent(call.CallId, "probe-ok")]));
        var second = await client.GetResponseAsync(
            messages,
            new ChatOptions { Tools = [tool] },
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(second.Text))
        {
            supported.Add(AgentCapability.ToolResultLoop);
        }
    }

    private static async Task ProbeJsonObjectAsync(
        IChatClient client,
        HashSet<AgentCapability> supported,
        CancellationToken cancellationToken)
    {
        var json = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, """Return {"probe":"ok"}.""")],
            new ChatOptions { ResponseFormat = ChatResponseFormat.Json },
            cancellationToken);
        if (!IsProbeJson(json.Text))
        {
            return;
        }
        supported.Add(AgentCapability.JsonObject);
    }

    private static async Task ProbeJsonSchemaAsync(
        IChatClient client,
        HashSet<AgentCapability> supported,
        CancellationToken cancellationToken)
    {
        var schema = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, """Return {"probe":"ok"}.""")],
            new ChatOptions
            {
                ResponseFormat = ChatResponseFormat.ForJsonSchema<ProbePayload>(
                    schemaName: "agent_capability_probe")
            },
            cancellationToken);
        if (IsProbeJson(schema.Text))
        {
            supported.Add(AgentCapability.JsonSchema);
        }
    }

    private static async Task IgnoreUnsupportedAsync(
        Func<Task> probe,
        CancellationToken cancellationToken)
    {
        try
        {
            await probe();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Capability probes are independent. A provider may reject one option
            // while supporting the remaining capabilities.
        }
    }

    private AgentCapabilityReport Report(
        AgentModelConfigurationSnapshot model,
        IReadOnlySet<AgentCapability> supported,
        string? failureCode) =>
        new(
            model.Id,
            model.Version,
            supported,
            failureCode,
            timeProvider.GetUtcNow().UtcDateTime);

    private static bool IsProbeJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.EnumerateObject().Count() == 1
                   && document.RootElement.TryGetProperty("probe", out var probe)
                   && probe.ValueKind == JsonValueKind.String
                   && string.Equals(probe.GetString(), "ok", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record ProbePayload(string Probe);
}
