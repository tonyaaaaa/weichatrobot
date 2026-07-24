namespace WechatRobot.Application.Storage;

public interface IObjectStorage
{
    Task<StoredObject> PutAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken);
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
}

public sealed record StoredObject(string ObjectKey, Uri PublicUrl);
