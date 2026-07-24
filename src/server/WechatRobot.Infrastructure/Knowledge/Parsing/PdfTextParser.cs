using UglyToad.PdfPig;
using WechatRobot.Application.Knowledge.Parsing;

namespace WechatRobot.Infrastructure.Knowledge.Parsing;

public sealed class PdfTextParser : IDocumentParser
{
    public bool Supports(string verifiedMediaType) => verifiedMediaType == "application/pdf";
    public async Task<ParsedDocument> ParseAsync(Stream source, string verifiedMediaType, DocumentProcessingContext context)
    {
        var bytes = await ParserUtilities.ReadBoundedAsync(source, context);
        try
        {
            context.Checkpoint("pdf-open-before");
            using var pdfStream = ParserUtilities.OpenReadOnlyStream(bytes);
            using var pdf = PdfDocument.Open(pdfStream);
            context.Checkpoint("pdf-open-after");
            if (pdf.NumberOfPages > context.Limits.MaximumPages) throw new DocumentParsingException(DocumentParsingError.PageLimitExceeded, "The PDF exceeds the page limit.");
            var blocks = new List<ParsedBlock>();
            for (var pageNumber = 1; pageNumber <= pdf.NumberOfPages; pageNumber++)
            {
                context.Checkpoint($"pdf-page:{pageNumber}");
                var page = pdf.GetPage(pageNumber);
                context.Checkpoint($"pdf-page-extracted:{pageNumber}");
                var text = page.Text.Trim();
                context.Checkpoint($"pdf-text-extracted:{pageNumber}");
                if (text.Length > 0)
                {
                    if (text.Length > context.Limits.MaximumPageCharacters)
                        throw new DocumentParsingException(DocumentParsingError.ResultLimitExceeded, $"PDF page {pageNumber} exceeds the per-page text limit.");
                    context.Reserve(checked((long)text.Length * sizeof(char) + 128), $"pdf-page-text:{pageNumber}");
                    context.AddResultCharacters(text.Length, $"pdf-page:{pageNumber}");
                    blocks.Add(new ParsedBlock(text, pageNumber, [], false, null, null));
                }
            }
            if (blocks.Count == 0) throw new DocumentParsingException(DocumentParsingError.EmptyTextPdf, "The PDF contains no extractable text and requires OCR.");
            return new ParsedDocument(blocks);
        }
        catch (DocumentParsingException) { throw; }
        catch (Exception exception) { throw new DocumentParsingException(DocumentParsingError.MalformedDocument, "The PDF document is malformed.", exception); }
    }
}
