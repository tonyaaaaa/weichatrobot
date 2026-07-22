using System.Text;
using System.Text.RegularExpressions;
using WechatRobot.Application.Models;

namespace WechatRobot.Application.Conversations;

public sealed class GroundedAnswerService(IRetrievalEvidenceProvider retrieval, IChatCompletionClient chat, GroundedAnswerOptions options)
{
    private static readonly Regex SourceMarker = new(@"\s*(?:\[(?:source|来源|citation)[^\]]*\]|【(?:来源|引用)[^】]*】|[\(（](?:source|来源|citation)[^\)）]*[\)）])\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task<GroundedAnswerResult> AnswerAsync(GroundedAnswerRequest request, CancellationToken token)
    {
        options.Validate();
        var contextPolicy = $"senderIsolated={request.ContextPolicy.SenderIsolated};turns={request.ContextPolicy.HistoryTurns};idleMinutes={request.ContextPolicy.IdleTimeoutMinutes};tokenCap={request.ContextPolicy.TokenCap};summary={request.ContextPolicy.SummaryEnabled};botHistory={request.ContextPolicy.IncludeBotHistory}";
        if (options.SensitiveTerms.Any(term => request.Question.Contains(term, StringComparison.OrdinalIgnoreCase)))
            return Result(AnswerDecisionKind.Handoff, options.SensitiveHandoffText, [], null, contextPolicy, "sensitive_topic");

        IReadOnlyList<RetrievalEvidence> evidence;
        try
        {
            evidence = await retrieval.RetrieveAsync(request.Question, request.AllowedTagIds, options.MaximumEvidence, token);
        }
        catch (RetrievalUnavailableException)
        {
            return Result(AnswerDecisionKind.SystemFailure, options.SystemFailureText, [], null, contextPolicy, "retrieval_unavailable");
        }

        var confidence = evidence.Count == 0 ? (double?)null : evidence.Max(item => item.Similarity);
        if (confidence is null || confidence < options.ConfidenceThreshold)
            return Result(AnswerDecisionKind.InsufficientEvidence, options.InsufficientEvidenceText, evidence, confidence, contextPolicy);

        var prompt = BuildPrompt(request, evidence);
        try
        {
            var completion = await chat.CompleteAsync(request.ChatConfiguration, prompt, token);
            var text = SourceMarker.Replace(completion.Content, " ").Trim();
            if (string.IsNullOrWhiteSpace(text))
                return Result(AnswerDecisionKind.InsufficientEvidence, options.InsufficientEvidenceText, evidence, confidence, contextPolicy);
            return Result(AnswerDecisionKind.Answer, text, evidence, confidence, contextPolicy);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return Result(AnswerDecisionKind.SystemFailure, options.SystemFailureText, evidence, confidence, contextPolicy, "provider_timeout");
        }
        catch (TimeoutException)
        {
            return Result(AnswerDecisionKind.SystemFailure, options.SystemFailureText, evidence, confidence, contextPolicy, "provider_timeout");
        }
        catch (HttpRequestException)
        {
            return Result(AnswerDecisionKind.SystemFailure, options.SystemFailureText, evidence, confidence, contextPolicy, "provider_failure");
        }
    }

    private ChatCompletionRequest BuildPrompt(GroundedAnswerRequest request, IReadOnlyList<RetrievalEvidence> evidence)
    {
        var messages = new List<ChatMessage>
        {
            new("system", "Answer only from the supplied evidence. Never invent unsupported claims. Return plain text only and do not include citations, source markers, filenames, chunk ids, or page markers in the group reply.")
        };
        if (!string.IsNullOrWhiteSpace(request.Context.Summary)) messages.Add(new("system", $"Earlier conversation summary: {request.Context.Summary}"));
        messages.AddRange(request.Context.Messages.Select(message => new ChatMessage(message.Role, message.Content)));
        var evidenceText = new StringBuilder();
        for (var index = 0; index < evidence.Count; index++) evidenceText.AppendLine($"Evidence {index + 1}: {evidence[index].Text}");
        messages.Add(new("user", $"{evidenceText}\nQuestion: {request.Question}"));
        return new(messages);
    }

    private GroundedAnswerResult Result(AnswerDecisionKind kind, string text, IReadOnlyList<RetrievalEvidence> evidence, double? confidence,
        string contextPolicy, string? failureCode = null) => new(new(kind, text),
        new(evidence, options.ConfidenceThreshold, confidence, contextPolicy, kind.ToString(), failureCode));
}
