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
                var scope = await retrieval.ResolveScopeAsync(tagIds, cancellationToken);
                var matches = await retrieval.RetrieveAsync(
                    question.Trim()[..Math.Min(question.Trim().Length, 500)],
                    scope,
                    5,
                    cancellationToken);
                return matches.Select(x => new SimilarToolItem(
                    x.DocumentId,
                    x.VersionId,
                    x.Similarity,
                    x.DocumentTitle,
                    x.Text[..Math.Min(x.Text.Length, 800)]))
                    .ToArray();
            },
            "find_similar_knowledge",
            "Finds currently active knowledge similar to one proposed question.");
        var submit = AIFunctionFactory.Create(
            (ProposalEnvelope envelope) =>
            {
                proposed.Clear();
                if (envelope?.Items is not null)
                {
                    proposed.AddRange(envelope.Items);
                }
                return new { accepted = proposed.Count is > 0 and <= 20 };
            },
            "propose_knowledge_items",
            "Submits the final list of 1 to 20 question-answer knowledge items.");
        var agent = new ChatClientAgent(
            client,
            """
            You organize internal private-chat notes into reusable knowledge.
            Use tools to inspect active tags and similar knowledge. Then call
            propose_knowledge_items exactly once with 1 to 20 concise question-answer items.
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
