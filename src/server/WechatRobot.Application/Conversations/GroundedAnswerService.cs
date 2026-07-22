using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using WechatRobot.Application.Models;

namespace WechatRobot.Application.Conversations;

public sealed class GroundedAnswerService(IRetrievalEvidenceProvider retrieval, IChatCompletionClient chat, GroundedAnswerOptions options, AnswerOutputFirewall outputFirewall)
{
    public async Task<GroundedAnswerResult> AnswerAsync(GroundedAnswerRequest request, CancellationToken token)
    {
        options.Validate();
        var contextPolicy = $"senderIsolated={request.ContextPolicy.SenderIsolated};turns={request.ContextPolicy.HistoryTurns};idleMinutes={request.ContextPolicy.IdleTimeoutMinutes};tokenCap={request.ContextPolicy.TokenCap};summary={request.ContextPolicy.SummaryEnabled};botHistory={request.ContextPolicy.IncludeBotHistory}";
        var inputSummaryJson = BuildInputSummary(request);
        if (options.SensitiveTerms.Any(term => request.Question.Contains(term, StringComparison.OrdinalIgnoreCase)))
            return Result(AnswerDecisionKind.Handoff, options.SensitiveHandoffText, [], null, contextPolicy, "sensitive_topic", inputSummaryJson);

        IReadOnlyList<RetrievalEvidence> evidence;
        try
        {
            evidence = await retrieval.RetrieveAsync(request.RetrievalQuery?.Query ?? request.Question, request.AllowedTagIds, options.MaximumEvidence, token);
        }
        catch (RetrievalUnavailableException)
        {
            return Result(AnswerDecisionKind.SystemFailure, options.SystemFailureText, [], null, contextPolicy, "retrieval_unavailable", inputSummaryJson);
        }
        catch (ModelUnavailableException)
        {
            return Result(AnswerDecisionKind.SystemFailure, options.SystemFailureText, [], null, contextPolicy, "embedding_unavailable", inputSummaryJson);
        }

        var confidence = evidence.Count == 0 ? (double?)null : evidence.Max(item => item.Similarity);
        if (confidence is null || confidence < options.ConfidenceThreshold)
            return NoEvidence(evidence, confidence, contextPolicy, inputSummaryJson);

        var prompt = BuildPrompt(request, evidence);
        try
        {
            var completion = await chat.CompleteAsync(request.ChatConfiguration, prompt, token);
            var text = completion.Content.Trim();
            var validation = outputFirewall.Validate(text, evidence);
            if (!validation.IsSafe)
                return Result(AnswerDecisionKind.Clarification, options.UnsafeOutputText, [], confidence, contextPolicy, "output_source_marker", inputSummaryJson);
            return Result(AnswerDecisionKind.Answer, text, evidence, confidence, contextPolicy, null, inputSummaryJson);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return Result(AnswerDecisionKind.SystemFailure, options.SystemFailureText, evidence, confidence, contextPolicy, "provider_timeout", inputSummaryJson);
        }
        catch (TimeoutException)
        {
            return Result(AnswerDecisionKind.SystemFailure, options.SystemFailureText, evidence, confidence, contextPolicy, "provider_timeout", inputSummaryJson);
        }
        catch (HttpRequestException)
        {
            return Result(AnswerDecisionKind.SystemFailure, options.SystemFailureText, evidence, confidence, contextPolicy, "provider_failure", inputSummaryJson);
        }
        catch (ModelUnavailableException)
        {
            return Result(AnswerDecisionKind.SystemFailure, options.SystemFailureText, evidence, confidence, contextPolicy, "provider_unavailable", inputSummaryJson);
        }
    }

    private GroundedAnswerResult NoEvidence(IReadOnlyList<RetrievalEvidence> evidence, double? confidence, string contextPolicy, string inputSummaryJson) => options.NoEvidencePolicy switch
    {
        NoEvidencePolicy.Clarification => Result(AnswerDecisionKind.Clarification, options.ClarificationText, evidence, confidence, contextPolicy, null, inputSummaryJson),
        NoEvidencePolicy.Handoff => Result(AnswerDecisionKind.Handoff, options.SensitiveHandoffText, evidence, confidence, contextPolicy, null, inputSummaryJson),
        _ => Result(AnswerDecisionKind.InsufficientEvidence, options.InsufficientEvidenceText, evidence, confidence, contextPolicy, null, inputSummaryJson)
    };

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
        string contextPolicy, string? failureCode = null, string inputSummaryJson = "{}") => new(new(kind, text),
        new(evidence, options.ConfidenceThreshold, confidence, contextPolicy, kind.ToString(), failureCode, inputSummaryJson));

    private string BuildInputSummary(GroundedAnswerRequest request)
    {
        var query = request.RetrievalQuery?.Query ?? request.Question;
        var ids = request.RetrievalQuery?.ContextMessageIds ?? [];
        var summary = request.Context.Summary;
        return JsonSerializer.Serialize(new
        {
            RetrievalQueryHash = Hash(query), RetrievalQueryLength = query.Length,
            ContextMessageIds = ids, ContextMessageCount = ids.Count,
            ContextHash = Hash(string.Join("|", ids)),
            SummaryHash = string.IsNullOrEmpty(summary) ? null : Hash(summary), SummaryLength = summary?.Length ?? 0,
            PromptTemplateVersion = "grounded-v2", ModelConfigurationId = request.ModelConfigurationId,
            options.ConfidenceThreshold, request.DegradationReason, request.SummaryFailureCode
        });
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
