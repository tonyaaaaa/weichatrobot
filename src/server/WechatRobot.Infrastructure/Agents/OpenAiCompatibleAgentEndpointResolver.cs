namespace WechatRobot.Infrastructure.Agents;

public static class OpenAiCompatibleAgentEndpointResolver
{
    private const string ChatResource = "/chat/completions";

    public static Uri ResolveServiceEndpoint(string baseUrl)
    {
        var configured = new Uri(baseUrl, UriKind.Absolute);
        var path = configured.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(ChatResource, StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^ChatResource.Length];
        }
        if (string.IsNullOrEmpty(path))
        {
            path = "/v1";
        }

        return new UriBuilder(configured)
        {
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri;
    }
}
