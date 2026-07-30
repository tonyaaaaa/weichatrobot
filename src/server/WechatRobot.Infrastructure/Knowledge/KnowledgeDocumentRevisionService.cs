using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Knowledge;

public sealed class KnowledgeDocumentRevisionService(
    WechatRobotDbContext database,
    TimeProvider timeProvider)
{
    public async Task<KnowledgeRevisionResult> CreateAsync(
        CreateKnowledgeRevisionCommand command,
        CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = null;
        if (!string.Equals(
                database.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.InMemory",
                StringComparison.Ordinal))
        {
            transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            var document = await database.KnowledgeDocuments
                .SingleOrDefaultAsync(item => item.Id == command.DocumentId, cancellationToken)
                ?? throw new KeyNotFoundException("knowledge-document-not-found");

            if (document.StateVersion != command.ExpectedDocumentStateVersion)
            {
                throw Concurrency(document);
            }

            if (document.IsDeleteRequested)
            {
                throw new KnowledgeRevisionStateException("document-delete-requested");
            }

            if (string.Equals(document.Status, "disabled", StringComparison.OrdinalIgnoreCase))
            {
                throw new KnowledgeRevisionStateException("document-disabled");
            }

            var source = await database.KnowledgeDocumentVersions
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.Id == command.SourceVersionId
                        && item.KnowledgeDocumentId == command.DocumentId,
                    cancellationToken)
                ?? throw new KeyNotFoundException("knowledge-document-version-not-found");

            var existing = await database.KnowledgeDocumentVersions
                .AsNoTracking()
                .Where(item => item.KnowledgeDocumentId == command.DocumentId
                    && item.SourceKind == "AdministrationRevision"
                    && (item.Status == "uploaded" || item.Status == "preview"))
                .OrderByDescending(item => item.Version)
                .Select(item => new KnowledgeRevisionResult(
                    item.KnowledgeDocumentId,
                    item.Id,
                    item.Version,
                    item.PreviewRevision))
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                throw new KnowledgeRevisionConflictException(
                    "revision-already-editable",
                    existing);
            }

            var chunks = await database.KnowledgeChunks
                .AsNoTracking()
                .Where(item => item.KnowledgeDocumentVersionId == source.Id
                    && item.Status == "approved")
                .OrderBy(item => item.Sequence)
                .ToListAsync(cancellationToken);
            if (chunks.Count == 0)
            {
                throw new KnowledgeRevisionStateException("approved-content-not-found");
            }

            var nextVersion = await database.KnowledgeDocumentVersions
                .Where(item => item.KnowledgeDocumentId == document.Id)
                .MaxAsync(item => item.Version, cancellationToken) + 1;
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var stagedText = string.Join(
                Environment.NewLine + Environment.NewLine,
                chunks.Select(item => item.Text));
            var stagedContent = Encoding.UTF8.GetBytes(stagedText);
            var revisionId = Guid.NewGuid();
            var revision = new KnowledgeDocumentVersionEntity
            {
                Id = revisionId,
                KnowledgeDocumentId = document.Id,
                Version = nextVersion,
                OriginalFileName = $"revision-{nextVersion}.txt",
                SafeFileName = $"revision-{nextVersion}.txt",
                ContentType = "text/plain",
                Sha256 = ComputeRevisionSha256(
                    document.Id,
                    revisionId,
                    stagedContent),
                SizeBytes = stagedContent.LongLength,
                ObjectKey = $"administration-revision/{document.Id:N}/{revisionId:N}",
                Status = "preview",
                StagedContent = stagedContent,
                IsPublished = false,
                PreviewRevision = 1,
                SourceKind = "AdministrationRevision",
                SourceActorDisplayName = NormalizeActorDisplayName(command.ActorDisplayName),
                ChangeKind = "Correction",
                SupersedesVersionId = source.Id,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            database.KnowledgeDocumentVersions.Add(revision);

            database.KnowledgeChunkPreviews.AddRange(chunks.Select(chunk =>
                new KnowledgeChunkPreviewEntity
                {
                    KnowledgeDocumentVersionId = revision.Id,
                    Sequence = chunk.Sequence,
                    PageNumber = chunk.PageNumber,
                    Text = chunk.Text,
                    HeadingsJson = chunk.HeadingsJson,
                    IsTable = chunk.IsTable,
                    TableRows = chunk.TableRows,
                    TableColumns = chunk.TableColumns,
                    Question = chunk.Question,
                    SynonymsJson = chunk.SynonymsJson,
                    Answer = chunk.Answer,
                    OverlapPrefixCharacters = 0,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                }));

            document.StateVersion++;
            document.UpdatedAtUtc = now;
            database.AdministrationAudits.Add(new AdministrationAuditEntity
            {
                Actor = NormalizeActor(command.ActorId),
                Action = "knowledge.document.revision.create",
                TargetType = "KnowledgeDocument",
                TargetId = document.Id.ToString(),
                SanitizedDetailJson = JsonSerializer.Serialize(new
                {
                    sourceVersionId = source.Id,
                    revisionVersionId = revision.Id,
                    revision.Version
                }),
                CreatedAtUtc = now
            });

            await database.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return new(
                document.Id,
                revision.Id,
                revision.Version,
                revision.PreviewRevision);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            database.ChangeTracker.Clear();
            var current = await database.KnowledgeDocuments
                .AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == command.DocumentId, cancellationToken);
            if (current is null)
            {
                throw new KeyNotFoundException("knowledge-document-not-found");
            }

            throw Concurrency(current);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private static string ComputeRevisionSha256(
        Guid documentId,
        Guid revisionId,
        byte[] stagedContent)
    {
        var prefix = Encoding.UTF8.GetBytes($"{documentId:N}:{revisionId:N}:");
        var input = new byte[prefix.Length + stagedContent.Length];
        Buffer.BlockCopy(prefix, 0, input, 0, prefix.Length);
        Buffer.BlockCopy(stagedContent, 0, input, prefix.Length, stagedContent.Length);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static string NormalizeActor(string actor) =>
        string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim();

    private static string? NormalizeActorDisplayName(string actorDisplayName) =>
        string.IsNullOrWhiteSpace(actorDisplayName) ? null : actorDisplayName.Trim();

    private static DocumentConcurrencyException Concurrency(
        KnowledgeDocumentEntity document) =>
        new(new KnowledgeDocumentCurrentState(
            document.Id,
            document.Status,
            document.StateVersion));
}
