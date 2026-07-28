using System.Security.Cryptography;
using System.Text;
using WechatRobot.Application.Groups;

namespace WechatRobot.Application.Conversations;

public sealed record ConversationContextMessagePreview(
    string Role,
    string Content,
    DateTime CreatedAtUtc);

public sealed record ConversationContextSessionItem(
    Guid SessionId,
    string SenderDisplayName,
    string Scope,
    string? Summary,
    DateTime? ClearedAtUtc,
    long ClearedThroughSequence,
    DateTime LastActivityAtUtc,
    int Version,
    IReadOnlyList<ConversationContextMessagePreview> Messages,
    bool WasIdleReset,
    bool WasTokenLimited,
    int ContextTokenCount);

public sealed record GroupConversationContextPage(
    Guid GroupId,
    int ConfigurationVersion,
    IReadOnlyList<ConversationContextSessionItem> Items,
    int Total,
    int Page,
    int PageSize);

public sealed class ConversationContextQueryService(
    IGroundedConversationRepository repository,
    GroupConfigurationService groupConfiguration,
    ConversationContextService contextService,
    TimeProvider timeProvider)
{
    public async Task<GroupConversationContextPage?> GetAsync(
        Guid groupId,
        int page,
        int pageSize,
        CancellationToken token)
    {
        var source = await repository.GetGroupContextAsync(groupId, page, pageSize, token);
        if (source is null) return null;

        var policy = groupConfiguration.GetEffectiveContext(source.ConfiguredContext);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var items = source.Items.Select(session =>
        {
            var context = contextService.Build(session.Messages, policy, session.SenderScopeKey, now, session.Summary);
            return new ConversationContextSessionItem(
                session.SessionId,
                session.SenderDisplayName,
                RedactScope(session.SenderScopeKey),
                context.Summary,
                session.ClearedAtUtc,
                session.ClearedThroughSequence,
                session.LastActivityAtUtc,
                session.Version,
                context.Messages.Select(message => new ConversationContextMessagePreview(
                    message.Role,
                    message.Content,
                    message.CreatedAtUtc)).ToArray(),
                context.WasIdleReset,
                context.WasTokenLimited,
                context.ContextTokenCount);
        }).ToArray();
        return new(
            source.GroupId,
            source.ConfigurationVersion,
            items,
            source.Total,
            source.Page,
            source.PageSize);
    }

    public Task<ClearConversationContextResult> ClearAsync(
        Guid groupId,
        int expectedConfigurationVersion,
        CancellationToken token) =>
        repository.ClearGroupContextAsync(
            groupId,
            expectedConfigurationVersion,
            timeProvider.GetUtcNow().UtcDateTime,
            token);

    private static string RedactScope(string scope)
    {
        if (string.Equals(scope, "group", StringComparison.Ordinal)) return "group";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(scope));
        return $"sender:{Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant()}";
    }
}
