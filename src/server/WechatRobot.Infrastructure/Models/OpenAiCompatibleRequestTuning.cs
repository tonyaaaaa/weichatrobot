using System.Text;
using System.Text.Json.Nodes;

namespace WechatRobot.Infrastructure.Models;

internal static class OpenAiCompatibleRequestTuning
{
    public static bool ShouldDisableThinking(string baseUrl, string model)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint))
            return false;

        var host = endpoint.Host;
        var isOfficialZai =
            host.Equals("api.z.ai", StringComparison.OrdinalIgnoreCase)
            || host.Equals("open.bigmodel.cn", StringComparison.OrdinalIgnoreCase);
        if (!isOfficialZai)
            return false;

        var modelName = model[(model.LastIndexOf('/') + 1)..];
        return modelName.StartsWith("glm-", StringComparison.OrdinalIgnoreCase);
    }

    public static void Apply(
        IDictionary<string, object?> body,
        string baseUrl,
        string model)
    {
        if (ShouldDisableThinking(baseUrl, model))
        {
            body["thinking"] = new Dictionary<string, string>
            {
                ["type"] = "disabled"
            };
            body.TryAdd("max_tokens", 2048);
        }
    }

    public static async Task TuneRequestAsync(
        HttpRequestMessage request,
        string baseUrl,
        string model,
        CancellationToken cancellationToken)
    {
        if (!ShouldDisableThinking(baseUrl, model)
            || request.Content is null
            || request.Method != HttpMethod.Post
            || request.RequestUri is not { } uri
            || !uri.AbsolutePath.EndsWith(
                "/chat/completions",
                StringComparison.OrdinalIgnoreCase))
            return;

        var json = await request.Content.ReadAsStringAsync(cancellationToken);
        var root = JsonNode.Parse(json)?.AsObject();
        if (root is null)
            return;

        root["thinking"] = new JsonObject
        {
            ["type"] = "disabled"
        };
        if (!root.ContainsKey("max_tokens"))
            root["max_tokens"] = 2048;
        var originalContentType = request.Content.Headers.ContentType;
        var replacement = new ByteArrayContent(
            Encoding.UTF8.GetBytes(root.ToJsonString()));
        replacement.Headers.ContentType = originalContentType;
        request.Content = replacement;
    }
}

internal sealed class OpenAiCompatibleRequestTuningHandler(
    HttpMessageHandler innerHandler,
    string baseUrl,
    string model,
    bool removeAuthorization)
    : DelegatingHandler(innerHandler)
{
    protected override HttpResponseMessage Send(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (removeAuthorization)
            request.Headers.Authorization = null;
        OpenAiCompatibleRequestTuning
            .TuneRequestAsync(request, baseUrl, model, cancellationToken)
            .GetAwaiter()
            .GetResult();
        return base.Send(request, cancellationToken);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (removeAuthorization)
            request.Headers.Authorization = null;
        await OpenAiCompatibleRequestTuning.TuneRequestAsync(
            request,
            baseUrl,
            model,
            cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }
}
