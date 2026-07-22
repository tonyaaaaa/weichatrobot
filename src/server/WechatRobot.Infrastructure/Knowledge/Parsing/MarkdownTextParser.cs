using System.Text;
using WechatRobot.Application.Knowledge.Parsing;

namespace WechatRobot.Infrastructure.Knowledge.Parsing;

public sealed class MarkdownTextParser : IDocumentParser
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Encoding Gb18030;
    static MarkdownTextParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gb18030 = Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public bool Supports(string verifiedMediaType) => verifiedMediaType is "text/plain" or "text/markdown";

    public async Task<ParsedDocument> ParseAsync(Stream source, string verifiedMediaType, DocumentParsingLimits limits, CancellationToken cancellationToken)
    {
        var bytes = await ParserUtilities.ReadBoundedAsync(source, limits, cancellationToken);
        var text = Decode(bytes);
        text = Normalize(text);
        if (verifiedMediaType == "text/plain") return new ParsedDocument([new ParsedBlock(text, null, [], false, null, null)]);

        var headings = new string?[6];
        var blocks = new List<ParsedBlock>();
        var content = new List<string>();
        void Flush()
        {
            var value = string.Join('\n', content).Trim();
            if (value.Length > 0) blocks.Add(new ParsedBlock(value, null, headings.Where(item => item is not null).Select(item => item!).ToArray(), false, null, null));
            content.Clear();
        }
        foreach (var line in text.Split('\n'))
        {
            var level = line.TakeWhile(character => character == '#').Count();
            if (level is >= 1 and <= 6 && line.Length > level && char.IsWhiteSpace(line[level]))
            {
                Flush();
                headings[level - 1] = line[(level + 1)..].Trim();
                for (var index = level; index < headings.Length; index++) headings[index] = null;
            }
            else content.Add(line);
        }
        Flush();
        return new ParsedDocument(blocks);
    }

    private static string Decode(byte[] bytes)
    {
        try { return Utf8.GetString(bytes); }
        catch (DecoderFallbackException)
        {
            try
            {
                var decoded = Gb18030.GetString(bytes);
                if (!Gb18030.GetBytes(decoded).AsSpan().SequenceEqual(bytes)) throw new DecoderFallbackException();
                return decoded;
            }
            catch (DecoderFallbackException exception) { throw new DocumentParsingException(DocumentParsingError.InvalidEncoding, "Text is neither valid UTF-8 nor GB18030.", exception); }
        }
    }
    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
}
