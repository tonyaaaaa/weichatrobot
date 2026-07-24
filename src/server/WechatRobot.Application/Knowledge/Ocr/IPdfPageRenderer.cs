using WechatRobot.Application.Knowledge.Parsing;

namespace WechatRobot.Application.Knowledge.Ocr;

public interface IPdfPageRenderer
{
    Task<int> GetPageCountAsync(Stream pdf, DocumentProcessingContext context);
    Task<IReadOnlyList<OcrRenderedPage>> RenderAsync(Stream pdf, IReadOnlyList<int> pageNumbers, DocumentProcessingContext context);
}
