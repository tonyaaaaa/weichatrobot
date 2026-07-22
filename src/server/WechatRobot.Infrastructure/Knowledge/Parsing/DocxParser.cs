using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.IO.Compression;
using WechatRobot.Application.Knowledge.Parsing;

namespace WechatRobot.Infrastructure.Knowledge.Parsing;

public sealed class DocxParser : IDocumentParser
{
    private const string MediaType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public bool Supports(string verifiedMediaType) => verifiedMediaType == MediaType;
    public async Task<ParsedDocument> ParseAsync(Stream source, string verifiedMediaType, DocumentParsingLimits limits, CancellationToken cancellationToken)
    {
        var bytes = await ParserUtilities.ReadBoundedAsync(source, limits, cancellationToken);
        try
        {
            using (var zip = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read, leaveOpen: false))
            {
                long expanded = 0;
                foreach (var entry in zip.Entries)
                {
                    checked { expanded += entry.Length; }
                    if (expanded > limits.MaximumMemoryBytes) throw new DocumentParsingException(DocumentParsingError.MemoryLimitExceeded, "The expanded DOCX exceeds the parsing memory limit.");
                }
            }
            using var document = WordprocessingDocument.Open(new MemoryStream(bytes, false), false);
            var body = document.MainDocumentPart?.Document?.Body ?? throw new InvalidDataException("DOCX body is missing.");
            var headings = new string?[6];
            var blocks = new List<ParsedBlock>();
            foreach (var element in body.ChildElements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (element is Paragraph paragraph)
                {
                    var text = paragraph.InnerText.Trim();
                    if (text.Length == 0) continue;
                    var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                    var level = HeadingLevel(style);
                    if (level is not null)
                    {
                        headings[level.Value - 1] = text;
                        for (var index = level.Value; index < headings.Length; index++) headings[index] = null;
                    }
                    else blocks.Add(new ParsedBlock(text, null, Current(headings), false, null, null));
                }
                else if (element is Table table)
                {
                    var rows = table.Elements<TableRow>().ToArray();
                    var columns = rows.Length == 0 ? 0 : rows.Max(row => row.Elements<TableCell>().Count());
                    var text = string.Join('\n', rows.Select(row => string.Join('\t', row.Elements<TableCell>().Select(cell => cell.InnerText.Trim()))));
                    if (text.Length > 0) blocks.Add(new ParsedBlock(text, null, Current(headings), true, rows.Length, columns));
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
