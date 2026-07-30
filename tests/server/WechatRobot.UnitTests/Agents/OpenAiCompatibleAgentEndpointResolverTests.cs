using WechatRobot.Infrastructure.Agents;

namespace WechatRobot.UnitTests.Agents;

public sealed class OpenAiCompatibleAgentEndpointResolverTests
{
    [Theory]
    [InlineData("https://api.example.com", "https://api.example.com/v1")]
    [InlineData("https://api.example.com/v4", "https://api.example.com/v4")]
    [InlineData("https://api.example.com/v4/chat/completions", "https://api.example.com/v4")]
    [InlineData("https://api.example.com/v1/chat/completions/", "https://api.example.com/v1")]
    public void ResolveServiceEndpoint_preserves_provider_prefix_and_removes_resource_once(
        string configured,
        string expected)
    {
        Assert.Equal(
            new Uri(expected),
            OpenAiCompatibleAgentEndpointResolver.ResolveServiceEndpoint(configured));
    }
}
