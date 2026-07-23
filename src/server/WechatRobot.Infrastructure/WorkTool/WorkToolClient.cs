using System.Net.Http.Json;
using System.Text.Json;
using WechatRobot.Application.WorkTool;

namespace WechatRobot.Infrastructure.WorkTool;

public sealed class WorkToolClient(HttpClient httpClient, IWorkToolCredentialResolver credentials) : IWorkToolClient
{
    public async Task<WorkToolSendResult> SendTextAsync(WorkToolSendRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"wework/sendRawMessage?robotId={Uri.EscapeDataString(await credentials.ResolveRobotIdAsync(request.RobotConfigId, cancellationToken))}",
            new
            {
                socketType = 2,
                list = new[]
                {
                    new
                    {
                        type = 203,
                        titleList = new[] { request.GroupName },
                        receivedContent = request.Text,
                        atList = request.AtList ?? []
                    }
                }
            },
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return WorkToolSendResult.Failed($"HTTP {(int)response.StatusCode}", deliveryMayHaveOccurred: true);
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
            return WorkToolSendResult.Failed("WorkTool returned an invalid response.", deliveryMayHaveOccurred: true);
        }
    }

    public async Task<WorkToolSendResult> ExecuteGroupOperationAsync(WorkToolGroupOperationRequest request, CancellationToken cancellationToken)
    {
        object command = request.Kind == WorkToolGroupOperationKind.Create
            ? new { type = 206, groupName = request.GroupIdentifier, selectList = request.MemberIds, groupAnnouncement = request.Value }
            : new
            {
                type = 207,
                groupName = request.GroupIdentifier,
                newGroupName = request.Kind == WorkToolGroupOperationKind.Rename ? request.Value : null,
                newGroupAnnouncement = request.Kind == WorkToolGroupOperationKind.UpdateAnnouncement ? request.Value : null,
                selectList = request.Kind == WorkToolGroupOperationKind.AddMembers ? request.MemberIds : Array.Empty<string>(),
                showMessageHistory = false,
                removeList = request.Kind == WorkToolGroupOperationKind.RemoveMembers ? request.MemberIds : Array.Empty<string>()
            };

        return await SendCommandAsync(await credentials.ResolveRobotIdAsync(request.RobotConfigId, cancellationToken), command, cancellationToken);
    }

    public async Task<WorkToolSendResult> TestConnectionAsync(Guid robotConfigId, CancellationToken cancellationToken)
    {
        var workToolRobotId = await credentials.ResolveRobotIdAsync(robotConfigId, cancellationToken);
        using var response = await httpClient.GetAsync($"wework/robot?robotId={Uri.EscapeDataString(workToolRobotId)}", cancellationToken);
        return await ParseResultAsync(response, cancellationToken);
    }

    public async Task<WorkToolSendResult> BindCallbackAsync(Guid robotConfigId, int type, Uri callbackUrl, CancellationToken cancellationToken)
    {
        var robotId = await credentials.ResolveRobotIdAsync(robotConfigId, cancellationToken);
        using var response = await httpClient.PostAsJsonAsync(
            $"robot/robotInfo/callBack/bind?robotId={Uri.EscapeDataString(robotId)}",
            new { type, callBackUrl = callbackUrl.AbsoluteUri },
            cancellationToken);
        return await ParseResultAsync(response, cancellationToken);
    }

    private async Task<WorkToolSendResult> SendCommandAsync(string robotId, object command, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"wework/sendRawMessage?robotId={Uri.EscapeDataString(robotId)}",
            new { socketType = 2, list = new[] { command } }, cancellationToken);
        return await ParseResultAsync(response, cancellationToken);
    }

    private static async Task<WorkToolSendResult> ParseResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return WorkToolSendResult.Failed($"HTTP {(int)response.StatusCode}", deliveryMayHaveOccurred: true);
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
            return WorkToolSendResult.Failed("WorkTool returned an invalid response.", deliveryMayHaveOccurred: true);
        }
    }
}
