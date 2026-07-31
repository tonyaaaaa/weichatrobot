using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Jobs;
using WechatRobot.Application.PrivateChat;
using WechatRobot.Infrastructure.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Agents;

public sealed class PrivateKnowledgeIngestProcessor(
    WechatRobotDbContext database,
    IPrivateKnowledgeProposalAgent proposalAgent,
    IPrivateKnowledgeIngestStore batches,
    QdrantKnowledgeService knowledge,
    IDurableJobRepository jobs,
    TimeProvider timeProvider) : IPrivateKnowledgeIngestProcessor
{
    public async Task ProcessAsync(
        LeasedDurableJob job,
        CancellationToken cancellationToken)
    {
        if (job.JobType != "ProcessPrivateKnowledgeIngest")
        {
            throw new InvalidOperationException("Unsupported private knowledge job.");
        }
        var payload = JsonSerializer.Deserialize<Payload>(
            job.PayloadJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Private knowledge payload is invalid.");
        var batch = await database.PrivateKnowledgeIngestBatches.SingleAsync(
            x => x.Id == payload.BatchId,
            cancellationToken);
        var source = await database.ConversationMessages.AsNoTracking().SingleAsync(
            x => x.Id == batch.SourceConversationMessageId,
            cancellationToken);
        try
        {
            var existingItems = await database.PrivateKnowledgeIngestItems
                .Where(x => x.BatchId == batch.Id)
                .OrderBy(x => x.Sequence)
                .ToArrayAsync(cancellationToken);
            if (existingItems.Any(x => x.StagedVersionId != null))
            {
                await QueueStagedAsync(batch, existingItems, cancellationToken);
                return;
            }

            var command = PrivateChatCommandParser.Parse(
                source.RoomType ?? 0,
                source.Text);
            if (command.Kind != PrivateChatMessageKind.DirectKnowledgeIngest)
            {
                throw new PrivateKnowledgeProposalException(
                    "private_knowledge_source_invalid");
            }
            var proposals = Validate(
                await proposalAgent.ProposeAsync(command.Body, cancellationToken));
            await batches.SaveProposalsAsync(
                batch.Id,
                batch.Version,
                proposals,
                timeProvider.GetUtcNow().UtcDateTime,
                cancellationToken);
            batch = await database.PrivateKnowledgeIngestBatches.SingleAsync(
                x => x.Id == batch.Id,
                cancellationToken);
            await StageAsync(batch, source, proposals, cancellationToken);
        }
        catch (PrivateKnowledgeProposalException exception)
        {
            await batches.MarkFailedAsync(
                batch.Id,
                exception.Code,
                false,
                timeProvider.GetUtcNow().UtcDateTime,
                cancellationToken);
            await NotifyAsync(
                batch,
                source,
                "知识整理失败，未发布任何知识。请在后台查看失败代码后重试。",
                "failed",
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await batches.MarkFailedAsync(
                batch.Id,
                "private_knowledge_processing_unavailable",
                true,
                timeProvider.GetUtcNow().UtcDateTime,
                CancellationToken.None);
            throw;
        }
    }

    private async Task StageAsync(
        PrivateKnowledgeIngestBatchEntity batch,
        ConversationMessageEntity source,
        IReadOnlyList<ProposedKnowledgeItem> proposals,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var globalTagId = await EnsureGlobalTagAsync(now, cancellationToken);
        var items = await database.PrivateKnowledgeIngestItems
            .Where(x => x.BatchId == batch.Id)
            .OrderBy(x => x.Sequence)
            .ToArrayAsync(cancellationToken);
        for (var index = 0; index < proposals.Count; index++)
        {
            var proposal = proposals[index];
            var item = items[index];
            var exact = await FindExactAsync(
                proposal.Question,
                proposal.Answer,
                cancellationToken);
            if (exact is not null)
            {
                item.ChangeKind = "Duplicate";
                item.MatchedDocumentId = exact.Value.DocumentId;
                item.MatchedVersionId = exact.Value.VersionId;
                item.ResolvedTagIdsJson = JsonSerializer.Serialize(exact.Value.TagIds);
                continue;
            }

            var target = await ResolveTargetAsync(proposal, cancellationToken);
            if (proposal.ChangeKind == KnowledgeChangeKind.Duplicate
                && target is not null)
            {
                item.ChangeKind = KnowledgeChangeKind.Duplicate.ToString();
                item.MatchedDocumentId = target.DocumentId;
                item.MatchedVersionId = target.VersionId;
                item.ResolvedTagIdsJson = JsonSerializer.Serialize(
                    await ResolveTagsAsync(
                        proposal,
                        target.VersionId,
                        globalTagId,
                        now,
                        cancellationToken));
                continue;
            }
            var change = target is null ? KnowledgeChangeKind.New : proposal.ChangeKind;
            if (change == KnowledgeChangeKind.Correction
                && target is not null
                && await database.KnowledgeChunks.CountAsync(
                    x => x.KnowledgeDocumentVersionId == target.VersionId,
                    cancellationToken) != 1)
            {
                target = null;
                change = KnowledgeChangeKind.New;
            }
            var tagIds = await ResolveTagsAsync(
                proposal,
                target?.VersionId,
                globalTagId,
                now,
                cancellationToken);
            var staged = await CreateStagedVersionAsync(
                batch,
                source,
                proposal,
                change,
                target,
                tagIds,
                index + 1,
                now,
                cancellationToken);
            item.ChangeKind = change.ToString();
            item.MatchedDocumentId = target?.DocumentId;
            item.MatchedVersionId = target?.VersionId;
            item.StagedDocumentId = staged.DocumentId;
            item.StagedVersionId = staged.VersionId;
            item.ResolvedTagIdsJson = JsonSerializer.Serialize(tagIds);
        }
        Recount(batch, items);
        batch.Status = items.All(x => x.ChangeKind == "Duplicate")
            ? "Activated"
            : "Indexing";
        batch.FailureCode = null;
        batch.Version++;
        batch.UpdatedAtUtc = now;
        await database.SaveChangesAsync(cancellationToken);

        if (batch.Status == "Activated")
        {
            await NotifyAsync(
                batch,
                source,
                $"知识整理完成：新增 {batch.NewCount}，重复 {batch.DuplicateCount}，补充 {batch.SupplementCount}，纠正 {batch.CorrectionCount}。",
                "final",
                cancellationToken);
            return;
        }
        await QueueStagedAsync(batch, items, cancellationToken);
    }

    private async Task QueueStagedAsync(
        PrivateKnowledgeIngestBatchEntity batch,
        IReadOnlyList<PrivateKnowledgeIngestItemEntity> items,
        CancellationToken cancellationToken)
    {
        foreach (var item in items.Where(x =>
                     x.ChangeKind != "Duplicate"
                     && x.StagedDocumentId != null
                     && x.StagedVersionId != null))
        {
            var tagIds = JsonSerializer.Deserialize<Guid[]>(item.ResolvedTagIdsJson) ?? [];
            await knowledge.QueuePrivateBatchIndexAsync(
                batch.Id,
                item.StagedDocumentId!.Value,
                item.StagedVersionId!.Value,
                tagIds,
                cancellationToken);
        }
        batch.Status = "Indexing";
        batch.FailureCode = null;
        batch.Version++;
        batch.UpdatedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        await database.SaveChangesAsync(cancellationToken);
    }

    private static IReadOnlyList<ProposedKnowledgeItem> Validate(
        IReadOnlyList<ProposedKnowledgeItem> proposals)
    {
        if (proposals.Count is < 1 or > 20)
        {
            throw new PrivateKnowledgeProposalException(
                "private_knowledge_item_count_invalid");
        }
        return proposals.Select(item =>
        {
            var question = item.Question.Trim();
            var answer = item.Answer.Trim();
            if (question.Length is < 1 or > 2048
                || answer.Length is < 1 or > 16000)
            {
                throw new PrivateKnowledgeProposalException(
                    "private_knowledge_item_length_invalid");
            }
            return item with
            {
                Question = question,
                Answer = answer,
                ExplicitTags = item.ExplicitTags
                    .Select(x => x.Trim())
                    .Where(x => x.Length is > 0 and <= 128)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(20)
                    .ToArray()
            };
        }).ToArray();
    }

    private async Task<(Guid DocumentId, Guid VersionId, Guid[] TagIds)?> FindExactAsync(
        string question,
        string answer,
        CancellationToken cancellationToken)
    {
        var row = await (from chunk in database.KnowledgeChunks.AsNoTracking()
                         join version in database.KnowledgeDocumentVersions.AsNoTracking()
                             on chunk.KnowledgeDocumentVersionId equals version.Id
                         join document in database.KnowledgeDocuments.AsNoTracking()
                             on version.KnowledgeDocumentId equals document.Id
                         where chunk.Question == question
                               && chunk.Answer == answer
                               && chunk.Status == "approved"
                               && version.Status == "active"
                               && version.IsPublished
                               && document.ActiveVersionId == version.Id
                         select new
                         {
                             DocumentId = document.Id,
                             VersionId = version.Id,
                             ChunkId = chunk.Id
                         })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        var tags = await database.KnowledgeChunkTags.AsNoTracking()
            .Where(x => x.KnowledgeChunkId == row.ChunkId)
            .Select(x => x.KnowledgeTagId)
            .ToArrayAsync(cancellationToken);
        return (row.DocumentId, row.VersionId, tags);
    }

    private async Task<Target?> ResolveTargetAsync(
        ProposedKnowledgeItem proposal,
        CancellationToken cancellationToken)
    {
        if (proposal.ChangeKind == KnowledgeChangeKind.New
            || proposal.SimilarVersionId is not { } versionId)
        {
            return null;
        }
        return await (from version in database.KnowledgeDocumentVersions.AsNoTracking()
                      join document in database.KnowledgeDocuments.AsNoTracking()
                          on version.KnowledgeDocumentId equals document.Id
                      where version.Id == versionId
                            && version.Status == "active"
                            && version.IsPublished
                            && document.ActiveVersionId == version.Id
                            && document.Status == "active"
                      select new Target(document.Id, version.Id, version.Version))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<Guid[]> ResolveTagsAsync(
        ProposedKnowledgeItem proposal,
        Guid? targetVersionId,
        Guid globalTagId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var resolved = new HashSet<Guid>();
        foreach (var value in proposal.ExplicitTags)
        {
            if (GlobalKnowledgeTag.IsReservedDisplayName(value))
            {
                resolved.Add(globalTagId);
                continue;
            }

            var normalized = KnowledgeTagManager.NormalizeName(value);
            var tag = await database.KnowledgeTags.SingleOrDefaultAsync(
                x => x.NormalizedName == normalized,
                cancellationToken);
            if (tag is null)
            {
                tag = new KnowledgeTagEntity
                {
                    Name = value,
                    NormalizedName = normalized,
                    IsEnabled = true,
                    CreatedAtUtc = now
                };
                database.KnowledgeTags.Add(tag);
                await database.SaveChangesAsync(cancellationToken);
            }
            if (tag.IsEnabled) resolved.Add(tag.Id);
        }
        if (proposal.ExplicitTags.Count == 0
            && proposal.SuggestedTagId is { } suggested
            && await database.KnowledgeTags.AnyAsync(
                x => x.Id == suggested && x.IsEnabled,
                cancellationToken))
        {
            resolved.Add(suggested);
        }
        if (targetVersionId is { } target)
        {
            var inherited = await (from binding in database.KnowledgeChunkTags.AsNoTracking()
                                   join chunk in database.KnowledgeChunks.AsNoTracking()
                                       on binding.KnowledgeChunkId equals chunk.Id
                                   where chunk.KnowledgeDocumentVersionId == target
                                   select binding.KnowledgeTagId)
                .Distinct()
                .ToArrayAsync(cancellationToken);
            resolved.UnionWith(inherited);
        }
        if (resolved.Count == 0) resolved.Add(globalTagId);
        return resolved.Order().ToArray();
    }

    private async Task<(Guid DocumentId, Guid VersionId)> CreateStagedVersionAsync(
        PrivateKnowledgeIngestBatchEntity batch,
        ConversationMessageEntity source,
        ProposedKnowledgeItem proposal,
        KnowledgeChangeKind change,
        Target? target,
        IReadOnlyList<Guid> tagIds,
        int sequence,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var document = target is null
            ? new KnowledgeDocumentEntity
            {
                Title = proposal.Question[..Math.Min(proposal.Question.Length, 256)],
                Status = "indexing",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            }
            : await database.KnowledgeDocuments.SingleAsync(
                x => x.Id == target.DocumentId,
                cancellationToken);
        if (target is null) database.KnowledgeDocuments.Add(document);
        var version = new KnowledgeDocumentVersionEntity
        {
            KnowledgeDocumentId = document.Id,
            Version = (target?.VersionNumber ?? 0) + 1,
            OriginalFileName = $"private-chat-{batch.Id:N}-{sequence}.txt",
            SafeFileName = $"{Guid.NewGuid():N}.txt",
            ContentType = "text/plain",
            Sha256 = Hash($"{batch.Id:N}:{sequence}:{proposal.Question}:{proposal.Answer}"),
            SizeBytes = Encoding.UTF8.GetByteCount(proposal.Question + proposal.Answer),
            ObjectKey = $"private-chat/{batch.Id:N}/{sequence}",
            Status = "approved",
            StagedContent = Encoding.UTF8.GetBytes(
                $"问题：{proposal.Question}\n答案：{proposal.Answer}"),
            SourceKind = "PrivateChatDirect",
            SourceConversationMessageId = source.Id,
            SourceActorDisplayName = source.PeerDisplayName ?? source.SenderDisplayName,
            SourceBatchId = batch.Id,
            ChangeKind = change.ToString(),
            SupersedesVersionId = target?.VersionId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        database.KnowledgeDocumentVersions.Add(version);
        var chunkSequence = 1;
        if (change == KnowledgeChangeKind.Supplement && target is not null)
        {
            var oldChunks = await database.KnowledgeChunks.AsNoTracking()
                .Where(x => x.KnowledgeDocumentVersionId == target.VersionId)
                .OrderBy(x => x.Sequence)
                .ToArrayAsync(cancellationToken);
            foreach (var old in oldChunks)
            {
                database.KnowledgeChunks.Add(new KnowledgeChunkEntity
                {
                    KnowledgeDocumentVersionId = version.Id,
                    Sequence = chunkSequence++,
                    PageNumber = old.PageNumber,
                    Text = old.Text,
                    HeadingsJson = old.HeadingsJson,
                    IsTable = old.IsTable,
                    TableRows = old.TableRows,
                    TableColumns = old.TableColumns,
                    Question = old.Question,
                    SynonymsJson = old.SynonymsJson,
                    Answer = old.Answer,
                    Status = "approved",
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            }
        }
        database.KnowledgeChunks.Add(new KnowledgeChunkEntity
        {
            KnowledgeDocumentVersionId = version.Id,
            Sequence = chunkSequence,
            Text = $"问题：{proposal.Question}\n答案：{proposal.Answer}",
            Question = proposal.Question,
            Answer = proposal.Answer,
            Status = "approved",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        return (document.Id, version.Id);
    }

    private async Task<Guid> EnsureGlobalTagAsync(
        DateTime now,
        CancellationToken cancellationToken)
    {
        var existing = await database.KnowledgeTags.SingleOrDefaultAsync(
            x => x.SystemKind == GlobalKnowledgeTag.SystemKind,
            cancellationToken);
        if (existing is not null)
        {
            return existing.Id;
        }

        var global = GlobalKnowledgeTag.Create(now);
        database.KnowledgeTags.Add(global);
        await database.SaveChangesAsync(cancellationToken);
        return global.Id;
    }

    private async Task NotifyAsync(
        PrivateKnowledgeIngestBatchEntity batch,
        ConversationMessageEntity source,
        string text,
        string suffix,
        CancellationToken cancellationToken)
    {
        await jobs.EnqueueSendCommandAsync(
            new EnqueueSendCommandRequest(
                batch.RobotConfigId,
                string.Empty,
                source.PeerDisplayName ?? source.SenderDisplayName,
                text,
                $"private-ingest-{suffix}:{batch.Id:D}"),
            cancellationToken);
    }

    private static void Recount(
        PrivateKnowledgeIngestBatchEntity batch,
        IReadOnlyList<PrivateKnowledgeIngestItemEntity> items)
    {
        batch.TotalCount = items.Count;
        batch.NewCount = items.Count(x => x.ChangeKind == "New");
        batch.DuplicateCount = items.Count(x => x.ChangeKind == "Duplicate");
        batch.SupplementCount = items.Count(x => x.ChangeKind == "Supplement");
        batch.CorrectionCount = items.Count(x => x.ChangeKind == "Correction");
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record Payload(Guid BatchId);
    private sealed record Target(Guid DocumentId, Guid VersionId, int VersionNumber);
}
