using System.Net.Http.Json;
using System.Text.Json;
using WechatRobot.Application.WorkTool;

namespace WechatRobot.Infrastructure.WorkTool;

public sealed class WorkToolClient(HttpClient httpClient) : IWorkToolClient
{
    public async Task<WorkToolSendResult> SendTextAsync(WorkToolSendRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"wework/sendRawMessage?robotId={Uri.EscapeDataString(request.WorkToolRobotId)}",
            new
            {
                socketType = 2,
                list = new[]
                {
                    new
                    {
                        type = 203,
                        titleList = new[] { request.GroupName },
                        receivedContent = request.Text
                    }
                }
            },
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return WorkToolSendResult.Failed($"HTTP {(int)response.StatusCode}");
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.TryGetProperty("code", out var code) && code.GetInt32() == 0)
            {
                return WorkToolSendResult.Success();
            }

            var message = root.TryGetProperty("message", out var responseMessage) ? responseMessage.GetString() : null;
            return WorkToolSendResult.Failed(string.IsNullOrWhiteSpace(message) ? "WorkTool rejected the command." : message);
        }
        catch (JsonException)
        {
            return WorkToolSendResult.Failed("WorkTool returned an invalid response.");
        }
    }
}
