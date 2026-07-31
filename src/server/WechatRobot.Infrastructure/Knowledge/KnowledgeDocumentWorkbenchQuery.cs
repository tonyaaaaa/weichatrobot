using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Knowledge;

public sealed class KnowledgeDocumentWorkbenchQuery(WechatRobotDbContext database)
{
    public async Task<KnowledgeDocumentWorkbench?> GetAsync(
        Guid documentId,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        var document = await database.KnowledgeDocuments.AsNoTracking()
            .Where(item => item.Id == documentId)
            .Select(item => new
            {
                item.Id,
                item.Title,
                item.Status,
                item.StateVersion,
                item.ActiveVersionId,
                item.IsDeleteRequested
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (document is null) return null;

        var version = await database.KnowledgeDocumentVersions.AsNoTracking()
            .Where(item => item.Id == versionId && item.KnowledgeDocumentId == documentId)
            .Select(item => new
            {
                item.Id,
                item.Version,
                item.Status,
                item.IsPublished,
                item.SourceKind,
                item.SourceConversationMessageId,
                item.SourceActorDisplayName,
                item.SourceBatchId,
                item.ChangeKind,
                item.SupersedesVersionId,
                item.CreatedAtUtc,
                item.UpdatedAtUtc
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (version is null) return null;

        var chunkRows = await database.KnowledgeChunks.AsNoTracking()
            .Where(item => item.KnowledgeDocumentVersionId == versionId &&
                           item.Status == "approved")
            .OrderBy(item => item.Sequence)
            .Select(item => new
            {
                item.Id,
                item.Sequence,
                item.Text,
                item.PageNumber,
                item.Question,
                item.SynonymsJson,
                item.Answer,
                item.Status
            })
            .ToArrayAsync(cancellationToken);
        var chunks = chunkRows.Select(item => new KnowledgeWorkbenchChunk(
            item.Id,
            item.Sequence,
            item.Text,
            item.PageNumber,
            item.Question,
            ParseSynonyms(item.SynonymsJson),
            item.Answer,
            item.Status)).ToArray();

        var tagRows = await database.KnowledgeChunks.AsNoTracking()
            .Where(chunk => chunk.KnowledgeDocumentVersionId == versionId &&
                            chunk.Status == "approved")
            .Join(
                database.KnowledgeChunkTags.AsNoTracking(),
                chunk => chunk.Id,
                binding => binding.KnowledgeChunkId,
                (_, binding) => binding.KnowledgeTagId)
            .Join(
                database.KnowledgeTags.AsNoTracking(),
                tagId => tagId,
                tag => tag.Id,
                (_, tag) => new { tag.Id, tag.Name })
            .Distinct()
            .OrderBy(tag => tag.Name)
            .ThenBy(tag => tag.Id)
            .ToArrayAsync(cancellationToken);
        var tags = tagRows
            .Select(tag => new KnowledgeDocumentTagSummary(tag.Id, tag.Name))
            .ToArray();

        var indexJobs = await database.KnowledgeIndexJobs.AsNoTracking()
            .Where(item => item.KnowledgeDocumentVersionId == versionId &&
                           item.Operation != "cleanup")
            .OrderByDescending(item => item.UpdatedAtUtc)
            .Select(item => new KnowledgeDocumentIndexJobSummary(
                item.Id,
                item.Operation,
                item.Status,
                item.AttemptCount,
                item.FailureReason != null,
                item.CreatedAtUtc,
                item.UpdatedAtUtc))
            .ToArrayAsync(cancellationToken);

        var sourceMessageId = version.SourceConversationMessageId;
        if (sourceMessageId is null &&
            string.Equals(version.SourceKind, "ConversationReview", StringComparison.Ordinal))
        {
            var candidate = await database.KnowledgeCandidates.AsNoTracking()
                .Where(item => item.KnowledgeDocumentVersionId == versionId)
                .Select(item => new
                {
                    item.SourceConversationMessageId,
                    item.QuestionMessageId
                })
                .FirstOrDefaultAsync(cancellationToken);
            sourceMessageId = candidate?.SourceConversationMessageId ??
                              candidate?.QuestionMessageId;
        }

        var source = sourceMessageId is { } messageId
            ? await LoadSourceEvidenceAsync(messageId, cancellationToken)
            : null;

        var editableRevision = await database.KnowledgeDocumentVersions.AsNoTracking()
            .Where(item => item.KnowledgeDocumentId == documentId &&
                           item.SourceKind == "AdministrationRevision" &&
                           (item.Status == "uploaded" || item.Status == "preview"))
            .OrderByDescending(item => item.Version)
            .Select(item => new KnowledgeWorkbenchRevisionLink(
                item.Id,
                item.Version,
                item.PreviewRevision))
            .FirstOrDefaultAsync(cancellationToken);

        var sourceReason = source is null &&
                           version.SourceKind is "PrivateChatDirect" or "ConversationReview"
            ? "source-message-missing"
            : null;
        var canCreateRevision = !document.IsDeleteRequested &&
                                document.Status != "disabled" &&
                                chunks.Length > 0 &&
                                editableRevision is null;
        var cleanupStatus = document.IsDeleteRequested
            ? await database.DurableJobs.AsNoTracking()
                .Where(job =>
                    job.Id ==
                    KnowledgeDocumentCleanupJobIdentity.Create(document.Id) &&
                    job.JobType == "CleanupKnowledgeDocument")
                .Select(job => job.Status)
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        return new KnowledgeDocumentWorkbench(
            document.Id,
            document.Title,
            document.Status,
            document.StateVersion,
            document.IsDeleteRequested,
            cleanupStatus is "deadLetter" or "cancelled",
            document.ActiveVersionId,
            new KnowledgeWorkbenchVersion(
                version.Id,
                version.Version,
                version.Status,
                version.IsPublished,
                version.SourceKind,
                version.SourceActorDisplayName,
                version.SourceBatchId,
                version.ChangeKind,
                version.SupersedesVersionId,
                tags,
                indexJobs,
                version.CreatedAtUtc,
                version.UpdatedAtUtc),
            chunks,
            source,
            sourceReason,
            editableRevision,
            canCreateRevision);
    }

    private async Task<KnowledgeWorkbenchSourceEvidence?> LoadSourceEvidenceAsync(
        Guid messageId,
        CancellationToken cancellationToken)
    {
        var message = await database.ConversationMessages.AsNoTracking()
            .Where(item => item.Id == messageId)
            .Select(item => new
            {
                item.ChannelType,
                item.RoomType,
                item.PeerDisplayName,
                item.SenderDisplayName,
                item.Text,
                item.ReceivedAtUtc
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (message is null) return null;

        var actor = message.ChannelType.StartsWith(
            "Private",
            StringComparison.OrdinalIgnoreCase)
            ? message.PeerDisplayName ?? message.SenderDisplayName
            : message.SenderDisplayName;
        return new KnowledgeWorkbenchSourceEvidence(
            message.ChannelType,
            message.RoomType,
            actor,
            message.Text,
            message.ReceivedAtUtc);
    }

    private static IReadOnlyList<string> ParseSynonyms(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
