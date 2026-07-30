using System.Security.Claims;
using WechatRobot.Application.FixedReplies;
using WechatRobot.Infrastructure.Identity;

namespace WechatRobot.Api.FixedReplies;

public static class FixedReplyTemplateEndpoints
{
    public static IEndpointRouteBuilder MapFixedReplyTemplateEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/fixed-reply-templates")
            .RequireAuthorization(SystemRoles.Admin);
        group.MapGet("", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("", CreateAsync);
        group.MapPut("/{id:guid}", UpdateAsync);
        group.MapPost("/{id:guid}/enable", EnableAsync);
        group.MapPost("/{id:guid}/disable", DisableAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);
        group.MapPut("/{id:guid}/group-rules", ReplaceGroupRulesAsync);
        group.MapPost("/preview", PreviewRouteAsync);

        endpoints.MapGet(
                "/api/admin/groups/{groupId:guid}/fixed-reply-templates",
                ListForGroupAsync)
            .RequireAuthorization(SystemRoles.Admin);
        endpoints.MapPost(
                "/api/admin/groups/{groupId:guid}/fixed-reply-templates/{templateId:guid}/include",
                IncludeForGroupAsync)
            .RequireAuthorization(SystemRoles.Admin);
        endpoints.MapDelete(
                "/api/admin/groups/{groupId:guid}/fixed-reply-templates/{templateId:guid}/include",
                RemoveIncludeForGroupAsync)
            .RequireAuthorization(SystemRoles.Admin);
        endpoints.MapPost(
                "/api/admin/groups/{groupId:guid}/fixed-reply-templates/{templateId:guid}/exclude",
                ExcludeForGroupAsync)
            .RequireAuthorization(SystemRoles.Admin);
        endpoints.MapDelete(
                "/api/admin/groups/{groupId:guid}/fixed-reply-templates/{templateId:guid}/exclude",
                RemoveExcludeForGroupAsync)
            .RequireAuthorization(SystemRoles.Admin);
        return endpoints;
    }

    private static async Task<IResult> PreviewRouteAsync(
        FixedReplyRoutePreviewRequest request,
        ITemplateRoutingAgent router,
        FixedReplyTemplateService service,
        CancellationToken cancellationToken)
    {
        if (request.GroupProfileId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.Question)
            || request.Question.Trim().Length > 2000)
        {
            return Validation("preview", "fixed_reply_preview_invalid");
        }
        var decision = await router.RouteAsync(
            request.GroupProfileId,
            request.Question.Trim(),
            cancellationToken);
        if (decision is not MatchFixedTemplate match)
        {
            return Results.Ok(new
            {
                matched = false,
                decision = "ContinueKnowledgeAnswer",
                reasonCode = (decision as ContinueKnowledgeAnswer)?.FailureCode
            });
        }
        var template = await service.GetAsync(match.TemplateId, cancellationToken);
        return template is null
            ? Results.Ok(new
            {
                matched = false,
                decision = "ContinueKnowledgeAnswer",
                reasonCode = "fixed_reply_stale_match"
            })
            : Results.Ok(new
            {
                matched = true,
                decision = "MatchFixedTemplate",
                templateId = template.Id,
                templateVersion = template.Version,
                templateName = template.Name,
                replyText = template.ReplyText
            });
    }

    private static async Task<IResult> ListAsync(
        FixedReplyTemplateService service,
        string? search,
        string? scopeType,
        bool? isEnabled,
        Guid? groupProfileId,
        int skip = 0,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (!TryScope(scopeType, out var scope))
        {
            return Validation("scopeType", "fixed_reply_scope_invalid");
        }
        return Results.Ok(await service.ListAsync(
            new FixedReplyTemplateQuery(
                search,
                scope,
                isEnabled,
                groupProfileId,
                skip,
                take),
            cancellationToken));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        FixedReplyTemplateService service,
        CancellationToken cancellationToken)
    {
        var template = await service.GetAsync(id, cancellationToken);
        return template is null ? Results.NotFound() : Results.Ok(template);
    }

    private static async Task<IResult> CreateAsync(
        FixedReplyTemplateRequest request,
        ClaimsPrincipal principal,
        FixedReplyTemplateService service,
        CancellationToken cancellationToken)
    {
        if (!TryActor(principal, out var actor))
        {
            return Results.Unauthorized();
        }
        if (!TryDraft(request, out var draft, out var failure))
        {
            return failure;
        }
        try
        {
            var created = await service.CreateAsync(draft, actor, cancellationToken);
            return Results.Created(
                $"/api/admin/fixed-reply-templates/{created.Id:D}",
                created);
        }
        catch (FixedReplyValidationException exception)
        {
            return Validation("template", exception.Code);
        }
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        FixedReplyTemplateUpdateRequest request,
        ClaimsPrincipal principal,
        FixedReplyTemplateService service,
        CancellationToken cancellationToken)
    {
        if (!TryActor(principal, out var actor))
        {
            return Results.Unauthorized();
        }
        if (!TryDraft(request.Template, out var draft, out var failure))
        {
            return failure;
        }
        return await MutationAsync(() => service.UpdateAsync(
            id,
            request.ExpectedVersion,
            draft,
            actor,
            cancellationToken));
    }

    private static Task<IResult> EnableAsync(
        Guid id,
        VersionRequest request,
        ClaimsPrincipal principal,
        FixedReplyTemplateService service,
        CancellationToken cancellationToken) =>
        SetEnabledAsync(id, request, principal, service, true, cancellationToken);

    private static Task<IResult> DisableAsync(
        Guid id,
        VersionRequest request,
        ClaimsPrincipal principal,
        FixedReplyTemplateService service,
        CancellationToken cancellationToken) =>
        SetEnabledAsync(id, request, principal, service, false, cancellationToken);

    private static async Task<IResult> SetEnabledAsync(
        Guid id,
        VersionRequest request,
        ClaimsPrincipal principal,
        FixedReplyTemplateService service,
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!TryActor(principal, out var actor))
        {
            return Results.Unauthorized();
        }
        return await MutationAsync(() => service.SetEnabledAsync(
            id,
            request.ExpectedVersion,
            enabled,
            actor,
            cancellationToken));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        int expectedVersion,
        ClaimsPrincipal principal,
        FixedReplyTemplateService service,
        CancellationToken cancellationToken)
    {
        if (!TryActor(principal, out var actor))
        {
            return Results.Unauthorized();
        }
        try
        {
            await service.DeleteAsync(
                id,
                expectedVersion,
                actor,
                cancellationToken);
            return Results.NoContent();
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (FixedReplyConcurrencyException)
        {
            return Results.Conflict(new
            {
                code = "fixed_reply_concurrency_conflict"
            });
        }
    }

    private static async Task<IResult> ReplaceGroupRulesAsync(
        Guid id,
        GroupRulesRequest request,
        ClaimsPrincipal principal,
        FixedReplyTemplateService service,
        CancellationToken cancellationToken)
    {
        var current = await service.GetAsync(id, cancellationToken);
        if (current is null)
        {
            return Results.NotFound();
        }
        return await UpdateAsync(
            id,
            new FixedReplyTemplateUpdateRequest(
                request.ExpectedVersion,
                current.Name,
                current.IntentDescription,
                current.ReplyText,
                current.ScopeType.ToString(),
                current.Priority,
                current.IsEnabled,
                current.Examples,
                request.GroupRules),
            principal,
            service,
            cancellationToken);
    }

    private static async Task<IResult> ListForGroupAsync(
        Guid groupId,
        FixedReplyTemplateService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListEffectiveAsync(
            groupId,
            cancellationToken: cancellationToken));

    private static Task<IResult> IncludeForGroupAsync(
        Guid groupId,
        Guid templateId,
        VersionRequest request,
        ClaimsPrincipal principal,
        FixedReplyTemplateService service,
        CancellationToken cancellationToken) =>
        ChangeGroupRuleAsync(
            groupId,
            templateId,
            request,
            principal,
            service,
            FixedReplyGroupEffect.Include,
            remove: false,
            cancellationToken);

    private static Task<IResult> RemoveIncludeForGroupAsync(
        Guid groupId,
        Guid templateId,
        int expectedVersion,
        ClaimsPrincipal principal,
        FixedReplyTemplateService service,
        CancellationToken cancellationToken) =>
        ChangeGroupRuleAsync(
            groupId,
            templateId,
            new VersionRequest(expectedVersion),
            principal,
            service,
            FixedReplyGroupEffect.Include,
            remove: true,
            cancellationToken);

    private static Task<IResult> ExcludeForGroupAsync(
        Guid groupId,
        Guid templateId,
        VersionRequest request,
        ClaimsPrincipal principal,
        FixedReplyTemplateService service,
        CancellationToken cancellationToken) =>
        ChangeGroupRuleAsync(
            groupId,
            templateId,
            request,
            principal,
            service,
            FixedReplyGroupEffect.Exclude,
            remove: false,
            cancellationToken);

    private static Task<IResult> RemoveExcludeForGroupAsync(
        Guid groupId,
        Guid templateId,
        int expectedVersion,
        ClaimsPrincipal principal,
        FixedReplyTemplateService service,
        CancellationToken cancellationToken) =>
        ChangeGroupRuleAsync(
            groupId,
            templateId,
            new VersionRequest(expectedVersion),
            principal,
            service,
            FixedReplyGroupEffect.Exclude,
            remove: true,
            cancellationToken);

    private static async Task<IResult> ChangeGroupRuleAsync(
        Guid groupId,
        Guid templateId,
        VersionRequest request,
        ClaimsPrincipal principal,
        FixedReplyTemplateService service,
        FixedReplyGroupEffect effect,
        bool remove,
        CancellationToken cancellationToken)
    {
        var current = await service.GetAsync(templateId, cancellationToken);
        if (current is null)
        {
            return Results.NotFound();
        }
        var rules = current.GroupRules
            .Where(rule => rule.GroupProfileId != groupId)
            .ToList();
        if (!remove)
        {
            rules.Add(new FixedReplyGroupRuleInput(groupId, effect));
        }
        return await UpdateAsync(
            templateId,
            new FixedReplyTemplateUpdateRequest(
                request.ExpectedVersion,
                current.Name,
                current.IntentDescription,
                current.ReplyText,
                current.ScopeType.ToString(),
                current.Priority,
                current.IsEnabled,
                current.Examples,
                rules),
            principal,
            service,
            cancellationToken);
    }

    private static async Task<IResult> MutationAsync(
        Func<Task<FixedReplyTemplateView>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (FixedReplyValidationException exception)
        {
            return Validation("template", exception.Code);
        }
        catch (FixedReplyConcurrencyException)
        {
            return Results.Conflict(new
            {
                code = "fixed_reply_concurrency_conflict"
            });
        }
    }

    private static bool TryDraft(
        FixedReplyTemplateRequest request,
        out FixedReplyTemplateDraft draft,
        out IResult failure)
    {
        if (!Enum.TryParse<FixedReplyScopeType>(
                request.ScopeType,
                ignoreCase: true,
                out var scope))
        {
            draft = null!;
            failure = Validation("scopeType", "fixed_reply_scope_invalid");
            return false;
        }
        draft = new FixedReplyTemplateDraft(
            request.Name,
            request.IntentDescription,
            request.ReplyText,
            scope,
            request.Priority,
            request.IsEnabled,
            request.Examples,
            request.GroupRules);
        failure = null!;
        return true;
    }

    private static bool TryScope(
        string? value,
        out FixedReplyScopeType? scope)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            scope = null;
            return true;
        }
        if (Enum.TryParse<FixedReplyScopeType>(
                value,
                ignoreCase: true,
                out var parsed))
        {
            scope = parsed;
            return true;
        }
        scope = null;
        return false;
    }

    private static bool TryActor(ClaimsPrincipal principal, out Guid actor) =>
        Guid.TryParse(
            principal.FindFirstValue(ClaimTypes.NameIdentifier),
            out actor);

    private static IResult Validation(string key, string code) =>
        Results.ValidationProblem(
            new Dictionary<string, string[]> { [key] = [code] });

    public sealed record FixedReplyTemplateRequest(
        string Name,
        string IntentDescription,
        string ReplyText,
        string ScopeType,
        int Priority,
        bool IsEnabled,
        IReadOnlyList<string> Examples,
        IReadOnlyList<FixedReplyGroupRuleInput> GroupRules);

    public sealed record FixedReplyTemplateUpdateRequest(
        int ExpectedVersion,
        string Name,
        string IntentDescription,
        string ReplyText,
        string ScopeType,
        int Priority,
        bool IsEnabled,
        IReadOnlyList<string> Examples,
        IReadOnlyList<FixedReplyGroupRuleInput> GroupRules)
    {
        public FixedReplyTemplateRequest Template =>
            new(
                Name,
                IntentDescription,
                ReplyText,
                ScopeType,
                Priority,
                IsEnabled,
                Examples,
                GroupRules);
    }

    public sealed record VersionRequest(int ExpectedVersion);
    public sealed record FixedReplyRoutePreviewRequest(
        Guid GroupProfileId,
        string Question);

    public sealed record GroupRulesRequest(
        int ExpectedVersion,
        IReadOnlyList<FixedReplyGroupRuleInput> GroupRules);
}
