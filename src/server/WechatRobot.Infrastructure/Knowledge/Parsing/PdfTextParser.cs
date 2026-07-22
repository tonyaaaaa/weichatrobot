using UglyToad.PdfPig;
using WechatRobot.Application.Knowledge.Parsing;

namespace WechatRobot.Infrastructure.Knowledge.Parsing;

public sealed class PdfTextParser : IDocumentParser
{
    public bool Supports(string verifiedMediaType) => verifiedMediaType == "application/pdf";
    public async Task<ParsedDocument> ParseAsync(Stream source, string verifiedMediaType, DocumentParsingLimits limits, CancellationToken cancellationToken)
    {
        var bytes = await ParserUtilities.ReadBoundedAsync(source, limits, cancellationToken);
        try
        {
            using var pdf = PdfDocument.Open(bytes);
            if (pdf.NumberOfPages > limits.MaximumPages) throw new DocumentParsingException(DocumentParsingError.PageLimitExceeded, "The PDF exceeds the page limit.");
            var blocks = new List<ParsedBlock>();
            for (var pageNumber = 1; pageNumber <= pdf.NumberOfPages; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = pdf.GetPage(pageNumber).Text.Trim();
                if (text.Length > 0) blocks.Add(new ParsedBlock(text, pageNumber, [], false, null, null));
            }
            if (blocks.Count == 0) throw new DocumentParsingException(DocumentParsingError.EmptyTextPdf, "The PDF contains no extractable text and requires OCR.");
            return new ParsedDocument(blocks);
        }
        catch (DocumentParsingException) { throw; }
        catch (Exception exception) { throw new DocumentParsingException(DocumentParsingError.MalformedDocument, "The PDF document is malformed.", exception); }
    }
}
