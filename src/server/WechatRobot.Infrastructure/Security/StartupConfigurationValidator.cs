using System.Data.Common;
using Microsoft.Extensions.Configuration;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Messaging;
using WechatRobot.Infrastructure.WorkTool;

namespace WechatRobot.Infrastructure.Security;

public static class StartupConfigurationValidator
{
    public static void Validate(IConfiguration configuration, bool requireCors)
    {
        ValidateMasterKey();
        ValidateMySql(configuration.GetConnectionString("WechatRobot"));
        ValidateUpload(configuration);
        ValidateSendLimit(configuration);
        ValidateWorkToolRateLimit(configuration);
        if (requireCors) ValidateCors(configuration);
    }

    private static void ValidateMasterKey()
    {
        var encoded = Environment.GetEnvironmentVariable("WECHATROBOT_MASTER_KEY_BASE64");
        byte[] decoded;
        try { decoded = Convert.FromBase64String(encoded ?? string.Empty); }
        catch (FormatException) { throw new InvalidOperationException("WECHATROBOT_MASTER_KEY_BASE64 must be valid Base64."); }
        if (decoded.Length != 32)
            throw new InvalidOperationException("WECHATROBOT_MASTER_KEY_BASE64 must decode to exactly 32 bytes because encrypted settings are required.");
    }

    private static void ValidateMySql(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:WechatRobot must be configured.");
        try
        {
            var values = new DbConnectionStringBuilder { ConnectionString = connectionString };
            if (values.Count == 0) throw new InvalidOperationException();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("ConnectionStrings:WechatRobot must be a valid MySQL connection string.", exception);
        }
    }

    private static void ValidateCors(IConfiguration configuration)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
        if (origins is null || origins.Length == 0 || origins.Any(origin =>
                !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                origin.Contains('*') ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                uri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                !string.Equals(origin, uri.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal)))
            throw new InvalidOperationException("Cors:AllowedOrigins must contain exact normalized HTTP(S) authority origins without credentials or wildcards.");
    }

    private static void ValidateUpload(IConfiguration configuration)
    {
        var options = configuration.GetSection(DocumentUploadOptions.SectionName).Get<DocumentUploadOptions>() ?? new();
        if (options.MaximumBytes is <= 0 or > int.MaxValue ||
            options.MaximumArchiveEntries <= 0 ||
            options.MaximumExpandedArchiveBytes < options.MaximumBytes ||
            options.MaximumArchiveExpansionRatio <= 0)
            throw new InvalidOperationException("Document upload limits are invalid.");
    }

    private static void ValidateSendLimit(IConfiguration configuration)
    {
        var options = configuration.GetSection(FixedReplyOptions.SectionName).Get<FixedReplyOptions>() ?? new();
        if (options.SendRateLimitPerMinute is < 1 or > 60)
            throw new InvalidOperationException("FixedReply:SendRateLimitPerMinute must be between 1 and the WorkTool limit of 60.");
    }

    private static void ValidateWorkToolRateLimit(IConfiguration configuration)
    {
        var options = configuration
            .GetSection(WorkToolRateLimitOptions.SectionName)
            .Get<WorkToolRateLimitOptions>() ?? new();
        if (string.IsNullOrWhiteSpace(options.ScopeKey) || options.ScopeKey.Length > 128)
            throw new InvalidOperationException("WorkTool:RateLimit:ScopeKey must contain 1-128 characters.");
        if (options.RequestsPerMinute is < 1 or > 60)
            throw new InvalidOperationException("WorkTool:RateLimit:RequestsPerMinute must be between 1 and 60.");
        if (options.MaxWaitSeconds is < 1 or > 60)
            throw new InvalidOperationException("WorkTool:RateLimit:MaxWaitSeconds must be between 1 and 60.");
    }
}
