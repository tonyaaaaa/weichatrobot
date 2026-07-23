using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.WorkTool;

namespace WechatRobot.Application.Messaging;

public sealed class InboundMessageService(IDurableJobRepository durableJobs, TimeProvider timeProvider, TimeSpan fallbackDeduplicationWindow)
{
    private static readonly Regex Whitespace = new("\\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<InboundMessageIngestResult> IngestAsync(Guid robotConfigId, string robotDeduplicationScope, WorkToolCallbackDto callback, CancellationToken cancellationToken)
    {
        var receivedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var deduplication = CreateDeduplicationKey(
            robotDeduplicationScope,
            callback.MessageId,
            callback.GroupName!,
            callback.ReceivedName!,
            callback.Spoken!,
            receivedAtUtc,
            fallbackDeduplicationWindow);
        var fallbackWindowStartUtc = deduplication.FallbackWindowStartUtc ?? FloorToWindow(receivedAtUtc, fallbackDeduplicationWindow);
        var fallbackHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deduplication.Key)));

        return await durableJobs.IngestInboundMessageAsync(new(
            robotConfigId,
            Normalize(callback.MessageId) ?? string.Empty,
            fallbackHash,
            fallbackWindowStartUtc,
            Normalize(callback.GroupName)!,
            Normalize(callback.ReceivedName)!,
            Normalize(callback.Spoken)!,
            receivedAtUtc,
            Normalize(callback.ConnectorStableSenderId),
            callback.AtMe == true), cancellationToken);
    }

    public static DeduplicationKey CreateDeduplicationKey(
        string robotCode,
        string? messageId,
        string groupName,
        string senderName,
        string text,
        DateTime receivedAtUtc,
        TimeSpan timeBucket)
    {
        var normalizedMessageId = Normalize(messageId);
        if (normalizedMessageId is not null)
        {
            return new($"message:{normalizedMessageId}", null);
        }

        var windowStart = FloorToWindow(receivedAtUtc, timeBucket);
        var stableInput = string.Join("\n", Normalize(robotCode), Normalize(groupName), Normalize(senderName), Normalize(text), windowStart.Ticks);
        return new($"fallback:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableInput)))}", windowStart);
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Whitespace.Replace(value.Trim(), " ");
    }

    private static DateTime FloorToWindow(DateTime timestampUtc, TimeSpan timeBucket)
    {
        if (timeBucket <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeBucket));
        }

        var utc = timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc : timestampUtc.ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % timeBucket.Ticks, DateTimeKind.Utc);
    }
}

public sealed record DeduplicationKey(string Key, DateTime? FallbackWindowStartUtc);
