using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using WechatRobot.Application.Agents;
using WechatRobot.Application.Conversations;
using WechatRobot.Application.PrivateChat;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Infrastructure.Agents;

public sealed class PrivateKnowledgeProposalAgent(
    WechatRobotDbContext database,
    IAgentChatClientFactory clients,
    IRetrievalEvidenceProvider retrieval) : IPrivateKnowledgeProposalAgent
{
    public async Task<IReadOnlyList<ProposedKnowledgeItem>> ProposeAsync(
        string sourceText,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            throw new PrivateKnowledgeProposalException("private_knowledge_source_empty");
        }
        var modelId = await database.ModelConfigs.AsNoTracking()
            .Where(x => x.ConfigurationType == "chat" && x.IsEnabled && x.IsDefault)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new PrivateKnowledgeProposalException("private_knowledge_model_unavailable");
        var tagIds = await database.KnowledgeTags.AsNoTracking()
            .Where(x => x.IsEnabled)
            .Select(x => x.Id)
            .ToArrayAsync(cancellationToken);
        var proposed = new List<ProposalToolItem>();
        var similarVersionIdsByQuestion =
            new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);

        using var client = await clients.CreateAsync(modelId, cancellationToken);
        var listTags = AIFunctionFactory.Create(
            async () => await database.KnowledgeTags.AsNoTracking()
                .Where(x => x.IsEnabled)
                .OrderBy(x => x.Name)
                .Take(200)
                .Select(x => new TagToolItem(x.Id, x.Name))
                .ToArrayAsync(cancellationToken),
            "list_active_knowledge_tags",
            "Lists enabled knowledge tags. Use exact names where the source explicitly names a category.");
        var findSimilar = AIFunctionFactory.Create(
            async (string question) =>
            {
                if (string.IsNullOrWhiteSpace(question))
                {
                    return Array.Empty<SimilarToolItem>();
                }
                var normalizedQuestion = NormalizeLookupQuestion(question);
                var scope = await retrieval.ResolveScopeAsync(tagIds, cancellationToken);
                var matches = await retrieval.RetrieveAsync(
                    normalizedQuestion,
                    scope,
                    5,
                    cancellationToken);
                var result = matches.Select(x => new SimilarToolItem(
                    x.DocumentId,
                    x.VersionId,
                    x.Similarity,
                    x.DocumentTitle,
                    x.Text[..Math.Min(x.Text.Length, 800)]))
                    .ToArray();
                similarVersionIdsByQuestion[normalizedQuestion] =
                    result.Select(x => x.VersionId).ToHashSet();
                return result;
            },
            "find_similar_knowledge",
            "Finds currently active knowledge similar to one proposed question.");
        var submit = AIFunctionFactory.Create(
            (ProposalEnvelope envelope) =>
            {
                proposed.Clear();
                var items = envelope?.Items;
                var accepted = items is { Count: > 0 and <= 20 }
                               && items.All(item => IsSimilarityValidated(
                                   item,
                                   similarVersionIdsByQuestion));
                if (accepted)
                {
                    proposed.AddRange(items!);
                }
                return new ProposalSubmissionResult(
                    accepted,
                    accepted ? null : "similarity_validation_failed");
            },
            "propose_knowledge_items",
            "Submits the final list of 1 to 20 question-answer knowledge items.");
        var agent = new ChatClientAgent(
            client,
            """
            You organize internal private-chat notes into reusable knowledge.
            Use tools to inspect active tags. Before deciding ChangeKind, call
            find_similar_knowledge for every proposed question.
            propose_knowledge_items rejects any final question that was not searched.
            Judge duplicates by question meaning, not exact wording or answer text.
            Use Duplicate when the same question already exists even if the new answer differs,
            or when wording differs but the question has the same meaning.
            A different answer alone is never enough to classify an item as Supplement or
            Correction.
            Use Supplement or Correction only when the source contains a genuine factual
            addition or correction. Duplicate, Supplement, and Correction must include
            the matched SimilarVersionId returned by find_similar_knowledge.
            Then call propose_knowledge_items exactly once with 1 to 20 concise
            question-answer items.
            ChangeKind must be New, Duplicate, Supplement, or Correction.
            Never invent facts absent from the source. Never write to storage.
            """,
            "PrivateKnowledgeProposalAgent",
            "Organizes private chat source text into validated knowledge proposals.",
            [listTags, findSimilar, submit]);
        await agent.RunAsync(
            sourceText[..Math.Min(sourceText.Length, 16000)],
            cancellationToken: cancellationToken);

        if (proposed.Count is < 1 or > 20)
        {
            throw new PrivateKnowledgeProposalException("private_knowledge_agent_invalid_output");
        }
        try
        {
            return proposed.Select(item => new ProposedKnowledgeItem(
                    item.Question ?? string.Empty,
                    item.Answer ?? string.Empty,
                    item.ExplicitTags ?? [],
                    item.SuggestedTagId,
                    item.SimilarVersionId,
                    Enum.Parse<KnowledgeChangeKind>(
                        item.ChangeKind ?? string.Empty,
                        ignoreCase: true)))
                .ToArray();
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            throw new PrivateKnowledgeProposalException(
                "private_knowledge_agent_invalid_output");
        }
    }

    public sealed record ProposalEnvelope(IReadOnlyList<ProposalToolItem>? Items);
    public sealed record ProposalToolItem(
        string? Question,
        string? Answer,
        IReadOnlyList<string>? ExplicitTags,
        Guid? SuggestedTagId,
        Guid? SimilarVersionId,
        string? ChangeKind);
    private static bool IsSimilarityValidated(
        ProposalToolItem item,
        IReadOnlyDictionary<string, HashSet<Guid>> similarVersionIdsByQuestion)
    {
        if (string.IsNullOrWhiteSpace(item.Question)
            || !similarVersionIdsByQuestion.TryGetValue(
                NormalizeLookupQuestion(item.Question),
                out var versionIds))
        {
            return false;
        }

        if (!Enum.TryParse<KnowledgeChangeKind>(
                item.ChangeKind,
                ignoreCase: true,
                out var changeKind))
        {
            return false;
        }

        return changeKind == KnowledgeChangeKind.New
               || item.SimilarVersionId is { } versionId
               && versionIds.Contains(versionId);
    }

    private static string NormalizeLookupQuestion(string question)
    {
        var trimmed = question.Trim();
        return trimmed[..Math.Min(trimmed.Length, 500)];
    }

    private sealed record ProposalSubmissionResult(bool Accepted, string? Reason);
    private sealed record TagToolItem(Guid Id, string Name);
    private sealed record SimilarToolItem(
        Guid DocumentId,
        Guid VersionId,
        double Similarity,
        string Title,
        string Text);
}

public sealed class PrivateKnowledgeProposalException(string code)
    : Exception(code)
{
    public string Code { get; } = code;
}
