using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge.Chunking;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Knowledge;

public sealed class ChunkPreviewConcurrencyException : Exception;
public sealed class ChunkPreviewStateException : Exception;
public sealed record ChunkPreviewSet(Guid VersionId, int Revision, IReadOnlyList<ChunkPreview> Items);

public sealed class ChunkPreviewRepository(WechatRobotDbContext database)
{
    private readonly ChunkPreviewEditor _editor = new();

    public async Task<ChunkPreviewSet> GetAsync(Guid versionId, CancellationToken cancellationToken)
    {
        var version = await database.KnowledgeDocumentVersions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == versionId, cancellationToken)
            ?? throw new KeyNotFoundException();
        return new ChunkPreviewSet(versionId, version.PreviewRevision, await LoadAsync(versionId, cancellationToken));
    }

    public async Task<ChunkPreviewSet> ReplaceAsync(Guid versionId, IReadOnlyList<ChunkPreview> previews, int expectedRevision, CancellationToken cancellationToken)
    {
        var version = await WritableVersionAsync(versionId, expectedRevision, cancellationToken);
        database.KnowledgeChunkPreviews.RemoveRange(await database.KnowledgeChunkPreviews.Where(item => item.KnowledgeDocumentVersionId == versionId).ToArrayAsync(cancellationToken));
        version.PreviewRevision++;
        version.Status = "preview";
        version.UpdatedAtUtc = DateTime.UtcNow;
        database.KnowledgeChunkPreviews.AddRange(previews.OrderBy(item => item.Sequence).Select((item, index) => ToEntity(versionId, item with { Sequence = index })));
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw new ChunkPreviewConcurrencyException(); }
        return await GetAsync(versionId, cancellationToken);
    }

    public Task<ChunkPreviewSet> EditAsync(Guid versionId, Guid id, string text, int expectedRevision, CancellationToken token) => MutateAsync(versionId, expectedRevision, items => _editor.Edit(items, id, text), token);
    public Task<ChunkPreviewSet> DeleteAsync(Guid versionId, Guid id, int expectedRevision, CancellationToken token) => MutateAsync(versionId, expectedRevision, items => _editor.Delete(items, id), token);
    public Task<ChunkPreviewSet> SplitAsync(Guid versionId, Guid id, int offset, int expectedRevision, CancellationToken token) => MutateAsync(versionId, expectedRevision, items => _editor.Split(items, id, offset), token);
    public Task<ChunkPreviewSet> MergeAsync(Guid versionId, Guid firstId, Guid secondId, int expectedRevision, CancellationToken token) => MutateAsync(versionId, expectedRevision, items => _editor.Merge(items, firstId, secondId), token);

    public async Task<IReadOnlyList<KnowledgeChunkEntity>> ApproveAsync(Guid versionId, int expectedRevision, CancellationToken cancellationToken)
    {
        await using var transaction = database.Database.IsRelational() ? await database.Database.BeginTransactionAsync(cancellationToken) : null;
        var version = await database.KnowledgeDocumentVersions.SingleOrDefaultAsync(item => item.Id == versionId, cancellationToken) ?? throw new KeyNotFoundException();
        if (version.Status == "approved" && version.PreviewRevision == expectedRevision)
            return await database.KnowledgeChunks.AsNoTracking().Where(item => item.KnowledgeDocumentVersionId == versionId).OrderBy(item => item.Sequence).ToArrayAsync(cancellationToken);
        EnsureMutable(version, expectedRevision);
        var previews = await database.KnowledgeChunkPreviews.AsNoTracking().Where(item => item.KnowledgeDocumentVersionId == versionId).OrderBy(item => item.Sequence).ToArrayAsync(cancellationToken);
        if (previews.Length == 0) throw new ChunkPreviewStateException();
        database.KnowledgeChunks.RemoveRange(await database.KnowledgeChunks.Where(item => item.KnowledgeDocumentVersionId == versionId).ToArrayAsync(cancellationToken));
        var chunks = previews.Select(ToActive).ToArray();
        database.KnowledgeChunks.AddRange(chunks);
        version.Status = "approved";
        version.UpdatedAtUtc = DateTime.UtcNow;
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return chunks;
        }
        catch (DbUpdateException exception)
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            database.ChangeTracker.Clear();
            var winner = await database.KnowledgeDocumentVersions.AsNoTracking().SingleAsync(item => item.Id == versionId, cancellationToken);
            if (winner.Status == "approved" && winner.PreviewRevision == expectedRevision)
                return await database.KnowledgeChunks.AsNoTracking().Where(item => item.KnowledgeDocumentVersionId == versionId).OrderBy(item => item.Sequence).ToArrayAsync(cancellationToken);
            if (exception is DbUpdateConcurrencyException) throw new ChunkPreviewConcurrencyException();
            throw;
        }
    }

    private async Task<ChunkPreviewSet> MutateAsync(Guid versionId, int expectedRevision, Func<IReadOnlyList<ChunkPreview>, IReadOnlyList<ChunkPreview>> mutation, CancellationToken cancellationToken)
    {
        var version = await WritableVersionAsync(versionId, expectedRevision, cancellationToken);
        var current = await LoadAsync(versionId, cancellationToken);
        var changed = mutation(current);
        database.KnowledgeChunkPreviews.RemoveRange(await database.KnowledgeChunkPreviews.Where(item => item.KnowledgeDocumentVersionId == versionId).ToArrayAsync(cancellationToken));
        version.PreviewRevision++;
        version.UpdatedAtUtc = DateTime.UtcNow;
        database.KnowledgeChunkPreviews.AddRange(changed.Select(item => ToEntity(versionId, item)));
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw new ChunkPreviewConcurrencyException(); }
        return await GetAsync(versionId, cancellationToken);
    }

    private async Task<KnowledgeDocumentVersionEntity> WritableVersionAsync(Guid versionId, int? revision, CancellationToken cancellationToken)
    {
        var version = await database.KnowledgeDocumentVersions.SingleOrDefaultAsync(item => item.Id == versionId, cancellationToken) ?? throw new KeyNotFoundException();
        if (revision is not null && version.PreviewRevision != revision) throw new ChunkPreviewConcurrencyException();
        if (version.Status is not ("uploaded" or "preview")) throw new ChunkPreviewStateException();
        return version;
    }
    private static void EnsureMutable(KnowledgeDocumentVersionEntity version, int revision)
    {
        if (version.PreviewRevision != revision) throw new ChunkPreviewConcurrencyException();
        if (version.Status != "preview") throw new ChunkPreviewStateException();
    }
    private async Task<IReadOnlyList<ChunkPreview>> LoadAsync(Guid versionId, CancellationToken token) =>
        (await database.KnowledgeChunkPreviews.AsNoTracking().Where(item => item.KnowledgeDocumentVersionId == versionId).OrderBy(item => item.Sequence).ToArrayAsync(token)).Select(ToModel).ToArray();
    private static KnowledgeChunkPreviewEntity ToEntity(Guid versionId, ChunkPreview item) => new()
    {
        Id = item.Id, KnowledgeDocumentVersionId = versionId, Sequence = item.Sequence, Text = item.Text, PageNumber = item.PageNumber,
        HeadingsJson = JsonSerializer.Serialize(item.Headings), IsTable = item.IsTable, TableRows = item.TableRows, TableColumns = item.TableColumns,
        Question = item.Question, SynonymsJson = JsonSerializer.Serialize(item.Synonyms ?? []), Answer = item.Answer
    };
    private static ChunkPreview ToModel(KnowledgeChunkPreviewEntity item) => new(item.Id, item.Sequence, item.Text, item.PageNumber,
        JsonSerializer.Deserialize<string[]>(item.HeadingsJson) ?? [], item.IsTable, item.TableRows, item.TableColumns, item.Question,
        JsonSerializer.Deserialize<string[]>(item.SynonymsJson) ?? [], item.Answer);
    private static KnowledgeChunkEntity ToActive(KnowledgeChunkPreviewEntity item) => new()
    {
        Id = item.Id, KnowledgeDocumentVersionId = item.KnowledgeDocumentVersionId, Sequence = item.Sequence, Text = item.Text, PageNumber = item.PageNumber,
        HeadingsJson = item.HeadingsJson, IsTable = item.IsTable, TableRows = item.TableRows, TableColumns = item.TableColumns,
        Question = item.Question, SynonymsJson = item.SynonymsJson, Answer = item.Answer, Status = "approved"
    };
}
