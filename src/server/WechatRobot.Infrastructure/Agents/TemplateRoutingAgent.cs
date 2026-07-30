using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using WechatRobot.Application.Agents;
using WechatRobot.Application.FixedReplies;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Infrastructure.Agents;

public sealed class TemplateRoutingAgent(
    FixedReplyTemplateService templates,
    IAgentChatClientFactory clients,
    WechatRobotDbContext database) : ITemplateRoutingAgent
{
    public Task<TemplateRouteDecision> RouteAsync(
        Guid groupProfileId,
        string message,
        CancellationToken cancellationToken)
        => RouteCoreAsync(
            groupProfileId,
            message,
            cancellationToken);

    public Task<TemplateRouteDecision> RoutePrivateAsync(
        string message,
        CancellationToken cancellationToken)
        => RouteCoreAsync(
            null,
            message,
            cancellationToken);

    private async Task<TemplateRouteDecision> RouteCoreAsync(
        Guid? groupProfileId,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            var candidates = groupProfileId is { } groupId
                ? await templates.ListEffectiveAsync(
                    groupId,
                    cancellationToken: cancellationToken)
                : await templates.ListEffectiveForPrivateAsync(
                    cancellationToken: cancellationToken);
            if (candidates.Count == 0)
            {
                return new ContinueKnowledgeAnswer("fixed_reply_no_candidates");
            }
            var modelId = await database.ModelConfigs.AsNoTracking()
                .Where(item =>
                    item.ConfigurationType == "chat"
                    && item.IsEnabled
                    && item.IsDefault)
                .Select(item => (Guid?)item.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (modelId is null)
            {
                return new ContinueKnowledgeAnswer("fixed_reply_model_unavailable");
            }

            using var client = await clients.CreateAsync(modelId.Value, cancellationToken);
            var match = AIFunctionFactory.Create(
                (Guid templateId, int expectedVersion) => true,
                "match_fixed_template",
                "Select exactly one template only when the message clearly matches its intent.");
            var continueAnswer = AIFunctionFactory.Create(
                () => true,
                "continue_knowledge_answer",
                "Use for ambiguity, multiple intents, no exact match, or ordinary knowledge questions.");
            var prompt = JsonSerializer.Serialize(new
            {
                instruction =
                    "Choose exactly one terminal tool. Never write an answer. Shared topic alone is not a match.",
                message,
                candidates = candidates.Select(item => new
                {
                    templateId = item.Id,
                    expectedVersion = item.Version,
                    item.Name,
                    item.IntentDescription,
                    item.Examples,
                    item.Priority,
                    scope = item.IsGroupSpecific ? "group" : "global"
                })
            });
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                new ChatOptions
                {
                    Tools = [match, continueAnswer],
                    ToolMode = ChatToolMode.RequireAny
                },
                cancellationToken);
            var calls = response.Messages.SelectMany(item => item.Contents)
                .OfType<FunctionCallContent>()
                .ToArray();
            if (calls.Length != 1)
            {
                return new ContinueKnowledgeAnswer("fixed_reply_invalid_tool_count");
            }
            var call = calls[0];
            if (call.Name == continueAnswer.Name)
            {
                return new ContinueKnowledgeAnswer();
            }
            if (call.Name != match.Name
                || !TryGuid(call.Arguments, "templateId", out var templateId)
                || !TryInt(call.Arguments, "expectedVersion", out var version)
                || !candidates.Any(item =>
                    item.Id == templateId && item.Version == version))
            {
                return new ContinueKnowledgeAnswer("fixed_reply_invalid_tool_arguments");
            }
            var resolved = groupProfileId is { } currentGroupId
                ? await templates.ResolveAsync(
                    templateId,
                    version,
                    currentGroupId,
                    cancellationToken)
                : await templates.ResolveForPrivateAsync(
                    templateId,
                    version,
                    cancellationToken);
            return resolved is null
                ? new ContinueKnowledgeAnswer("fixed_reply_stale_match")
                : new MatchFixedTemplate(templateId, version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ContinueKnowledgeAnswer("fixed_reply_agent_unavailable");
        }
    }

    private static bool TryGuid(
        IDictionary<string, object?>? arguments,
        string key,
        out Guid value)
    {
        value = default;
        return arguments is not null
               && arguments.TryGetValue(key, out var raw)
               && Guid.TryParse(Value(raw), out value);
    }

    private static bool TryInt(
        IDictionary<string, object?>? arguments,
        string key,
        out int value)
    {
        value = default;
        return arguments is not null
               && arguments.TryGetValue(key, out var raw)
               && int.TryParse(Value(raw), out value);
    }

    private static string? Value(object? value) =>
        value is JsonElement element ? element.ToString() : value?.ToString();
}
