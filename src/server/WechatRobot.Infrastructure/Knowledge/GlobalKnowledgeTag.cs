using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Knowledge;

public static class GlobalKnowledgeTag
{
    public static readonly Guid DefaultId =
        Guid.Parse("f5b8e5c1-5f2d-4d61-9ae0-126dca90a0e1");

    public const string DisplayName = "全局知识";
    public const string NormalizedName = "SYSTEM:GLOBAL_KNOWLEDGE";
    public const string SystemKind = "GlobalKnowledge";

    public static bool IsReservedDisplayName(string? value) =>
        string.Equals(
            value?.Trim(),
            DisplayName,
            StringComparison.OrdinalIgnoreCase);

    public static KnowledgeTagEntity Create(DateTime now) =>
        new()
        {
            Id = DefaultId,
            Name = DisplayName,
            NormalizedName = NormalizedName,
            SystemKind = SystemKind,
            IsEnabled = true,
            IsGlobalPublic = true,
            CreatedAtUtc = now
        };
}
