namespace WechatRobot.Application.FixedReplies;

public interface ITemplateRoutingAgent
{
    Task<TemplateRouteDecision> RouteAsync(
        Guid groupProfileId,
        string message,
        CancellationToken cancellationToken);

    Task<TemplateRouteDecision> RoutePrivateAsync(
        string message,
        CancellationToken cancellationToken);
}
