using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Knowledge;

public sealed class KnowledgeTagManager(WechatRobotDbContext database)
{
    public async Task<KnowledgeTagPage> ListAsync(
        string? query,
        bool? isEnabled,
        bool? isGlobalPublic,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var tags = database.KnowledgeTags.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var trimmed = query.Trim();
            var normalized = NormalizeName(trimmed);
            tags = tags.Where(tag =>
                tag.NormalizedName.Contains(normalized) ||
                tag.Name.Contains(trimmed));
        }

        if (isEnabled is not null)
        {
            tags = tags.Where(tag => tag.IsEnabled == isEnabled);
        }

        if (isGlobalPublic is not null)
        {
            tags = tags.Where(tag => tag.IsGlobalPublic == isGlobalPublic);
        }

        var total = await tags.CountAsync(cancellationToken);
        var items = await tags
            .OrderBy(tag => tag.Name)
            .ThenBy(tag => tag.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(tag => new KnowledgeTagRecord(
                tag.Id,
                tag.Name,
                tag.IsEnabled,
                tag.IsGlobalPublic,
                tag.Version,
                tag.CreatedAtUtc))
            .ToArrayAsync(cancellationToken);
        return new(items, total, page, pageSize);
    }

    public Task<KnowledgeTagOption[]> ListOptionsAsync(CancellationToken cancellationToken) =>
        database.KnowledgeTags.AsNoTracking()
            .Where(tag => tag.IsEnabled)
            .OrderBy(tag => tag.Name)
            .ThenBy(tag => tag.Id)
            .Select(tag => new KnowledgeTagOption(
                tag.Id,
                tag.Name,
                tag.IsGlobalPublic))
            .ToArrayAsync(cancellationToken);

    public async Task<KnowledgeTagMutationResult> CreateAsync(
        string actor,
        KnowledgeTagDraft draft,
        CancellationToken cancellationToken)
    {
        var name = ValidateAndTrimName(draft.Name);
        if (name is null)
        {
            return InvalidName();
        }

        var normalizedName = NormalizeName(name);
        var conflict = await FindByNormalizedNameAsync(normalizedName, null, cancellationToken);
        if (conflict is not null)
        {
            return NameConflict(conflict);
        }

        var entity = new KnowledgeTagEntity
        {
            Name = name,
            NormalizedName = normalizedName,
            IsEnabled = true,
            IsGlobalPublic = draft.IsGlobalPublic
        };
        database.KnowledgeTags.Add(entity);
        AddAudit(
            actor,
            "knowledge-tag.create",
            entity.Id,
            new { after = AuditSnapshot(entity) });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return Succeeded(entity);
        }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
            conflict = await FindByNormalizedNameAsync(normalizedName, null, cancellationToken);
            if (conflict is not null)
            {
                return NameConflict(conflict);
            }

            throw;
        }
    }

    public async Task<KnowledgeTagMutationResult> UpdateAsync(
        Guid id,
        string actor,
        KnowledgeTagUpdate update,
        CancellationToken cancellationToken)
    {
        var name = ValidateAndTrimName(update.Name);
        if (name is null)
        {
            return InvalidName();
        }

        var entity = await database.KnowledgeTags.SingleOrDefaultAsync(
            tag => tag.Id == id,
            cancellationToken);
        if (entity is null)
        {
            return new(KnowledgeTagMutationStatus.NotFound);
        }

        if (entity.Version != update.ExpectedVersion)
        {
            return ConcurrencyConflict(entity);
        }

        var normalizedName = NormalizeName(name);
        var conflict = await FindByNormalizedNameAsync(normalizedName, id, cancellationToken);
        if (conflict is not null)
        {
            return NameConflict(conflict);
        }

        var before = AuditSnapshot(entity);
        entity.Name = name;
        entity.NormalizedName = normalizedName;
        entity.IsGlobalPublic = update.IsGlobalPublic;
        entity.Version++;
        AddAudit(
            actor,
            "knowledge-tag.update",
            entity.Id,
            new { before, after = AuditSnapshot(entity) });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return Succeeded(entity);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ReloadConcurrencyConflictAsync(id, cancellationToken);
        }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
            conflict = await FindByNormalizedNameAsync(normalizedName, id, cancellationToken);
            if (conflict is not null)
            {
                return NameConflict(conflict);
            }

            throw;
        }
    }

    public async Task<KnowledgeTagMutationResult> SetEnabledAsync(
        Guid id,
        string actor,
        KnowledgeTagStateUpdate update,
        CancellationToken cancellationToken)
    {
        var entity = await database.KnowledgeTags.SingleOrDefaultAsync(
            tag => tag.Id == id,
            cancellationToken);
        if (entity is null)
        {
            return new(KnowledgeTagMutationStatus.NotFound);
        }

        if (entity.Version != update.ExpectedVersion)
        {
            return ConcurrencyConflict(entity);
        }

        if (entity.IsEnabled == update.IsEnabled)
        {
            return Succeeded(entity);
        }

        var before = AuditSnapshot(entity);
        entity.IsEnabled = update.IsEnabled;
        entity.Version++;
        AddAudit(
            actor,
            update.IsEnabled ? "knowledge-tag.enable" : "knowledge-tag.disable",
            entity.Id,
            new { before, after = AuditSnapshot(entity) });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return Succeeded(entity);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ReloadConcurrencyConflictAsync(id, cancellationToken);
        }
    }

    public async Task<KnowledgeTagMutationResult> DeleteAsync(
        Guid id,
        string actor,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        var entity = await database.KnowledgeTags.SingleOrDefaultAsync(
            tag => tag.Id == id,
            cancellationToken);
        if (entity is null)
        {
            return new(KnowledgeTagMutationStatus.NotFound);
        }

        if (entity.Version != expectedVersion)
        {
            return ConcurrencyConflict(entity);
        }

        var references = await ReferencesAsync(id, cancellationToken);
        if (references.IsReferenced)
        {
            return new(
                KnowledgeTagMutationStatus.Referenced,
                ToRecord(entity),
                references);
        }

        var before = AuditSnapshot(entity);
        database.KnowledgeTags.Remove(entity);
        AddAudit(
            actor,
            "knowledge-tag.delete",
            entity.Id,
            new { before });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return Succeeded(entity);
        }
        catch (DbUpdateConcurrencyException)
        {
            return await ReloadConcurrencyConflictAsync(id, cancellationToken);
        }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
            var current = await database.KnowledgeTags.AsNoTracking().SingleOrDefaultAsync(
                tag => tag.Id == id,
                cancellationToken);
            if (current is null)
            {
                return new(KnowledgeTagMutationStatus.NotFound);
            }

            references = await ReferencesAsync(id, cancellationToken);
            if (references.IsReferenced)
            {
                return new(
                    KnowledgeTagMutationStatus.Referenced,
                    ToRecord(current),
                    references);
            }

            throw;
        }
    }

    public static string NormalizeName(string name) => name.Trim().ToUpperInvariant();

    private async Task<KnowledgeTagReferenceSummary> ReferencesAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var groups = await database.GroupProfileTags.CountAsync(
            item => item.KnowledgeTagId == id,
            cancellationToken);
        var chunks = await database.KnowledgeChunkTags.CountAsync(
            item => item.KnowledgeTagId == id,
            cancellationToken);
        var reviewJson = await database.KnowledgeReviews.AsNoTracking()
            .Select(item => item.TagIdsJson)
            .ToArrayAsync(cancellationToken);
        var indexJson = await database.KnowledgeIndexJobs.AsNoTracking()
            .Select(item => item.PendingTagIdsJson)
            .ToArrayAsync(cancellationToken);
        return new(
            groups,
            chunks,
            reviewJson.Count(json => ContainsTag(json, id)),
            indexJson.Count(json => ContainsTag(json, id)));
    }

    private static bool ContainsTag(string json, Guid id)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array &&
                   document.RootElement.EnumerateArray().Any(item =>
                       item.ValueKind == JsonValueKind.String &&
                       Guid.TryParse(item.GetString(), out var value) &&
                       value == id);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<KnowledgeTagEntity?> FindByNormalizedNameAsync(
        string normalizedName,
        Guid? excludedId,
        CancellationToken cancellationToken) =>
        await database.KnowledgeTags.AsNoTracking().SingleOrDefaultAsync(
            tag => tag.NormalizedName == normalizedName &&
                   (excludedId == null || tag.Id != excludedId),
            cancellationToken);

    private async Task<KnowledgeTagMutationResult> ReloadConcurrencyConflictAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        database.ChangeTracker.Clear();
        var current = await database.KnowledgeTags.AsNoTracking().SingleOrDefaultAsync(
            tag => tag.Id == id,
            cancellationToken);
        return current is null
            ? new(KnowledgeTagMutationStatus.NotFound)
            : ConcurrencyConflict(current);
    }

    private void AddAudit(string actor, string action, Guid targetId, object detail)
    {
        database.AdministrationAudits.Add(new AdministrationAuditEntity
        {
            Actor = string.IsNullOrWhiteSpace(actor) ? "unknown" : actor,
            Action = action,
            TargetType = "knowledge-tag",
            TargetId = targetId.ToString("D"),
            SanitizedDetailJson = JsonSerializer.Serialize(detail)
        });
    }

    private static string? ValidateAndTrimName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        return trimmed.Length <= 128 ? trimmed : null;
    }

    private static object AuditSnapshot(KnowledgeTagEntity entity) => new
    {
        name = entity.Name,
        isEnabled = entity.IsEnabled,
        isGlobalPublic = entity.IsGlobalPublic,
        version = entity.Version
    };

    private static KnowledgeTagRecord ToRecord(KnowledgeTagEntity entity) =>
        new(
            entity.Id,
            entity.Name,
            entity.IsEnabled,
            entity.IsGlobalPublic,
            entity.Version,
            entity.CreatedAtUtc);

    private static KnowledgeTagMutationResult Succeeded(KnowledgeTagEntity entity) =>
        new(KnowledgeTagMutationStatus.Succeeded, ToRecord(entity));

    private static KnowledgeTagMutationResult InvalidName() =>
        new(
            KnowledgeTagMutationStatus.InvalidInput,
            Error: "knowledge-tag-name-invalid");

    private static KnowledgeTagMutationResult NameConflict(KnowledgeTagEntity entity) =>
        new(KnowledgeTagMutationStatus.NameConflict, ToRecord(entity));

    private static KnowledgeTagMutationResult ConcurrencyConflict(KnowledgeTagEntity entity) =>
        new(KnowledgeTagMutationStatus.ConcurrencyConflict, ToRecord(entity));
}
