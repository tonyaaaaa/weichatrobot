namespace WechatRobot.Application.Knowledge.Ocr;

public sealed record OcrRenderedPage(int PageNumber, byte[] ImageBytes, int Width, int Height);
public sealed record OcrTextBlock(int Order, string Text, double Confidence);
public enum OcrPageStatus { Completed, Failed, Timeout }
public sealed record OcrPageResult(int PageNumber, OcrPageStatus Status, IReadOnlyList<OcrTextBlock> Blocks, string? Error);

public interface IOcrClient
{
    Task<IReadOnlyList<OcrPageResult>> RecognizeAsync(IReadOnlyList<OcrRenderedPage> pages, CancellationToken cancellationToken);
}

public enum OcrClientError { Timeout, InvalidResponse, ResponseTooLarge, Unavailable }

public sealed class OcrClientException(OcrClientError error, string message, Exception? inner = null) : Exception(message, inner)
{
    public OcrClientError Error { get; } = error;
}
