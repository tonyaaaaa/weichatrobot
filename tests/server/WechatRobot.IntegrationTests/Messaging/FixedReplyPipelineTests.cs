using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.Messaging;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.Infrastructure.WorkTool;
using WechatRobot.IntegrationTests.Infrastructure;
using WechatRobot.Worker.Jobs;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.Groups;
using WechatRobot.Application.Models;

namespace WechatRobot.IntegrationTests.Messaging;

public sealed class FixedReplyPipelineTests : IClassFixture<MySqlFixture>
{
    private readonly MySqlFixture _fixture;

    public FixedReplyPipelineTests(MySqlFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Two_workers_process_an_ingested_message_and_fake_endpoint_receives_one_fixed_reply()
    {
        var handler = new FakeWorkToolHandler();
        using var services = CreateServices(handler);
        Guid robotId;
        await using (var setupScope = services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var robot = new RobotConfigEntity { Name = "fixed-reply", WorkToolRobotId = "robot-fixed", CallbackSecretHash = "test" };
            db.RobotConfigs.Add(robot);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            var repository = setupScope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
            await repository.IngestInboundMessageAsync(new InboundMessageIngestRequest(robot.Id, "message-fixed", "fallback-fixed", DateTime.UtcNow, "Support", "Alice", "hello", DateTime.UtcNow), TestContext.Current.CancellationToken);
            robotId = robot.Id;
        }

        var jobWorker = new DurableJobWorker(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        var firstSendWorker = new RobotSendWorker(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        var secondSendWorker = new RobotSendWorker(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

        Assert.True(await jobWorker.ProcessOnceAsync(TestContext.Current.CancellationToken));
        await using (var processedScope = services.CreateAsyncScope())
        {
            var processedDb = processedScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
            Assert.Equal(1, await processedDb.SendCommands.CountAsync(command => command.RobotConfigId == robotId, TestContext.Current.CancellationToken));
            Assert.Equal(1, await processedDb.DurableJobs.CountAsync(job => job.Status == "completed" && job.PayloadJson.Contains(robotId.ToString()), TestContext.Current.CancellationToken));
        }
        await Task.WhenAll(firstSendWorker.ProcessOnceAsync(TestContext.Current.CancellationToken), secondSendWorker.ProcessOnceAsync(TestContext.Current.CancellationToken));
        await Task.WhenAll(firstSendWorker.ProcessOnceAsync(TestContext.Current.CancellationToken), secondSendWorker.ProcessOnceAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.SendCount);
        Assert.Equal("/wework/sendRawMessage?robotId=robot-fixed", handler.PathAndQuery);
        Assert.Contains("fixed reply", handler.Body, StringComparison.Ordinal);
        await using var verifyScope = services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        Assert.Equal(1, await verifyDb.SendCommands.CountAsync(command => command.RobotConfigId == robotId && command.Status == "completed", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Fourth_send_failure_creates_dead_letter_after_5_15_and_45_second_retries()
    {
        using var services = CreateServices(new FakeWorkToolHandler());
        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var robot = new RobotConfigEntity { Name = $"retry-{Guid.NewGuid():N}", WorkToolRobotId = $"retry-{Guid.NewGuid():N}", CallbackSecretHash = "test" };
        database.RobotConfigs.Add(robot);
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = scope.ServiceProvider.GetRequiredService<IDurableJobRepository>();
        await repository.EnqueueSendCommandAsync(new EnqueueSendCommandRequest(robot.Id, robot.WorkToolRobotId, "Support", "fixed reply", $"retry-{Guid.NewGuid():N}"), TestContext.Current.CancellationToken);

        var now = TruncateToMicroseconds(DateTime.UtcNow.AddSeconds(1));
        foreach (var expectedDelay in new[] { 5, 15, 45 })
        {
            var leased = await repository.LeaseNextSendCommandAsync("retry-worker", now, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
            Assert.NotNull(leased);
            await repository.FailSendCommandAsync(leased!, "fake failure", now, SendCommandService.GetRetryDelay(leased.AttemptCount + 1), TestContext.Current.CancellationToken);
            var command = await database.SendCommands.AsNoTracking().SingleAsync(value => value.Id == leased.Id, TestContext.Current.CancellationToken);
            Assert.Equal("retrying", command.Status);
            Assert.Equal(now.AddSeconds(expectedDelay), command.NextAttemptAtUtc);
            now = command.NextAttemptAtUtc.AddSeconds(1);
        }

        var fourth = await repository.LeaseNextSendCommandAsync("retry-worker", now, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.NotNull(fourth);
        await repository.FailSendCommandAsync(fourth!, "fake failure", now, SendCommandService.GetRetryDelay(fourth.AttemptCount + 1), TestContext.Current.CancellationToken);

        Assert.Equal(1, await database.SendCommands.CountAsync(value => value.Id == fourth!.Id && value.Status == "deadLetter", TestContext.Current.CancellationToken));
        Assert.Equal(1, await database.DeadLetters.CountAsync(value => value.SendCommandId == fourth!.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Saved_robot_rate_above_60_is_rejected()
    {
        using var services = CreateServices(new FakeWorkToolHandler());
        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<WechatRobotDbContext>();
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        database.RobotConfigs.Add(new RobotConfigEntity { Name = $"invalid-rate-{Guid.NewGuid():N}", WorkToolRobotId = $"invalid-rate-{Guid.NewGuid():N}", CallbackSecretHash = "test", SendRateLimitPerMinute = 61 });

        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    private static DateTime TruncateToMicroseconds(DateTime value) => new(value.Ticks - value.Ticks % 10, DateTimeKind.Utc);

    private ServiceProvider CreateServices(FakeWorkToolHandler handler) => new ServiceCollection()
        .AddDbContext<WechatRobotDbContext>(options => options.UseMySQL(_fixture.ConnectionString))
        .AddScoped<IDurableJobRepository, DurableJobRepository>()
        .AddScoped<SendCommandService>()
        .AddScoped<IGroundedConversationRepository, FakeConversationRepository>()
        .AddSingleton<ConversationContextService>()
        .AddSingleton<AnswerOutputFirewall>()
        .AddSingleton(new RetrievalQueryOptions())
        .AddSingleton<RetrievalQueryBuilder>()
        .AddSingleton<IConversationSummarizer, NoOpSummarizer>()
        .AddSingleton<IRetrievalEvidenceProvider, FakeEvidenceProvider>()
        .AddSingleton<IChatCompletionClient, FakeChatClient>()
        .AddSingleton(new GroundedAnswerOptions(.5, 8, "insufficient", "failure", "handoff"))
        .AddScoped<GroundedAnswerService>()
        .AddScoped<InboundMessageProcessor>()
        .AddSingleton<IWorkToolClient>(_ => new WorkToolClient(new HttpClient(handler) { BaseAddress = new Uri("https://fake.worktool.test/") }))
        .AddSingleton(TimeProvider.System)
        .AddOptions<FixedReplyOptions>()
        .Configure(options => options.Text = "fixed reply")
        .Services
        .BuildServiceProvider();

    private sealed class FakeConversationRepository(WechatRobotDbContext database, IDurableJobRepository jobs) : IGroundedConversationRepository
    {
        public async Task<ConversationProcessingRequest> LoadForProcessingAsync(Guid messageId, CancellationToken token)
        {
            var message = await database.ConversationMessages.SingleAsync(item => item.Id == messageId, token);
            var robot = await database.RobotConfigs.SingleAsync(item => item.Id == message.RobotConfigId, token);
            var scope = ConversationScopeResolver.Resolve(false, message.StableSenderId, message.Id);
            return new(message.Id, robot.Id, robot.WorkToolRobotId, Guid.NewGuid(), "Support", message.SenderDisplayName, message.StableSenderId, scope, message.Text,
                message.ReceivedAtUtc, [], [], null, new GroupContextSettings(false, 6, 30, 3000, true, true),
                new ModelProviderConfiguration("https://fake.test", "fake", "fake", TimeSpan.FromSeconds(1), 0));
        }

        public async Task<ConversationProcessingRequest> LeaseForProcessingAsync(Guid messageId, string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken token) =>
            (await LoadForProcessingAsync(messageId, token)) with { ConversationSessionId = Guid.NewGuid(), SessionLeaseOwner = leaseOwner, SessionVersion = 1 };
        public Task<bool> RenewLeaseAsync(Guid sessionId, string leaseOwner, DateTime nowUtc, TimeSpan leaseDuration, CancellationToken token) => Task.FromResult(true);
        public Task ReleaseLeaseAsync(Guid sessionId, string leaseOwner, CancellationToken token) => Task.CompletedTask;

        public async Task PersistAnswerAndEnqueueAsync(ConversationProcessingRequest request, GroundedAnswerResult result, CancellationToken token) =>
            _ = await jobs.EnqueueSendCommandAsync(new(request.RobotConfigId, request.WorkToolRobotId, request.GroupName, result.Decision.GroupText,
                $"grounded-reply:{request.MessageId:D}"), token);
        public Task<int> ClearGroupContextAsync(Guid groupProfileId, DateTime clearedAtUtc, CancellationToken token) => Task.FromResult(0);
        public Task<PageResult<ConversationPageItem>> GetHistoryAsync(Guid groupProfileId, int page, int pageSize, CancellationToken token) => throw new NotSupportedException();
        public Task<PageResult<RetrievalAuditPageItem>> GetAuditsAsync(Guid groupProfileId, int page, int pageSize, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class FakeEvidenceProvider : IRetrievalEvidenceProvider
    {
        public Task<IReadOnlyList<RetrievalEvidence>> RetrieveAsync(string question, IReadOnlyList<Guid> allowedTagIds, int limit, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<RetrievalEvidence>>([new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, .99, [], "fake", "fixed evidence")]);
    }

    private sealed class FakeChatClient : IChatCompletionClient
    {
        public Task<ChatCompletionResponse> CompleteAsync(ModelProviderConfiguration configuration, ChatCompletionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatCompletionResponse("fixed reply"));
    }

    private sealed class NoOpSummarizer : IConversationSummarizer
    {
        public Task<string> SummarizeAsync(ModelProviderConfiguration configuration, string? existingSummary, IReadOnlyList<ConversationHistoryMessage> evictedMessages, CancellationToken token) =>
            Task.FromResult(existingSummary ?? "summary");
    }

    private sealed class FakeWorkToolHandler : HttpMessageHandler
    {
        private int _sendCount;
        public int SendCount => _sendCount;
        public string? PathAndQuery { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sendCount);
            PathAndQuery = request.RequestUri!.PathAndQuery;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":0,\"message\":\"accepted\"}", Encoding.UTF8, "application/json") };
        }
    }
}
