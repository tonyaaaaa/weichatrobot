namespace WechatRobot.Infrastructure.Storage;

public sealed class OssOptions
{
    public const string SectionName = "Oss";
    public string AccessKeyId { get; set; } = string.Empty;
    public string AccessKeySecret { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string? PublicBaseUrl { get; set; }
    public bool PublicReadRiskAccepted { get; set; }

    public string ResolveAccessKeyId() => string.IsNullOrWhiteSpace(AccessKeyId)
        ? Environment.GetEnvironmentVariable("OSS_ACCESS_KEY_ID") ?? string.Empty : AccessKeyId;
    public string ResolveAccessKeySecret() => string.IsNullOrWhiteSpace(AccessKeySecret)
        ? Environment.GetEnvironmentVariable("OSS_ACCESS_KEY_SECRET") ?? string.Empty : AccessKeySecret;
}
