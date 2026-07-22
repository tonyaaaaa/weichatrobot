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

    public Task<WorkToolSendResult> ExecuteGroupOperationAsync(WorkToolGroupOperationRequest request, CancellationToken cancellationToken)
    {
        object command = request.Kind == WorkToolGroupOperationKind.Create
            ? new { type = 206, groupName = request.GroupIdentifier, memberIds = request.MemberIds }
            : new
            {
                type = 207,
                groupId = request.GroupIdentifier,
                action = request.Kind switch
                {
                    WorkToolGroupOperationKind.AddMembers => "addMembers",
                    WorkToolGroupOperationKind.RemoveMembers => "removeMembers",
                    WorkToolGroupOperationKind.Rename => "rename",
                    WorkToolGroupOperationKind.UpdateAnnouncement => "updateAnnouncement",
                    WorkToolGroupOperationKind.UpdateRemark => "updateRemark",
                    _ => throw new ArgumentOutOfRangeException(nameof(request.Kind))
                },
                memberIds = request.MemberIds,
                value = request.Value
            };

        return SendCommandAsync(request.WorkToolRobotId, command, cancellationToken);
    }

    public async Task<WorkToolSendResult> TestConnectionAsync(string workToolRobotId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"wework/robot?robotId={Uri.EscapeDataString(workToolRobotId)}", cancellationToken);
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
