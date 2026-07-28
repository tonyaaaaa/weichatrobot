using WechatRobot.Application.Knowledge;

namespace WechatRobot.Application.Memory;

public sealed record MemoryVectorDocument(
    Guid MemoryEntryId,
    string ScopeType,
    Guid? RobotConfigId,
    Guid? GroupProfileId,
    string? SubjectKey,
    string MemoryType,
    int StatusVersion,
    int Generation,
    IReadOnlyList<float> Vector);

public sealed record MemoryVectorHit(Guid MemoryEntryId, double Score);

public interface IMemoryVectorIndex
{
    Task IndexAsync(
        MemoryVectorDocument document,
        int dimension,
        VectorDistance distance,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryVectorHit>> SearchAsync(
        IReadOnlyList<float> vector,
        int dimension,
        VectorDistance distance,
        int generation,
        int limit,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        Guid memoryEntryId,
        int dimension,
        VectorDistance distance,
        int generation,
        CancellationToken cancellationToken = default);
}

public sealed record RecalledMemory(
    Guid Id,
    string ScopeType,
    string MemoryType,
    string Content,
    int Version,
    double Score);

public sealed record MemoryRecallResult(
    IReadOnlyList<RecalledMemory> Memories,
    string? FailureCode = null);

public interface IMemoryRecallService
{
    Task<MemoryRecallResult> RecallAsync(
        string question,
        Guid robotConfigId,
        Guid groupProfileId,
        string? subjectKey,
        CancellationToken cancellationToken = default);
}
