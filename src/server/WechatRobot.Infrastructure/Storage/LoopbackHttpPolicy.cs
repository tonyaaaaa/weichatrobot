namespace WechatRobot.Infrastructure.Storage;

public static class LoopbackHttpPolicy
{
    public static bool IsStrictLoopbackHttp(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttp || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        var authorityStart = uri.OriginalString.IndexOf("://", StringComparison.Ordinal) + 3;
        var authorityEnd = uri.OriginalString.IndexOfAny(['/', '?', '#'], authorityStart);
        var authority = uri.OriginalString[authorityStart..(authorityEnd < 0 ? uri.OriginalString.Length : authorityEnd)];
        var originalHost = authority.Split(':', 2)[0];
        if (!string.Equals(originalHost, "localhost", StringComparison.OrdinalIgnoreCase) && originalHost != "127.0.0.1") return false;

        var originalPath = uri.OriginalString.Split(['?', '#'], 2)[0];
        var pathStart = originalPath.IndexOf('/', Uri.UriSchemeHttp.Length + 3);
        var path = pathStart < 0 ? string.Empty : originalPath[pathStart..];
        if (path.Contains('\\')) return false;
        try
        {
            for (var pass = 0; pass < 2; pass++) path = Uri.UnescapeDataString(path);
        }
        catch (UriFormatException) { return false; }
        return !path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == "..");
    }

    public static HttpClientHandler CreatePrimaryHandler() => new() { AllowAutoRedirect = false };

    public static void EnsureDevelopmentOnly(bool enabled, string environmentName)
    {
        if (enabled && !string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Loopback HTTP integrations are development-only.");
    }
}
