using WechatRobot.Application.Models;
using WechatRobot.Domain.Memory;

namespace WechatRobot.Application.Memory;

public sealed record MemoryExtractionMessage(
    Guid Id,
    string Role,
    string Content,
    DateTime CreatedAtUtc);

public sealed record MemoryExtractionContext(
    MemoryScope Scope,
    IReadOnlyList<MemoryExtractionMessage> Messages,
    IReadOnlyList<string>? ExistingSummaries = null);

public sealed record ExtractedMemory(
    MemoryType Type,
    string Content,
    double Confidence,
    bool IsExplicit,
    IReadOnlyList<Guid> SourceMessageIds);

public sealed record MemoryExtractionResult(IReadOnlyList<ExtractedMemory> Memories);

public interface IMemoryExtractor
{
    Task<MemoryExtractionResult> ExtractAsync(
        ModelProviderConfiguration configuration,
        MemoryExtractionContext context,
        CancellationToken cancellationToken = default);
}

public sealed class MemoryExtractionException(string failureCode) : Exception(failureCode)
{
    public string FailureCode { get; } = failureCode;
}
