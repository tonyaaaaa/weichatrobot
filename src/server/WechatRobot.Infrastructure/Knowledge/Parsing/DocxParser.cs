using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.IO.Compression;
using WechatRobot.Application.Knowledge.Parsing;

namespace WechatRobot.Infrastructure.Knowledge.Parsing;

public sealed class DocxParser : IDocumentParser
{
    private const string MediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public bool Supports(string verifiedMediaType) => verifiedMediaType == MediaType;
    public async Task<ParsedDocument> ParseAsync(Stream source, string verifiedMediaType, DocumentProcessingContext context)
    {
        var bytes = await ParserUtilities.ReadBoundedAsync(source, context);
        try
        {
            context.Checkpoint("docx-archive-before");
            using (var zipStream = ParserUtilities.OpenReadOnlyStream(bytes))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false))
            {
                foreach (var entry in zip.Entries)
                {
                    context.Checkpoint("docx-entry");
                    if (entry.Length > context.Limits.MaximumExpandedEntryBytes)
                        throw new DocumentParsingException(DocumentParsingError.ResultLimitExceeded, $"DOCX entry {entry.FullName} exceeds the expanded-entry limit.");
                    context.Reserve(entry.Length, $"docx-entry:{entry.FullName}");
                }
            }
            context.Checkpoint("docx-open-before");
            using var documentStream = ParserUtilities.OpenReadOnlyStream(bytes);
            using var document = WordprocessingDocument.Open(documentStream, false);
            context.Checkpoint("docx-open-after");
            var body = document.MainDocumentPart?.Document?.Body ?? throw new InvalidDataException("DOCX body is missing.");
            var headings = new string?[6];
            var blocks = new List<ParsedBlock>();
            foreach (var element in body.ChildElements)
            {
                context.Checkpoint("docx-element");
                if (element is Paragraph paragraph)
                {
                    context.Checkpoint("docx-paragraph-before");
                    var text = paragraph.InnerText.Trim();
                    context.Checkpoint("docx-paragraph-after");
                    if (text.Length == 0) continue;
                    context.Reserve(checked((long)text.Length * sizeof(char) + 128), "docx-paragraph-text");
                    var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                    var level = HeadingLevel(style);
                    if (level is not null)
                    {
                        headings[level.Value - 1] = text;
                        for (var index = level.Value; index < headings.Length; index++) headings[index] = null;
                    }
                    else
                    {
                        context.AddResultCharacters(text.Length, "docx-paragraph");
                        blocks.Add(new ParsedBlock(text, null, Current(headings), false, null, null));
                    }
                }
                else if (element is Table table)
                {
                    context.Checkpoint("docx-table-before");
                    var rows = table.Elements<TableRow>().ToArray();
                    var columns = rows.Length == 0 ? 0 : rows.Max(row => row.Elements<TableCell>().Count());
                    var text = string.Join('\n', rows.Select(row => string.Join('\t', row.Elements<TableCell>().Select(cell => cell.InnerText.Trim()))));
                    context.Checkpoint("docx-table-after");
                    if (text.Length > 0)
                    {
                        context.Reserve(checked((long)text.Length * sizeof(char) + (long)rows.Length * 64 + 128), "docx-table-text");
                        context.AddResultCharacters(text.Length, "docx-table");
                        blocks.Add(new ParsedBlock(text, null, Current(headings), true, rows.Length, columns));
                    }
                }
            }
            return new ParsedDocument(blocks);
        }
        catch (Exception exception) when (exception is OpenXmlPackageException or InvalidDataException)
        { throw new DocumentParsingException(DocumentParsingError.MalformedDocument, "The DOCX document is malformed.", exception); }
    }
    private static int? HeadingLevel(string? style) => style is not null && style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style[7..], out var level) && level is >= 1 and <= 6 ? level : null;
    private static string[] Current(string?[] headings) => headings.Where(item => item is not null).Select(item => item!).ToArray();
}
