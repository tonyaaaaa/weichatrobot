using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using WechatRobot.Application.Knowledge.Ocr;

namespace WechatRobot.Infrastructure.Knowledge.Ocr;

public sealed class OcrClientOptions
{
    public const string SectionName = "Ocr";
    public Uri BaseAddress { get; set; } = new("http://ocr:8000/");
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);
    public long MaximumResponseBytes { get; set; } = 10 * 1024 * 1024;
}

public sealed class HttpOcrClient(HttpClient httpClient, OcrClientOptions options) : IOcrClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<OcrPageResult>> RecognizeAsync(IReadOnlyList<OcrRenderedPage> pages, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            var request = new OcrRequest(pages.Select(page => new OcrRequestPage(page.PageNumber, Convert.ToBase64String(page.ImageBytes), page.Width, page.Height)).ToArray());
            using var response = await httpClient.PostAsJsonAsync("v1/ocr/pages", request, JsonOptions, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new OcrClientException(OcrClientError.Unavailable, $"OCR service returned HTTP {(int)response.StatusCode}.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(bytes, timeout.Token);
                if (read == 0) break;
                if (buffer.Length + read > options.MaximumResponseBytes)
                    throw new OcrClientException(OcrClientError.ResponseTooLarge, "OCR response exceeded the configured limit.");
                await buffer.WriteAsync(bytes.AsMemory(0, read), timeout.Token);
            }
            buffer.Position = 0;
            var payload = await JsonSerializer.DeserializeAsync<OcrResponse>(buffer, JsonOptions, timeout.Token)
                ?? throw new OcrClientException(OcrClientError.InvalidResponse, "OCR response was empty.");
            return payload.Pages.Select(page => new OcrPageResult(page.PageNumber, ParseStatus(page.Status),
                page.Blocks.OrderBy(block => block.Order).Select(block => new OcrTextBlock(block.Order, block.Text, block.Confidence)).ToArray(), page.Error)).ToArray();
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        { throw new OcrClientException(OcrClientError.Timeout, "OCR request timed out.", exception); }
        catch (JsonException exception)
        { throw new OcrClientException(OcrClientError.InvalidResponse, "OCR response JSON was invalid.", exception); }
    }

    private static OcrPageStatus ParseStatus(string status) => status switch
    {
        "completed" => OcrPageStatus.Completed,
        "timeout" => OcrPageStatus.Timeout,
        "failed" => OcrPageStatus.Failed,
        _ => throw new OcrClientException(OcrClientError.InvalidResponse, "OCR response contained an unknown page status.")
    };

    private sealed record OcrRequest([property: JsonPropertyName("pages")] IReadOnlyList<OcrRequestPage> Pages);
    private sealed record OcrRequestPage(
        [property: JsonPropertyName("pageNumber")] int PageNumber,
        [property: JsonPropertyName("imageBase64")] string ImageBase64,
        [property: JsonPropertyName("width")] int Width,
        [property: JsonPropertyName("height")] int Height);
    private sealed record OcrResponse([property: JsonPropertyName("pages")] IReadOnlyList<OcrResponsePage> Pages);
    private sealed record OcrResponsePage(
        [property: JsonPropertyName("pageNumber")] int PageNumber,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("blocks")] IReadOnlyList<OcrResponseBlock> Blocks,
        [property: JsonPropertyName("error")] string? Error);
    private sealed record OcrResponseBlock(
        [property: JsonPropertyName("order")] int Order,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("confidence")] double Confidence);
}
