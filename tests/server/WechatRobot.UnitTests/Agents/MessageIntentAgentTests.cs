using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using WechatRobot.Application.Agents;
using WechatRobot.Infrastructure.Agents;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.UnitTests.Agents;

public sealed class MessageIntentAgentTests
{
    [Fact]
    public async Task Terminal_tool_returns_a_typed_reply_decision_from_only_the_current_group_window()
    {
        await using var database = Database();
        var modelId = Guid.NewGuid();
        var robotId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var otherGroupId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        database.ModelConfigs.Add(Model(modelId));
        database.ConversationMessages.AddRange(
            Message(robotId, groupId, Guid.NewGuid(), "张三", "上一条群消息", DateTime.UtcNow.AddMinutes(-2)),
            Message(robotId, otherGroupId, Guid.NewGuid(), "李四", "不应泄漏到当前群", DateTime.UtcNow.AddMinutes(-1)),
            Message(robotId, groupId, currentId, "张三", "机器人，请帮我查签证进度", DateTime.UtcNow));
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var client = new IntentChatClient("Reply", "DirectedToBot", "explicitly_addresses_bot", .94m);
        var agent = new MessageIntentAgent(
            database,
            new StubFactory(client),
            Options.Create(new AgentRuntimeOptions
            {
                IntentRuntimeMode = IntentRuntimeMode.Shadow,
                IntentMinimumConfidence = .8m
            }));

        var result = await agent.DecideAsync(
            new MessageIntentRequest(currentId, groupId, true),
            TestContext.Current.CancellationToken);

        Assert.Equal(IntentDecision.Reply, result.Decision);
        Assert.Equal(IntentCategory.DirectedToBot, result.Category);
        Assert.Equal("explicitly_addresses_bot", result.ReasonCode);
        Assert.Contains("上一条群消息", client.LastPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("不应泄漏到当前群", client.LastPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Low_confidence_or_invalid_output_fails_closed()
    {
        await using var database = Database();
        var modelId = Guid.NewGuid();
        var robotId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var currentId = Guid.NewGuid();
        database.ModelConfigs.Add(Model(modelId));
        database.ConversationMessages.Add(
            Message(robotId, groupId, currentId, "张三", "这个呢", DateTime.UtcNow));
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var agent = new MessageIntentAgent(
            database,
            new StubFactory(new IntentChatClient(
                "Reply",
                "FollowUpToBot",
                "continues_recent_bot_turn",
                .4m)),
            Options.Create(new AgentRuntimeOptions
            {
                IntentRuntimeMode = IntentRuntimeMode.Shadow,
                IntentMinimumConfidence = .8m
            }));

        var result = await agent.DecideAsync(
            new MessageIntentRequest(currentId, groupId, false),
            TestContext.Current.CancellationToken);

        Assert.Equal(IntentDecision.Uncertain, result.Decision);
        Assert.Equal("intent_agent_uncertain", result.FailureCode);
    }

    private static WechatRobotDbContext Database() =>
        new(new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ModelConfigEntity Model(Guid id) => new()
    {
        Id = id,
        Name = "chat",
        NormalizedName = "CHAT",
        Provider = "OpenAI",
        ConfigurationType = "chat",
        BaseUrl = "https://example.test",
        Model = "test",
        IsEnabled = true,
        IsDefault = true,
        Version = 3
    };

    private static ConversationMessageEntity Message(
        Guid robotId,
        Guid groupId,
        Guid id,
        string sender,
        string text,
        DateTime atUtc) => new()
    {
        Id = id,
        RobotConfigId = robotId,
        GroupProfileId = groupId,
        FallbackHash = id.ToString("N"),
        FallbackWindowStartUtc = DateTime.UnixEpoch,
        GroupName = "测试群",
        SenderDisplayName = sender,
        Text = text,
        ReceivedAtUtc = atUtc,
        CreatedAtUtc = atUtc
    };

    private sealed class StubFactory(IChatClient client) : IAgentChatClientFactory
    {
        public Task<IChatClient> CreateAsync(
            Guid modelConfigurationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(client);
    }

    private sealed class IntentChatClient(
        string decision,
        string category,
        string reasonCode,
        decimal confidence) : IChatClient
    {
        private int calls;
        public string LastPrompt { get; private set; } = string.Empty;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            calls++;
            LastPrompt = string.Join('\n', messages.SelectMany(x => x.Contents)
                .OfType<TextContent>().Select(x => x.Text));
            if (calls == 1)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        "intent-1",
                        "submit_intent_decision",
                        new Dictionary<string, object?>
                        {
                            ["decision"] = decision,
                            ["category"] = category,
                            ["reasonCode"] = reasonCode,
                            ["confidence"] = confidence
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
