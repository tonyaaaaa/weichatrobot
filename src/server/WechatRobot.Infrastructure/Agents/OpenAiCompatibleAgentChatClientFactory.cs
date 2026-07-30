using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;
using WechatRobot.Application.Agents;
using WechatRobot.Application.Security;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Infrastructure.Agents;

public sealed class OpenAiCompatibleAgentChatClientFactory(
    WechatRobotDbContext database,
    ISecretProtector secretProtector) : IAgentChatClientFactory
{
    public async Task<IChatClient> CreateAsync(
        Guid modelConfigurationId,
        CancellationToken cancellationToken = default)
    {
        var model = await database.ModelConfigs
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item =>
                    item.Id == modelConfigurationId
                    && item.ConfigurationType == "chat",
                cancellationToken)
            ?? throw new KeyNotFoundException(
                "Chat model configuration was not found.");
        var hasApiKey = !string.IsNullOrWhiteSpace(model.EncryptedApiKey);
        var apiKey = hasApiKey
            ? secretProtector.Unprotect(model.EncryptedApiKey!)
            : "anonymous-credential-removed-before-send";
        var options = new OpenAIClientOptions
        {
            Endpoint = OpenAiCompatibleAgentEndpointResolver.ResolveServiceEndpoint(model.BaseUrl),
            NetworkTimeout = TimeSpan.FromSeconds(model.TimeoutSeconds)
        };
        if (!hasApiKey)
        {
            options.Transport = new HttpClientPipelineTransport(
                new HttpClient(new RemoveAuthorizationHandler())
                {
                    Timeout = Timeout.InfiniteTimeSpan
                });
        }
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), options);
        return client.GetChatClient(model.Model).AsIChatClient();
    }

    private sealed class RemoveAuthorizationHandler()
        : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override HttpResponseMessage Send(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.Authorization = null;
            return base.Send(request, cancellationToken);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.Authorization = null;
            return base.SendAsync(request, cancellationToken);
        }
    }
}
