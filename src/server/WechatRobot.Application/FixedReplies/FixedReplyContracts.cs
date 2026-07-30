using System.Text.Json.Serialization;

namespace WechatRobot.Application.FixedReplies;

[JsonConverter(typeof(JsonStringEnumConverter<FixedReplyScopeType>))]
public enum FixedReplyScopeType
{
    Global,
    SelectedGroups
}

public enum FixedReplyGroupEffect
{
    Include,
    Exclude
}

public sealed record FixedReplyGroupRuleInput(
    Guid GroupProfileId,
    [property: JsonConverter(
        typeof(JsonStringEnumConverter<FixedReplyGroupEffect>))]
    FixedReplyGroupEffect Effect);

public sealed record FixedReplyTemplateDraft(
    string Name,
    string IntentDescription,
    string ReplyText,
    FixedReplyScopeType ScopeType,
    int Priority,
    bool IsEnabled,
    IReadOnlyList<string> Examples,
    IReadOnlyList<FixedReplyGroupRuleInput> GroupRules);

public sealed record ValidatedFixedReplyTemplate(
    string Name,
    string IntentDescription,
    string ReplyText,
    FixedReplyScopeType ScopeType,
    int Priority,
    bool IsEnabled,
    IReadOnlyList<string> Examples,
    IReadOnlyList<FixedReplyGroupRuleInput> GroupRules);

public sealed record FixedReplyTemplateView(
    Guid Id,
    string Name,
    string IntentDescription,
    string ReplyText,
    FixedReplyScopeType ScopeType,
    int Priority,
    bool IsEnabled,
    int Version,
    IReadOnlyList<string> Examples,
    IReadOnlyList<FixedReplyGroupRuleInput> GroupRules,
    Guid CreatedByUserId,
    Guid UpdatedByUserId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    DateTime? DeletedAtUtc);

public sealed record FixedReplyTemplateQuery(
    string? Search = null,
    FixedReplyScopeType? ScopeType = null,
    bool? IsEnabled = null,
    Guid? GroupProfileId = null,
    int Skip = 0,
    int Take = 100);

public sealed record EffectiveFixedReply(
    Guid Id,
    int Version,
    string Name,
    string IntentDescription,
    IReadOnlyList<string> Examples,
    int Priority,
    bool IsGroupSpecific);

public sealed record ResolvedFixedReply(
    Guid Id,
    int Version,
    string Name,
    string ReplyText,
    bool IsGroupSpecific);

public sealed class FixedReplyValidationException(string code, string message)
    : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class FixedReplyConcurrencyException()
    : Exception("The fixed reply template was modified by another operator.");
