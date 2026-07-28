namespace WechatRobot.Domain.Memory;

public enum MemoryEntryStatus
{
    Active,
    Superseded,
    Forgotten,
    Expired
}

public sealed record MemoryEntry(
    Guid Id,
    MemoryScope Scope,
    MemoryType Type,
    string Content,
    double Confidence,
    MemoryEntryStatus Status,
    int Version,
    DateTime ValidFromUtc,
    DateTime? ExpiresAtUtc);
