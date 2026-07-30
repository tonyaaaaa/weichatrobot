namespace WechatRobot.Application.FixedReplies;

public abstract record TemplateRouteDecision;

public sealed record MatchFixedTemplate(Guid TemplateId, int ExpectedVersion)
    : TemplateRouteDecision;

public sealed record ContinueKnowledgeAnswer(string? FailureCode = null)
    : TemplateRouteDecision;
