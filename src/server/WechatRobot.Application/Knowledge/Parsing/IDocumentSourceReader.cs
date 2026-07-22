namespace WechatRobot.Application.Knowledge.Parsing;

public interface IDocumentSourceReader
{
    Task<Stream> OpenReadAsync(Uri publicUrl, long maximumBytes, CancellationToken cancellationToken);
}
