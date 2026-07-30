using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.WorkTool;
using WechatRobot.Application.Conversations;

namespace WechatRobot.Application.Messaging;

public sealed class InboundMessageService(IDurableJobRepository durableJobs, TimeProvider timeProvider, TimeSpan fallbackDeduplicationWindow)
{
    private static readonly Regex Whitespace = new("\\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<InboundMessageIngestResult> IngestAsync(Guid robotConfigId, string robotDeduplicationScope, WorkToolCallbackDto callback, CancellationToken cancellationToken)
    {
        var receivedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var isPrivate = callback.RoomType is 2 or 4;
        var privateScope = isPrivate
            ? PrivateConversationScope.Create(
                robotConfigId,
                callback.RoomType!.Value,
                callback.ReceivedName!)
            : null;
        var deduplication = CreateDeduplicationKey(
            robotDeduplicationScope,
            callback.MessageId,
            isPrivate ? privateScope!.ScopeHash : callback.GroupName!,
            callback.GroupRemark,
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
            Normalize(callback.GroupName) ?? string.Empty,
            Normalize(callback.GroupRemark),
            Normalize(callback.ReceivedName)!,
            NormalizeMessageText(callback.Spoken)!,
            receivedAtUtc,
            Normalize(callback.ConnectorStableSenderId),
            callback.AtMe == true,
            isPrivate ? "Private" : "Group",
            callback.RoomType,
            privateScope?.PeerDisplayName,
            privateScope?.ScopeHash), cancellationToken);
    }

    public static DeduplicationKey CreateDeduplicationKey(
        string robotCode,
        string? messageId,
        string groupName,
        string? groupRemark,
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
        var stableInput = string.Join("\n", Normalize(robotCode), Normalize(groupName), Normalize(groupRemark), Normalize(senderName), Normalize(text), windowStart.Ticks);
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

    private static string? NormalizeMessageText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
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
