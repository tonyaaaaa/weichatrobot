using System.Diagnostics;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using WechatRobot.Application.Agents;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Infrastructure.Agents;

public sealed class MessageIntentAgent(
    WechatRobotDbContext database,
    IAgentChatClientFactory clients,
    IOptions<AgentRuntimeOptions> configuredOptions) : IMessageIntentAgent
{
    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly HashSet<string> ReasonCodes = new(StringComparer.Ordinal)
    {
        "explicitly_addresses_bot",
        "mentions_bot_in_question",
        "continues_recent_bot_turn",
        "asks_group_member",
        "human_to_human_exchange",
        "social_or_acknowledgement",
        "insufficient_context"
    };

    public async Task<MessageIntentResult> DecideAsync(
        MessageIntentRequest request,
        CancellationToken cancellationToken)
    {
        var options = configuredOptions.Value;
        options.Validate();
        var started = Stopwatch.GetTimestamp();
        Guid? modelId = null;
        int? modelVersion = null;
        try
        {
            var current = await database.ConversationMessages.AsNoTracking()
                .SingleOrDefaultAsync(
                    item =>
                        item.Id == request.MessageId
                        && item.GroupProfileId == request.GroupProfileId
                        && item.ChannelType == "Group"
                        && item.Direction == "inbound",
                    cancellationToken);
            if (current is null)
            {
                return Failed("intent_context_missing", started);
            }

            var model = options.IntentModelConfigurationId is { } configuredId
                ? await database.ModelConfigs.AsNoTracking().SingleOrDefaultAsync(
                    item =>
                        item.Id == configuredId
                        && item.ConfigurationType == "chat"
                        && item.IsEnabled,
                    cancellationToken)
                : await database.ModelConfigs.AsNoTracking().SingleOrDefaultAsync(
                    item =>
                        item.ConfigurationType == "chat"
                        && item.IsDefault
                        && item.IsEnabled,
                    cancellationToken);
            if (model is null)
            {
                return Failed("intent_agent_unavailable", started);
            }
            modelId = model.Id;
            modelVersion = model.Version;

            var fromUtc = current.ReceivedAtUtc.AddMinutes(-options.IntentHistoryMinutes);
            var rawWindow = await database.ConversationMessages.AsNoTracking()
                .Where(item =>
                    item.GroupProfileId == request.GroupProfileId
                    && item.ChannelType == "Group"
                    && item.CreatedAtUtc >= fromUtc
                    && item.CreatedAtUtc <= current.CreatedAtUtc)
                .OrderByDescending(item => item.CreatedAtUtc)
                .ThenByDescending(item => item.Id)
                .Take(options.IntentHistoryMessageCount)
                .OrderBy(item => item.CreatedAtUtc)
                .ThenBy(item => item.Id)
                .Select(item => new
                {
                    messageRef = item.Id,
                    direction = item.Direction,
                    participant = item.Direction == "outbound" ? "机器人" : item.SenderDisplayName,
                    text = item.Text,
                    atUtc = item.CreatedAtUtc
                })
                .ToArrayAsync(cancellationToken);
            var prompt = BuildBoundedPrompt(
                current.Id,
                current.Text,
                current.SenderDisplayName,
                request.WasMentioned,
                rawWindow,
                options.IntentMaximumInputCharacters);
            IntentToolResult? submitted = null;
            using var client = await clients.CreateAsync(model.Id, cancellationToken);
            var submit = AIFunctionFactory.Create(
                (string decision, string category, string reasonCode, decimal confidence) =>
                {
                    submitted = new(decision, category, reasonCode, confidence);
                    return new { accepted = true };
                },
                "submit_intent_decision",
                """
                Submit exactly one final intent decision. Do not provide free text.
                decision must be Reply, NoReply, or Uncertain.
                category must be DirectedToBot, FollowUpToBot, HumanConversation,
                SocialChatter, or Uncertain.
                """);
            var agent = new ChatClientAgent(
                client,
                """
                Decide whether the current WeCom group message is directed to the robot.
                You can only classify intent. Never answer the message and never infer knowledge.
                Use only the supplied raw same-group messages. Call submit_intent_decision exactly once.
                decision must be Reply, NoReply, or Uncertain.
                category must be DirectedToBot, FollowUpToBot, HumanConversation, SocialChatter, or Uncertain.
                Allowed reasons: explicitly_addresses_bot, mentions_bot_in_question,
                continues_recent_bot_turn, asks_group_member, human_to_human_exchange,
                social_or_acknowledgement, insufficient_context.
                When uncertain, choose Uncertain and insufficient_context.
                """,
                "MessageIntentAgent",
                "Classifies whether one group message should enter the reply pipeline.",
                [submit]);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.IntentTimeoutSeconds));
            await agent.RunAsync(prompt, cancellationToken: timeout.Token);

            if (submitted is null
                || !TryNormalizeDecision(submitted.Decision, out var decision)
                || !TryNormalizeCategory(submitted.Category, out var category)
                || !ReasonCodes.Contains(submitted.ReasonCode)
                || submitted.Confidence is < 0 or > 1
                || !IsConsistent(decision, category, submitted.ReasonCode))
            {
                return Failed("intent_agent_invalid_output", started, modelId, modelVersion);
            }
            if (submitted.Confidence < options.IntentMinimumConfidence)
            {
                return new(
                    IntentDecision.Uncertain,
                    IntentCategory.Uncertain,
                    "insufficient_context",
                    submitted.Confidence,
                    "intent_agent_uncertain",
                    modelId,
                    modelVersion,
                    Elapsed(started));
            }
            return new(
                decision,
                category,
                submitted.ReasonCode,
                submitted.Confidence,
                null,
                modelId,
                modelVersion,
                Elapsed(started));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failed("intent_agent_timeout", started, modelId, modelVersion);
        }
        catch (Exception)
        {
            return Failed("intent_agent_failed", started, modelId, modelVersion);
        }
    }

    private static string BuildBoundedPrompt(
        Guid currentMessageId,
        string currentText,
        string currentSender,
        bool wasMentioned,
        object rawWindow,
        int maximumCharacters)
    {
        var json = JsonSerializer.Serialize(new
        {
            currentMessageRef = currentMessageId,
            currentSender,
            currentText,
            atMe = wasMentioned,
            recentSameGroupRawMessages = rawWindow
        }, PromptJsonOptions);
        if (json.Length <= maximumCharacters)
        {
            return json;
        }
        var boundedText = currentText[..Math.Min(currentText.Length, maximumCharacters / 2)];
        return JsonSerializer.Serialize(new
        {
            currentMessageRef = currentMessageId,
            currentSender,
            currentText = boundedText,
            atMe = wasMentioned,
            recentSameGroupRawMessages = Array.Empty<object>(),
            historyTruncated = true
        }, PromptJsonOptions);
    }

    private static MessageIntentResult Failed(
        string failureCode,
        long started,
        Guid? modelId = null,
        int? modelVersion = null) =>
        new(
            IntentDecision.Uncertain,
            IntentCategory.Uncertain,
            "insufficient_context",
            0,
            failureCode,
            modelId,
            modelVersion,
            Elapsed(started));

    private static int Elapsed(long started) =>
        (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    private static bool TryNormalizeDecision(
        string value,
        out IntentDecision decision)
    {
        if (Enum.TryParse(value?.Trim(), true, out decision))
        {
            return true;
        }

        switch (value?.Trim().ToLowerInvariant())
        {
            case "yes":
                decision = IntentDecision.Reply;
                return true;
            case "no":
                decision = IntentDecision.NoReply;
                return true;
            default:
                decision = default;
                return false;
        }
    }

    private static bool TryNormalizeCategory(
        string value,
        out IntentCategory category)
    {
        if (Enum.TryParse(value?.Trim(), true, out category))
        {
            return true;
        }

        category = value?.Trim().ToLowerInvariant() switch
        {
            "directed_to_bot"
                or "explicitly_addresses_bot"
                or "mentions_bot_in_question" =>
                IntentCategory.DirectedToBot,
            "follow_up_to_bot"
                or "continues_recent_bot_turn" =>
                IntentCategory.FollowUpToBot,
            "human_conversation"
                or "asks_group_member"
                or "human_to_human_exchange" =>
                IntentCategory.HumanConversation,
            "social_chatter"
                or "social_or_acknowledgement" =>
                IntentCategory.SocialChatter,
            "insufficient_context" =>
                IntentCategory.Uncertain,
            _ => default
        };
        return value?.Trim().ToLowerInvariant() is
            "directed_to_bot"
            or "explicitly_addresses_bot"
            or "mentions_bot_in_question"
            or "follow_up_to_bot"
            or "continues_recent_bot_turn"
            or "human_conversation"
            or "asks_group_member"
            or "human_to_human_exchange"
            or "social_chatter"
            or "social_or_acknowledgement"
            or "insufficient_context";
    }

    private static bool IsConsistent(
        IntentDecision decision,
        IntentCategory category,
        string reasonCode) =>
        reasonCode switch
        {
            "explicitly_addresses_bot"
                or "mentions_bot_in_question" =>
                decision == IntentDecision.Reply
                && category == IntentCategory.DirectedToBot,
            "continues_recent_bot_turn" =>
                decision == IntentDecision.Reply
                && category == IntentCategory.FollowUpToBot,
            "asks_group_member"
                or "human_to_human_exchange" =>
                decision == IntentDecision.NoReply
                && category == IntentCategory.HumanConversation,
            "social_or_acknowledgement" =>
                decision == IntentDecision.NoReply
                && category == IntentCategory.SocialChatter,
            "insufficient_context" =>
                decision == IntentDecision.Uncertain
                && category == IntentCategory.Uncertain,
            _ => false
        };

    private sealed record IntentToolResult(
        string Decision,
        string Category,
        string ReasonCode,
        decimal Confidence);
}
