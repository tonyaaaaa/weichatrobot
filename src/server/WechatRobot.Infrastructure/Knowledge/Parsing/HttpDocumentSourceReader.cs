using WechatRobot.Application.Knowledge.Parsing;

namespace WechatRobot.Infrastructure.Knowledge.Parsing;

public sealed class HttpDocumentSourceReader(HttpClient client) : IDocumentSourceReader
{
    public async Task<Stream> OpenReadAsync(Uri publicUrl, long maximumBytes, CancellationToken cancellationToken)
    {
        if (publicUrl.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("Document source URLs must use HTTPS.");
        using var response = await client.GetAsync(publicUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes) throw new DocumentParsingException(DocumentParsingError.SourceTooLarge, "The source exceeds the parsing size limit.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new MemoryStream((int)Math.Min(maximumBytes, 1024 * 1024));
        var bytes = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(bytes, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes) { await buffer.DisposeAsync(); throw new DocumentParsingException(DocumentParsingError.SourceTooLarge, "The source exceeds the parsing size limit."); }
            await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken);
        }
        buffer.Position = 0;
        return buffer;
    }
}
