using Microsoft.EntityFrameworkCore;
using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Persistence;

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

    public static string NormalizeName(string name) => name.Trim().ToUpperInvariant();
}
