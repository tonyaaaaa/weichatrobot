using Microsoft.AspNetCore.Mvc;
using WechatRobot.Application.Agents;
using WechatRobot.Infrastructure.Identity;

namespace WechatRobot.Api.Agents;

public static class AgentDiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapAgentDiagnosticsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/agent-diagnostics")
            .RequireAuthorization(policy => policy.RequireRole(SystemRoles.Admin));
        group.MapGet("", ListAsync);
        group.MapGet("runtime", RuntimeAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        [FromQuery] Guid? groupId,
        [FromQuery] IntentRuntimeMode? runtimeMode,
        [FromQuery] IntentDecision? decision,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        IMessageIntentDiagnosticsQuery query,
        CancellationToken cancellationToken)
    {
        if (page < 0 || pageSize < 0 || pageSize > 100
            || fromUtc is not null && toUtc is not null && fromUtc >= toUtc)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["query"] = ["Invalid diagnostics query."]
            });
        }
        return Results.Ok(await query.ListAsync(
            new(
                groupId,
                runtimeMode,
                decision,
                fromUtc,
                toUtc,
                page == 0 ? 1 : page,
                pageSize == 0 ? 20 : pageSize),
            cancellationToken));
    }

    private static IResult RuntimeAsync(AgentRuntimeOptions options) =>
        Results.Ok(new
        {
            intentRuntimeMode = options.IntentRuntimeMode.ToString(),
            answerRuntimeMode = options.AnswerRuntimeMode.ToString(),
            privateChatRuntimeMode = options.PrivateChatRuntimeMode.ToString(),
            templateRoutingRuntimeMode = options.TemplateRoutingRuntimeMode.ToString(),
            intentModelConfigurationId = options.IntentModelConfigurationId,
            intentMinimumConfidence = options.IntentMinimumConfidence,
            intentHistoryMessageCount = options.IntentHistoryMessageCount,
            intentHistoryMinutes = options.IntentHistoryMinutes
        });
}
