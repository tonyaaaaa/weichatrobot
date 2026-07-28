namespace WechatRobot.Infrastructure.Models;

internal static class OpenAiCompatibleEndpointResolver
{
    public static Uri Resolve(string baseUrl, string resource)
    {
        var baseUri = new Uri(baseUrl, UriKind.Absolute);
        var normalizedResource = resource.Trim('/');
        var normalizedPath = baseUri.AbsolutePath.TrimEnd('/');
        var resourceSuffix = "/" + normalizedResource;

        if (normalizedPath.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return baseUri;
        }

        var resolvedPath = string.IsNullOrEmpty(normalizedPath)
            ? "/v1/" + normalizedResource
            : normalizedPath + resourceSuffix;

        var builder = new UriBuilder(baseUri)
        {
            Path = resolvedPath
        };
        return builder.Uri;
    }
}
