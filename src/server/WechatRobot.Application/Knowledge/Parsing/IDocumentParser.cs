namespace WechatRobot.Application.Knowledge.Parsing;

public interface IDocumentParser
{
    bool Supports(string verifiedMediaType);
    Task<ParsedDocument> ParseAsync(Stream source, string verifiedMediaType, DocumentParsingLimits limits, CancellationToken cancellationToken);
}

public sealed record DocumentParsingLimits(long MaximumSourceBytes, int MaximumPages, long MaximumMemoryBytes, TimeSpan ExecutionTimeout);

public enum DocumentParsingError { UnsupportedMediaType, SourceTooLarge, PageLimitExceeded, MemoryLimitExceeded, Timeout, InvalidEncoding, EmptyTextPdf, MalformedDocument }

public sealed class DocumentParsingException(DocumentParsingError error, string message, Exception? inner = null) : Exception(message, inner)
{
    public DocumentParsingError Error { get; } = error;
}
