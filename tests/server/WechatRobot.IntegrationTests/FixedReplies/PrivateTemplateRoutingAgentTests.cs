using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using WechatRobot.Application.Agents;
using WechatRobot.Application.FixedReplies;
using WechatRobot.Infrastructure.Agents;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.IntegrationTests.FixedReplies;

public sealed class PrivateTemplateRoutingAgentTests
{
    [Fact]
    public async Task Private_route_reads_private_candidates_without_using_group_scope()
    {
        await using var database = new WechatRobotDbContext(
            new DbContextOptionsBuilder<WechatRobotDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        var store = new RecordingStore();
        var agent = new TemplateRoutingAgent(
            new FixedReplyTemplateService(store, TimeProvider.System),
            new ThrowingFactory(),
            database);

        var result = await agent.RoutePrivateAsync(
            "签证还有多久？",
            TestContext.Current.CancellationToken);

        Assert.True(store.PrivateListCalled);
        Assert.False(store.GroupListCalled);
        Assert.Equal(
            "fixed_reply_no_candidates",
            Assert.IsType<ContinueKnowledgeAnswer>(result).FailureCode);
    }

    private sealed class RecordingStore : IFixedReplyTemplateStore
    {
        public bool PrivateListCalled { get; private set; }
        public bool GroupListCalled { get; private set; }

        public Task<IReadOnlyList<EffectiveFixedReply>> ListEffectiveForPrivateAsync(
            int maximumCandidates,
            int examplesPerTemplate,
            CancellationToken cancellationToken)
        {
            PrivateListCalled = true;
            return Task.FromResult<IReadOnlyList<EffectiveFixedReply>>([]);
        }

        public Task<IReadOnlyList<EffectiveFixedReply>> ListEffectiveAsync(
            Guid groupProfileId,
            int maximumCandidates,
            int examplesPerTemplate,
            CancellationToken cancellationToken)
        {
            GroupListCalled = true;
            return Task.FromResult<IReadOnlyList<EffectiveFixedReply>>([]);
        }

        public Task<ResolvedFixedReply?> ResolveForPrivateAsync(
            Guid templateId,
            int expectedVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult<ResolvedFixedReply?>(null);

        public Task<ResolvedFixedReply?> ResolveAsync(
            Guid templateId,
            int expectedVersion,
            Guid groupProfileId,
            CancellationToken cancellationToken) =>
            Task.FromResult<ResolvedFixedReply?>(null);

        public Task<IReadOnlyList<FixedReplyTemplateView>> ListAsync(
            FixedReplyTemplateQuery query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<FixedReplyTemplateView?> GetAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<FixedReplyTemplateView> CreateAsync(
            ValidatedFixedReplyTemplate template,
            Guid actorUserId,
            DateTime nowUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<FixedReplyTemplateView> UpdateAsync(
            Guid id,
            int expectedVersion,
            ValidatedFixedReplyTemplate template,
            Guid actorUserId,
            DateTime nowUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<FixedReplyTemplateView> SetEnabledAsync(
            Guid id,
            int expectedVersion,
            bool enabled,
            Guid actorUserId,
            DateTime nowUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(
            Guid id,
            int expectedVersion,
            Guid actorUserId,
            DateTime nowUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingFactory : IAgentChatClientFactory
    {
        public Task<IChatClient> CreateAsync(
            Guid modelConfigurationId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No candidates must not call the model.");
    }
}
