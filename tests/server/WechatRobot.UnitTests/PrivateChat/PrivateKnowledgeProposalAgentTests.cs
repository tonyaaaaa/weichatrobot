using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using WechatRobot.Application.Agents;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Agents;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.UnitTests.PrivateChat;

public sealed class PrivateKnowledgeProposalAgentTests
{
    [Fact]
    public async Task Agent_uses_terminal_tool_and_returns_bounded_typed_proposals()
    {
        await using var db = Database();
        var modelId = Guid.NewGuid();
        db.ModelConfigs.Add(new ModelConfigEntity
        {
            Id = modelId,
            Name = "chat",
            NormalizedName = "CHAT",
            Provider = "OpenAI",
            ConfigurationType = "chat",
            BaseUrl = "https://example.test",
            Model = "test",
            IsEnabled = true,
            IsDefault = true
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var client = new ProposalChatClient();
        var agent = new PrivateKnowledgeProposalAgent(
            db,
            new StubFactory(client),
            new StubRetrieval());

        var result = await agent.ProposeAsync(
            "加拿大签证通常需要等待审核结果。",
            TestContext.Current.CancellationToken);

        var item = Assert.Single(result);
        Assert.Equal("加拿大签证需要多久？", item.Question);
        Assert.Equal("New", item.ChangeKind.ToString());
        Assert.True(client.SawProposalTool);
    }

    private static WechatRobotDbContext Database()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new WechatRobotDbContext(options);
    }

    private sealed class StubFactory(IChatClient client) : IAgentChatClientFactory
    {
        public Task<IChatClient> CreateAsync(
            Guid modelConfigurationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(client);
    }

    private sealed class StubRetrieval : IRetrievalEvidenceProvider
    {
        public Task<KnowledgeTagScope> ResolveScopeAsync(
            IReadOnlyList<Guid> requestedTagIds,
            CancellationToken token) =>
            Task.FromResult(new KnowledgeTagScope(requestedTagIds, requestedTagIds, "all"));

        public Task<IReadOnlyList<RetrievalEvidence>> RetrieveAsync(
            string question,
            KnowledgeTagScope scope,
            int limit,
            CancellationToken token) =>
            Task.FromResult<IReadOnlyList<RetrievalEvidence>>([]);
    }

    private sealed class ProposalChatClient : IChatClient
    {
        private int calls;
        public bool SawProposalTool { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            calls++;
            var tool = options?.Tools?.OfType<AIFunction>()
                .SingleOrDefault(x => x.Name == "propose_knowledge_items");
            SawProposalTool |= tool is not null;
            if (calls == 1)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        "proposal-1",
                        "propose_knowledge_items",
                        new Dictionary<string, object?>
                        {
                            ["envelope"] = new
                            {
                                items = new[]
                                {
                                    new
                                    {
                                        question = "加拿大签证需要多久？",
                                        answer = "请等待签证机关审核结果。",
                                        explicitTags = Array.Empty<string>(),
                                        suggestedTagId = (Guid?)null,
                                        similarVersionId = (Guid?)null,
                                        changeKind = "New"
                                    }
                                }
                            }
                        })])));
            }
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "done")));
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
        public void Dispose() { }
    }
}
