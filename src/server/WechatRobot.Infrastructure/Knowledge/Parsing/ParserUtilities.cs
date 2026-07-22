using WechatRobot.Application.Knowledge.Parsing;

namespace WechatRobot.Infrastructure.Knowledge.Parsing;

internal static class ParserUtilities
{
    public static async Task<byte[]> ReadBoundedAsync(Stream source, DocumentParsingLimits limits, CancellationToken cancellationToken)
    {
        Validate(limits);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limits.ExecutionTimeout);
        await using var destination = new MemoryStream((int)Math.Min(limits.MaximumSourceBytes, 1024 * 1024));
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, timeout.Token);
                if (read == 0) break;
                if (destination.Length + read > limits.MaximumSourceBytes) throw new DocumentParsingException(DocumentParsingError.SourceTooLarge, "The source exceeds the parsing size limit.");
                if (destination.Length + read > limits.MaximumMemoryBytes) throw new DocumentParsingException(DocumentParsingError.MemoryLimitExceeded, "The source exceeds the parsing memory limit.");
                await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw new DocumentParsingException(DocumentParsingError.Timeout, "Document parsing timed out.", exception); }
        return destination.ToArray();
    }

    private static void Validate(DocumentParsingLimits limits)
    {
        if (limits.MaximumSourceBytes < 1 || limits.MaximumPages < 1 || limits.MaximumMemoryBytes < limits.MaximumSourceBytes || limits.ExecutionTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("Document parsing limits are invalid.");
    }
}

public sealed class DocumentParserSelector(IEnumerable<IDocumentParser> parsers)
{
    private readonly IReadOnlyList<IDocumentParser> _parsers = parsers.ToArray();
    public IDocumentParser Select(string verifiedMediaType) => _parsers.SingleOrDefault(parser => parser.Supports(verifiedMediaType))
        ?? throw new DocumentParsingException(DocumentParsingError.UnsupportedMediaType, $"Unsupported verified media type: {verifiedMediaType}.");
}
