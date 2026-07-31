using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using WechatRobot.Application.Agents;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Memory;
using WechatRobot.Application.Models;

namespace WechatRobot.Infrastructure.Agents;

public sealed class AnswerAgent(
    IRetrievalEvidenceProvider retrieval,
    IAgentChatClientFactory clients,
    IChatCompletionClient legacyChat,
    GroundedAnswerOptions options,
    AnswerOutputFirewall outputFirewall,
    IMemoryRecallService? memoryRecallService = null) : IAnswerAgent
{
    public Task<GroundedAnswerResult> AnswerAsync(
        GroundedAnswerRequest request,
        CancellationToken cancellationToken)
    {
        var chat = request.ModelConfigurationId is { } modelId && modelId != Guid.Empty
            ? new AgentFrameworkCompletionClient(clients, legacyChat, modelId)
            : legacyChat;
        return new GroundedAnswerService(
                retrieval,
                chat,
                options,
                outputFirewall,
                memoryRecallService)
            .AnswerAsync(request, cancellationToken);
    }

    private sealed class AgentFrameworkCompletionClient(
        IAgentChatClientFactory clients,
        IChatCompletionClient legacy,
        Guid modelConfigurationId) : IChatCompletionClient
    {
        private static readonly JsonSerializerOptions PromptOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public async Task<ChatCompletionResponse> CompleteAsync(
            ModelProviderConfiguration configuration,
            ChatCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            // Provider-native Web Search has a verified vendor-specific request
            // and source contract. Keep that path on the existing typed client.
            if (request.WebSearch is not null)
            {
                return await legacy.CompleteAsync(
                    configuration,
                    request,
                    cancellationToken);
            }

            var instructions = string.Join(
                "\n\n",
                request.Messages
                    .Where(message => string.Equals(
                        message.Role,
                        "system",
                        StringComparison.OrdinalIgnoreCase))
                    .Select(message => message.Content));
            var untrustedMessages = request.Messages
                .Where(message => !string.Equals(
                    message.Role,
                    "system",
                    StringComparison.OrdinalIgnoreCase))
                .Where(message => !message.Content.Contains(
                    "<<<UNTRUSTED_BUSINESS_EVIDENCE_BEGIN>>>",
                    StringComparison.Ordinal))
                .Select(message => new
                {
                    role = message.Role,
                    content = message.Content
                })
                .ToArray();
            using var client = await clients.CreateAsync(
                modelConfigurationId,
                cancellationToken);
            var agent = new ChatClientAgent(
                client,
                new ChatClientAgentOptions
                {
                    Name = "AnswerAgent",
                    Description =
                        "Produces one answer using the deterministic server-provided context.",
                    ChatOptions = new ChatOptions
                    {
                        Instructions = instructions
                    },
                    AIContextProviders =
                    [
                        new KnowledgeEvidenceProvider(
                            request.ControlledEvidence ?? [])
                    ]
                });
            var response = await agent.RunAsync(
                JsonSerializer.Serialize(
                    new { messages = untrustedMessages },
                    PromptOptions),
                cancellationToken: cancellationToken);
            return new ChatCompletionResponse(response.Text ?? string.Empty);
        }
    }
}
