namespace WechatRobot.UnitTests.Messaging;

public sealed class MessageDeduplicationTests
{
    [Fact]
    public void Message_id_is_preferred_over_fallback_deduplication_key()
    {
        var method = GetCreateDeduplicationKey();
        var key = method.Invoke(null, ["worktool-a", "message-123", "Support", "Alice", "  Hello   world ", new DateTime(2026, 7, 21, 8, 12, 34, DateTimeKind.Utc), TimeSpan.FromMinutes(5)])!;

        Assert.Equal("message:message-123", GetProperty<string>(key, "Key"));
        Assert.Null(GetProperty<DateTime?>(key, "FallbackWindowStartUtc"));
    }

    [Fact]
    public void Missing_message_id_uses_normalized_values_and_time_bucket_for_fallback_hash()
    {
        var timestamp = new DateTime(2026, 7, 21, 8, 12, 34, DateTimeKind.Utc);
        var method = GetCreateDeduplicationKey();
        var first = method.Invoke(null, ["worktool-a", null, "Support", "Alice", "  Hello   world ", timestamp, TimeSpan.FromMinutes(5)])!;
        var equivalent = method.Invoke(null, ["worktool-a", " ", "Support", "Alice", "Hello world", timestamp.AddMinutes(2), TimeSpan.FromMinutes(5)])!;
        var later = method.Invoke(null, ["worktool-a", null, "Support", "Alice", "Hello world", timestamp.AddMinutes(5), TimeSpan.FromMinutes(5)])!;

        Assert.StartsWith("fallback:", GetProperty<string>(first, "Key"), StringComparison.Ordinal);
        Assert.Equal(GetProperty<string>(first, "Key"), GetProperty<string>(equivalent, "Key"));
        Assert.Equal(GetProperty<DateTime?>(first, "FallbackWindowStartUtc"), GetProperty<DateTime?>(equivalent, "FallbackWindowStartUtc"));
        Assert.NotEqual(GetProperty<string>(first, "Key"), GetProperty<string>(later, "Key"));
        Assert.NotEqual(GetProperty<DateTime?>(first, "FallbackWindowStartUtc"), GetProperty<DateTime?>(later, "FallbackWindowStartUtc"));
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
}
