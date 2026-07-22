using WechatRobot.Application.Knowledge.Parsing;
using Microsoft.Extensions.Options;

namespace WechatRobot.Infrastructure.Knowledge.Parsing;

public sealed class DocumentSourceOptions
{
    public const string SectionName = "DocumentSource";
    public bool AllowLoopbackHttp { get; set; }
}

public sealed class HttpDocumentSourceReader : IDocumentSourceReader
{
    private readonly HttpClient _client;
    private readonly bool _allowLoopbackHttp;

    public HttpDocumentSourceReader(HttpClient client, IOptions<DocumentSourceOptions> options)
    {
        _client = client;
        _allowLoopbackHttp = options.Value.AllowLoopbackHttp;
    }

    public async Task<Stream> OpenReadAsync(Uri publicUrl, DocumentProcessingContext context)
    {
        if (publicUrl.Scheme != Uri.UriSchemeHttps &&
            !(_allowLoopbackHttp && publicUrl.Scheme == Uri.UriSchemeHttp && publicUrl.IsLoopback))
            throw new InvalidOperationException("Document source URLs must use HTTPS, except an explicitly enabled loopback development source.");
        context.Checkpoint("source-http");
        try
        {
            using var response = await _client.GetAsync(publicUrl, HttpCompletionOption.ResponseHeadersRead, context.Token);
            context.Checkpoint("source-http-headers");
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength
                ?? throw new DocumentParsingException(DocumentParsingError.SourceTooLarge, "Document source Content-Length is required for bounded buffering.");
            if (length is < 0 or > int.MaxValue) throw new DocumentParsingException(DocumentParsingError.SourceTooLarge, "The source length is invalid.");
            context.ReserveSource(length);
            var bytes = GC.AllocateUninitializedArray<byte>(checked((int)length));
            await using var source = await response.Content.ReadAsStreamAsync(context.Token);
            var offset = 0;
            while (offset < bytes.Length)
            {
                context.Checkpoint("source-http-read");
                var read = await source.ReadAsync(bytes.AsMemory(offset), context.Token);
                if (read == 0) throw new EndOfStreamException("Document source ended before Content-Length.");
                offset += read;
            }
            context.Checkpoint("source-http-complete");
            return new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
        }
        catch (OperationCanceledException) { context.Checkpoint("source-http"); throw; }
    }
}
