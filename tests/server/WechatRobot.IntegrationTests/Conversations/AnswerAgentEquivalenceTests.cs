using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using WechatRobot.Application.Agents;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Groups;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Models;
using WechatRobot.Infrastructure.Agents;

namespace WechatRobot.IntegrationTests.Conversations;

public sealed class AnswerAgentEquivalenceTests
{
    [Fact]
    public async Task Knowledge_branch_preserves_source_evidence_and_firewall_semantics()
    {
        var retrieval = new EvidenceRetrieval();
        var options = new GroundedAnswerOptions(.7);
        var firewall = new AnswerOutputFirewall();
        var request = Request();
        var legacy = new GroundedAnswerService(
            retrieval,
            new LegacyChat(),
            options,
            firewall);
        var agent = new AnswerAgent(
            retrieval,
            new StubFactory(new AgentChat()),
            new LegacyChat(),
            options,
            firewall);

        var legacyResult = await legacy.AnswerAsync(
            request,
            TestContext.Current.CancellationToken);
        var agentResult = await agent.AnswerAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(legacyResult.Decision.Kind, agentResult.Decision.Kind);
        Assert.Equal(legacyResult.Audit.AnswerSource, agentResult.Audit.AnswerSource);
        Assert.Equal(legacyResult.Audit.FailureCode, agentResult.Audit.FailureCode);
        Assert.Equal(
            legacyResult.Audit.Evidence.Select(item => item.ChunkId),
            agentResult.Audit.Evidence.Select(item => item.ChunkId));
    }

    private static GroundedAnswerRequest Request() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "group",
        "签证需要多久？",
        [],
        new ConversationContextResult([], null, false, false),
        new GroupContextSettings(false, 6, 30, 3000, true, true),
        new ModelProviderConfiguration(
            "https://example.test",
            "chat",
            "encrypted",
            TimeSpan.FromSeconds(10),
            0),
        ModelConfigurationId: Guid.NewGuid());

    private sealed class EvidenceRetrieval : IRetrievalEvidenceProvider
    {
        private readonly RetrievalEvidence evidence = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            .92,
            [],
            "签证指南",
            "通常需要等待签证机关审核。");

        public Task<KnowledgeTagScope> ResolveScopeAsync(
            IReadOnlyList<Guid> requestedTagIds,
            CancellationToken token) =>
            Task.FromResult(new KnowledgeTagScope([], [], "all"));

        public Task<IReadOnlyList<RetrievalEvidence>> RetrieveAsync(
            string question,
            KnowledgeTagScope scope,
            int limit,
            CancellationToken token) =>
            Task.FromResult<IReadOnlyList<RetrievalEvidence>>([evidence]);
    }

    private sealed class LegacyChat : IChatCompletionClient
    {
        public Task<ChatCompletionResponse> CompleteAsync(
            ModelProviderConfiguration configuration,
            ChatCompletionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatCompletionResponse("请等待签证机关审核。"));
    }

    private sealed class StubFactory(IChatClient client) : IAgentChatClientFactory
    {
        public Task<IChatClient> CreateAsync(
            Guid modelConfigurationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(client);
    }

    private sealed class AgentChat : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(
                new Microsoft.Extensions.AI.ChatMessage(
                    ChatRole.Assistant,
                    "请等待签证机关审核。")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
