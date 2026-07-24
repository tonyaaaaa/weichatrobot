using System.Text.Json;
using PDFtoImage;
using SkiaSharp;

var arguments = ParseArguments(args);
var mode = Required("--mode");
var input = Path.GetFullPath(Required("--input"));
var output = Path.GetFullPath(Required("--output"));
var dpi = ParsePositiveInt("--dpi");
var maximumPixels = ParsePositiveLong("--max-pixels");
var maximumBytes = ParsePositiveLong("--max-bytes");
if (!File.Exists(input) || !Directory.Exists(output)) throw new InvalidOperationException("Renderer paths do not exist.");

await using var pdf = File.Open(input, FileMode.Open, FileAccess.Read, FileShare.Read);
#pragma warning disable CA1416
var pageCount = Conversion.GetPageCount(pdf, leaveOpen: true, password: null);
#pragma warning restore CA1416
var pages = new List<PageManifest>();
long totalBytes = 0;
if (mode == "render")
{
    var requested = Required("--pages").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    foreach (var pageNumber in requested)
    {
        if (pageNumber < 1 || pageNumber > pageCount) throw new InvalidOperationException("Requested page is outside the PDF.");
        pdf.Position = 0;
        var index = new Index(pageNumber - 1);
#pragma warning disable CA1416
        var size = Conversion.GetPageSize(pdf, index, leaveOpen: true, password: null);
#pragma warning restore CA1416
        var width = checked((int)Math.Ceiling(size.Width * dpi / 72d));
        var height = checked((int)Math.Ceiling(size.Height * dpi / 72d));
        if (checked((long)width * height) > maximumPixels) throw new InvalidOperationException("Rendered page exceeds the pixel limit.");
        pdf.Position = 0;
#pragma warning disable CA1416
        using var bitmap = Conversion.ToImage(pdf, index, leaveOpen: true, password: null, new RenderOptions { Dpi = dpi });
#pragma warning restore CA1416
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = png.ToArray();
        totalBytes = checked(totalBytes + bytes.LongLength);
        if (totalBytes > maximumBytes) throw new InvalidOperationException("Rendered output exceeds the byte limit.");
        var fileName = $"page-{pageNumber}.png";
        await File.WriteAllBytesAsync(Path.Combine(output, fileName), bytes);
        pages.Add(new PageManifest(pageNumber, fileName, bitmap.Width, bitmap.Height));
    }
}
else if (mode != "count") throw new InvalidOperationException("Unknown renderer mode.");

await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new Manifest(pageCount, pages), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
return;

Dictionary<string, string> ParseArguments(string[] values)
{
    if (values.Length % 2 != 0) throw new InvalidOperationException("Renderer arguments must be name/value pairs.");
    var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < values.Length; index += 2)
        if (!parsed.TryAdd(values[index], values[index + 1])) throw new InvalidOperationException("Duplicate renderer argument.");
    return parsed;
}
string Required(string name) => arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidOperationException($"Missing {name}.");
int ParsePositiveInt(string name) => int.TryParse(Required(name), out var value) && value > 0 ? value : throw new InvalidOperationException($"Invalid {name}.");
long ParsePositiveLong(string name) => long.TryParse(Required(name), out var value) && value > 0 ? value : throw new InvalidOperationException($"Invalid {name}.");

sealed record Manifest(int PageCount, IReadOnlyList<PageManifest> Pages);
sealed record PageManifest(int PageNumber, string FileName, int Width, int Height);
