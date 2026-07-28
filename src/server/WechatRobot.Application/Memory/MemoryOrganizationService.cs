using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WechatRobot.Domain.Memory;
using WechatRobot.Application.Models;

namespace WechatRobot.Application.Memory;

public sealed partial class MemoryOrganizationService(
    IMemoryStore store,
    TimeProvider timeProvider,
    IMemoryRelationshipClassifier? relationshipClassifier = null)
{
    public async Task<IReadOnlyList<MemoryOrganizationResult>> OrganizeAsync(
        MemoryExtractionContext context,
        MemoryExtractionResult extraction,
        Guid modelConfigurationId,
        Guid conversationSessionId,
        ModelProviderConfiguration? modelConfiguration = null,
        CancellationToken cancellationToken = default)
    {
        var messages = context.Messages.ToDictionary(x => x.Id);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var results = new List<MemoryOrganizationResult>();

        foreach (var memory in extraction.Memories)
        {
            var candidateScope = ScopeFor(context.Scope, memory.Type);
            Guid? supersedesEntryId = null;
            var unresolvedConflict = false;
            if (relationshipClassifier is not null && modelConfiguration is not null &&
                memory.Type is not MemoryType.BusinessFact)
            {
                var active = await store.FindActiveAsync(candidateScope, memory.Type, 5, cancellationToken);
                if (active.Count > 0)
                {
                    var relationships = await relationshipClassifier.ClassifyAsync(
                        modelConfiguration,
                        memory.Content,
                        active,
                        cancellationToken);
                    var conflicts = relationships
                        .Where(x => x.Value == MemoryRelationship.Conflict)
                        .Select(x => x.Key)
                        .ToArray();
                    if (conflicts.Length == 1 && memory.IsExplicit)
                        supersedesEntryId = conflicts[0];
                    else if (conflicts.Length > 0)
                        unresolvedConflict = true;
                }
            }
            var normalized = Normalize(memory.Content);
            var fingerprint = Hash($"{candidateScope.Type}|{candidateScope.RobotConfigId}|{candidateScope.GroupProfileId}|{candidateScope.SubjectKey}|{memory.Type}|{normalized}");
            foreach (var messageId in memory.SourceMessageIds)
            {
                var message = messages[messageId];
                results.Add(await store.ObserveAsync(
                    new MemoryCandidateDraft(
                        candidateScope,
                        memory.Type,
                        memory.Content.Trim(),
                        normalized,
                        fingerprint,
                        memory.Confidence,
                        memory.IsExplicit,
                        supersedesEntryId,
                        unresolvedConflict),
                    new MemoryObservationDraft(
                        conversationSessionId,
                        messageId,
                        Hash(message.Content),
                        Bound(message.Content.Trim(), 500),
                        message.CreatedAtUtc,
                        modelConfigurationId),
                    now,
                    cancellationToken));
            }
        }

        return results;
    }

    private static MemoryScope ScopeFor(MemoryScope source, MemoryType type) => type switch
    {
        MemoryType.UserPreference => source,
        MemoryType.GroupRule or MemoryType.BusinessFact => MemoryScope.Create(
            MemoryScopeType.Group,
            source.RobotConfigId,
            source.GroupProfileId,
            null,
            null),
        MemoryType.RobotExperience => MemoryScope.Create(
            MemoryScopeType.Robot,
            source.RobotConfigId,
            null,
            null,
            null),
        _ => source
    };

    private static string Normalize(string value) =>
        Whitespace().Replace(value.Trim().Normalize(NormalizationForm.FormKC), " ").ToLowerInvariant();

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Bound(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
