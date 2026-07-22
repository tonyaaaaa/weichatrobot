using WechatRobot.Application.Knowledge.Parsing;

namespace WechatRobot.Infrastructure.Knowledge.Parsing;

internal static class ParserUtilities
{
    public static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(Stream source, DocumentProcessingContext context)
    {
        context.Checkpoint("source-read");
        if (source is MemoryStream memory && memory.TryGetBuffer(out var segment))
        {
            context.EnsureSourceReservation(memory.Length);
            context.Checkpoint("source-buffered");
            return segment.Array!.AsMemory(segment.Offset, checked((int)memory.Length));
        }
        if (!source.CanSeek || source.Length > int.MaxValue)
            throw new DocumentParsingException(DocumentParsingError.SourceTooLarge, "Parser sources must be bounded seekable streams.");
        var length = source.Length;
        context.EnsureSourceReservation(length);
        var buffer = GC.AllocateUninitializedArray<byte>(checked((int)length));
        var offset = 0;
        try
        {
            while (offset < buffer.Length)
            {
                context.Checkpoint("source-read");
                var read = await source.ReadAsync(buffer.AsMemory(offset), context.Token);
                if (read == 0) throw new EndOfStreamException("Document source ended before its declared length.");
                offset += read;
            }
        }
        catch (OperationCanceledException) { context.Checkpoint("source-read"); throw; }
        context.Checkpoint("source-buffered");
        return buffer;
    }

    public static MemoryStream OpenReadOnlyStream(ReadOnlyMemory<byte> bytes)
    {
        if (!System.Runtime.InteropServices.MemoryMarshal.TryGetArray(bytes, out var segment) || segment.Array is null)
            throw new InvalidOperationException("The bounded parser buffer is not array-backed.");
        return new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false, publiclyVisible: true);
    }
}

public sealed class DocumentParserSelector(IEnumerable<IDocumentParser> parsers)
{
    private readonly IReadOnlyList<IDocumentParser> _parsers = parsers.ToArray();
    public IDocumentParser Select(string verifiedMediaType) => _parsers.SingleOrDefault(parser => parser.Supports(verifiedMediaType))
        ?? throw new DocumentParsingException(DocumentParsingError.UnsupportedMediaType, $"Unsupported verified media type: {verifiedMediaType}.");
}
