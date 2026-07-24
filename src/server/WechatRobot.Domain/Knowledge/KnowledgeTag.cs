namespace WechatRobot.Domain.Knowledge;

public sealed record KnowledgeTag(
    Guid Id,
    string Name,
    bool IsEnabled = true,
    bool IsGlobalPublic = false);
