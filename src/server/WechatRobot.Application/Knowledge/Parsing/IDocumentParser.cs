namespace WechatRobot.Application.Knowledge.Parsing;

public interface IDocumentParser
{
    bool Supports(string verifiedMediaType);
    Task<ParsedDocument> ParseAsync(Stream source, string verifiedMediaType, DocumentProcessingContext context);
}

public sealed record DocumentParsingLimits(long MaximumSourceBytes, int MaximumPages, long MaximumMemoryBytes, TimeSpan ExecutionTimeout)
{
    public int MaximumPageCharacters { get; init; } = 1_000_000;
    public long MaximumExpandedEntryBytes { get; init; } = 16 * 1024 * 1024;
    public long MaximumResultCharacters { get; init; } = 10_000_000;
}

public enum DocumentParsingError { UnsupportedMediaType, SourceTooLarge, PageLimitExceeded, MemoryLimitExceeded, ResultLimitExceeded, Timeout, InvalidEncoding, EmptyTextPdf, MalformedDocument, OcrLimitExceeded, OcrIncomplete }

public class DocumentParsingException(DocumentParsingError error, string message, Exception? inner = null) : Exception(message, inner)
{
    public DocumentParsingError Error { get; } = error;
}

public sealed class DocumentProcessingContext : IDisposable
{
    private readonly DocumentParsingLimits _limits;
    private readonly CancellationToken _externalToken;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _checkpointObserver;
    private readonly DateTimeOffset _deadlineUtc;
    private readonly CancellationTokenSource _deadlineSource;
    private readonly CancellationTokenSource _linkedSource;
    private readonly object _memoryLock = new();
    private long _memoryUsedBytes;
    private long _sourceBytesReserved;
    private long _resultCharacters;

    public DocumentProcessingContext(DocumentParsingLimits limits, CancellationToken externalToken, TimeProvider? timeProvider = null, Action<string>? checkpointObserver = null)
    {
        if (limits.MaximumSourceBytes < 1 || limits.MaximumPages < 1 || limits.MaximumMemoryBytes < 1 || limits.ExecutionTimeout <= TimeSpan.Zero ||
            limits.MaximumPageCharacters < 1 || limits.MaximumExpandedEntryBytes < 1 || limits.MaximumResultCharacters < 1)
            throw new InvalidOperationException("Document processing limits are invalid.");
        _limits = limits;
        _externalToken = externalToken;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _checkpointObserver = checkpointObserver;
        _deadlineUtc = _timeProvider.GetUtcNow().Add(limits.ExecutionTimeout);
        _deadlineSource = new CancellationTokenSource(limits.ExecutionTimeout);
        _linkedSource = CancellationTokenSource.CreateLinkedTokenSource(externalToken, _deadlineSource.Token);
    }

    public DocumentParsingLimits Limits => _limits;
    public CancellationToken Token => _linkedSource.Token;
    public long MemoryUsedBytes { get { lock (_memoryLock) return _memoryUsedBytes; } }
    public long SourceBytesReserved { get { lock (_memoryLock) return _sourceBytesReserved; } }

    public void Checkpoint(string stage)
    {
        _checkpointObserver?.Invoke(stage);
        if (_externalToken.IsCancellationRequested) _externalToken.ThrowIfCancellationRequested();
        if (_deadlineSource.IsCancellationRequested || _timeProvider.GetUtcNow() >= _deadlineUtc)
            throw new DocumentParsingException(DocumentParsingError.Timeout, $"Document processing timed out during {stage}.");
    }

    public void Reserve(long bytes, string category)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        Checkpoint(category);
        lock (_memoryLock)
        {
            long next;
            try { next = checked(_memoryUsedBytes + bytes); }
            catch (OverflowException exception) { throw new DocumentParsingException(DocumentParsingError.MemoryLimitExceeded, "Document processing memory accounting overflowed.", exception); }
            if (next > _limits.MaximumMemoryBytes)
                throw new DocumentParsingException(DocumentParsingError.MemoryLimitExceeded, $"Document processing exceeded the memory budget while reserving {category}.");
            _memoryUsedBytes = next;
        }
    }

    public void ReserveSource(long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (_memoryLock)
        {
            long next;
            try { next = checked(_sourceBytesReserved + bytes); }
            catch (OverflowException exception) { throw new DocumentParsingException(DocumentParsingError.SourceTooLarge, "Document source accounting overflowed.", exception); }
            if (next > _limits.MaximumSourceBytes)
                throw new DocumentParsingException(DocumentParsingError.SourceTooLarge, "The source exceeds the parsing size limit.");
        }
        Reserve(bytes, "source");
        lock (_memoryLock) _sourceBytesReserved += bytes;
    }

    public void EnsureSourceReservation(long totalBytes)
    {
        var missing = totalBytes - SourceBytesReserved;
        if (missing > 0) ReserveSource(missing);
        if (totalBytes > _limits.MaximumSourceBytes) throw new DocumentParsingException(DocumentParsingError.SourceTooLarge, "The source exceeds the parsing size limit.");
    }

    public void AddResultCharacters(long characters, string category)
    {
        if (characters < 0) throw new ArgumentOutOfRangeException(nameof(characters));
        Checkpoint(category);
        lock (_memoryLock)
        {
            long next;
            try { next = checked(_resultCharacters + characters); }
            catch (OverflowException exception) { throw new DocumentParsingException(DocumentParsingError.ResultLimitExceeded, "Document result accounting overflowed.", exception); }
            if (next > _limits.MaximumResultCharacters)
                throw new DocumentParsingException(DocumentParsingError.ResultLimitExceeded, $"Document output exceeded the result limit while producing {category}.");
            _resultCharacters = next;
        }
    }

    public void Dispose()
    {
        _linkedSource.Dispose();
        _deadlineSource.Dispose();
    }
}
