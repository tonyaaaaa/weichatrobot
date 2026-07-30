using WechatRobot.Application.Jobs;
using WechatRobot.Application.Messaging;
using WechatRobot.Application.WorkTool;

namespace WechatRobot.UnitTests.Messaging;

public sealed class MessageDeduplicationTests
{
    [Fact]
    public async Task Ingest_preserves_message_line_breaks_for_private_chat_commands()
    {
        var repository = new CapturingDurableJobRepository();
        var service = new InboundMessageService(
            repository,
            TimeProvider.System,
            TimeSpan.FromMinutes(5));
        var callback = new WorkToolCallbackDto
        {
            MessageId = "private-ingest-1",
            RoomType = 4,
            TextType = 1,
            ReceivedName = "Internal User",
            Spoken = "#知识入库\r\n问题：测试编号是什么？\r\n答案：KB-20260730。"
        };

        await service.IngestAsync(
            Guid.NewGuid(),
            "robot-scope",
            callback,
            TestContext.Current.CancellationToken);

        Assert.NotNull(repository.Request);
        Assert.Equal(
            "#知识入库\n问题：测试编号是什么？\n答案：KB-20260730。",
            repository.Request.Text);
    }

    [Fact]
    public void Message_id_is_preferred_over_fallback_deduplication_key()
    {
        var method = GetCreateDeduplicationKey();
        var key = method.Invoke(null, ["worktool-a", "message-123", "Support", null, "Alice", "  Hello   world ", new DateTime(2026, 7, 21, 8, 12, 34, DateTimeKind.Utc), TimeSpan.FromMinutes(5)])!;

        Assert.Equal("message:message-123", GetProperty<string>(key, "Key"));
        Assert.Null(GetProperty<DateTime?>(key, "FallbackWindowStartUtc"));
    }

    [Fact]
    public void Missing_message_id_uses_normalized_values_and_time_bucket_for_fallback_hash()
    {
        var timestamp = new DateTime(2026, 7, 21, 8, 12, 34, DateTimeKind.Utc);
        var method = GetCreateDeduplicationKey();
        var first = method.Invoke(null, ["worktool-a", null, "Support", null, "Alice", "  Hello   world ", timestamp, TimeSpan.FromMinutes(5)])!;
        var equivalent = method.Invoke(null, ["worktool-a", " ", "Support", null, "Alice", "Hello world", timestamp.AddMinutes(2), TimeSpan.FromMinutes(5)])!;
        var later = method.Invoke(null, ["worktool-a", null, "Support", null, "Alice", "Hello world", timestamp.AddMinutes(5), TimeSpan.FromMinutes(5)])!;

        Assert.StartsWith("fallback:", GetProperty<string>(first, "Key"), StringComparison.Ordinal);
        Assert.Equal(GetProperty<string>(first, "Key"), GetProperty<string>(equivalent, "Key"));
        Assert.Equal(GetProperty<DateTime?>(first, "FallbackWindowStartUtc"), GetProperty<DateTime?>(equivalent, "FallbackWindowStartUtc"));
        Assert.NotEqual(GetProperty<string>(first, "Key"), GetProperty<string>(later, "Key"));
        Assert.NotEqual(GetProperty<DateTime?>(first, "FallbackWindowStartUtc"), GetProperty<DateTime?>(later, "FallbackWindowStartUtc"));
    }

    [Fact]
    public void Missing_message_id_keeps_same_name_groups_with_distinct_remarks_separate()
    {
        var timestamp = new DateTime(2026, 7, 21, 8, 12, 34, DateTimeKind.Utc);
        var method = GetCreateDeduplicationKey();
        var east = method.Invoke(null, ["worktool-a", null, "Support", "support-east", "Alice", "Hello", timestamp, TimeSpan.FromMinutes(5)])!;
        var west = method.Invoke(null, ["worktool-a", null, "Support", "support-west", "Alice", "Hello", timestamp, TimeSpan.FromMinutes(5)])!;

        Assert.NotEqual(GetProperty<string>(east, "Key"), GetProperty<string>(west, "Key"));
    }

    private static System.Reflection.MethodInfo GetCreateDeduplicationKey()
    {
        var type = Type.GetType("WechatRobot.Application.Messaging.InboundMessageService, WechatRobot.Application");
        Assert.NotNull(type);
        var method = type.GetMethod("CreateDeduplicationKey", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return method;
    }

    private static T? GetProperty<T>(object value, string name) => (T?)value.GetType().GetProperty(name)?.GetValue(value);

    private sealed class CapturingDurableJobRepository : IDurableJobRepository
    {
        public InboundMessageIngestRequest? Request { get; private set; }

        public Task<InboundMessageIngestResult> IngestInboundMessageAsync(
            InboundMessageIngestRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(InboundMessageIngestResult.Accepted);
        }

        public Task<LeasedDurableJob?> LeaseNextJobAsync(
            string jobType,
            string leaseOwner,
            DateTime nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CompleteJobAsync(Guid jobId, string leaseOwner, DateTime completedAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task FailJobAsync(LeasedDurableJob job, string reason, DateTime failedAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnqueueSendCommandResult> EnqueueSendCommandAsync(
            EnqueueSendCommandRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LeasedSendCommand?> LeaseNextSendCommandAsync(
            string leaseOwner,
            DateTime nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> MarkSendDispatchingAsync(
            LeasedSendCommand command,
            DateTime dispatchedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MarkSendDeliveryUnknownAsync(
            LeasedSendCommand command,
            string reason,
            DateTime failedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MarkSendRejectedAsync(
            LeasedSendCommand command,
            string reason,
            DateTime rejectedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task MarkSendAcceptedAsync(
            LeasedSendCommand command,
            string workToolMessageId,
            DateTime acceptedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task FailSendCommandAsync(
            LeasedSendCommand command,
            string reason,
            DateTime failedAtUtc,
            TimeSpan? retryDelay,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> RenewSendLeasesAsync(
            LeasedSendCommand command,
            DateTime nowUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
