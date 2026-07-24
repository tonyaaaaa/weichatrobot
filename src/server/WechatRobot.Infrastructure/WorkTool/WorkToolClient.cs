using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WechatRobot.Application.WorkTool;

namespace WechatRobot.Infrastructure.WorkTool;

public sealed class WorkToolClient(
    HttpClient httpClient,
    IWorkToolCredentialResolver credentials,
    ILogger<WorkToolClient>? logger = null) : IWorkToolClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<WorkToolCommandSubmission> SendTextAsync(
        WorkToolSendRequest request,
        CancellationToken cancellationToken)
    {
        var robotId = await credentials.ResolveRobotIdAsync(request.RobotConfigId, cancellationToken);
        return await SendCommandAsync(
            robotId,
            new
            {
                type = 203,
                titleList = new[] { request.GroupName },
                receivedContent = request.Text,
                atList = request.AtList ?? []
            },
            cancellationToken);
    }

    public async Task<WorkToolCommandSubmission> ExecuteGroupOperationAsync(
        WorkToolGroupOperationRequest request,
        CancellationToken cancellationToken)
    {
        object command = request.Kind == WorkToolGroupOperationKind.Create
            ? new
            {
                type = 206,
                groupName = request.GroupIdentifier,
                selectList = request.MemberDisplayNames,
                groupAnnouncement = request.Value
            }
            : new
            {
                type = 207,
                groupName = request.GroupIdentifier,
                newGroupName = request.Kind == WorkToolGroupOperationKind.Rename ? request.Value : null,
                newGroupAnnouncement =
                    request.Kind == WorkToolGroupOperationKind.UpdateAnnouncement ? request.Value : null,
                selectList = request.Kind == WorkToolGroupOperationKind.AddMembers
                    ? request.MemberDisplayNames
                    : Array.Empty<string>(),
                showMessageHistory = false,
                removeList = request.Kind == WorkToolGroupOperationKind.RemoveMembers
                    ? request.MemberDisplayNames
                    : Array.Empty<string>()
            };

        var robotId = await credentials.ResolveRobotIdAsync(request.RobotConfigId, cancellationToken);
        return await SendCommandAsync(robotId, command, cancellationToken);
    }

    public async Task<WorkToolRobotSnapshot> GetRobotAsync(
        Guid robotConfigId,
        CancellationToken cancellationToken)
    {
        var robotId = await credentials.ResolveRobotIdAsync(robotConfigId, cancellationToken);
        using var response = await httpClient.GetAsync(
            $"robot/robotInfo/get?robotId={Escape(robotId)}",
            cancellationToken);
        var parsed = await ReadEnvelopeAsync<RobotData>(response, "get_robot", cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new(false, null, false, false, HttpFailure(response));
        }

        if (!parsed.Parsed || parsed.Envelope?.Code != 200 || parsed.Envelope.Data is null)
        {
            return new(
                false,
                null,
                false,
                false,
                parsed.Parsed
                    ? SafeFailureCode(parsed.Envelope?.Code)
                    : "worktool_invalid_response");
        }

        var data = parsed.Envelope.Data;
        return new(
            true,
            data.RobotId,
            data.OpenCallback == 1,
            data.ReplyAll == 1,
            null);
    }

    public async Task<WorkToolOnlineSnapshot> GetOnlineAsync(
        Guid robotConfigId,
        CancellationToken cancellationToken)
    {
        var robotId = await credentials.ResolveRobotIdAsync(robotConfigId, cancellationToken);
        using var response = await httpClient.GetAsync(
            $"robot/robotInfo/online?robotId={Escape(robotId)}",
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new(null, HttpFailure(response));
        }

        if (string.IsNullOrWhiteSpace(body) || body.Trim() == "{}")
        {
            return new(null, null);
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope<OnlineData>>(body, JsonOptions);
            if (envelope?.Code is not (0 or 200))
            {
                LogFailure("get_online", envelope?.Code);
                return new(null, SafeFailureCode(envelope?.Code));
            }

            return new(envelope.Data?.ToBoolean(), null);
        }
        catch (JsonException)
        {
            return new(null, "worktool_invalid_response");
        }
    }

    public async Task<WorkToolMessageCallbackConfiguration> ConfigureMessageCallbackAsync(
        Guid robotConfigId,
        WorkToolMessageCallbackRequest request,
        CancellationToken cancellationToken)
    {
        var robotId = await credentials.ResolveRobotIdAsync(robotConfigId, cancellationToken);
        using var response = await httpClient.PostAsJsonAsync(
            $"robot/robotInfo/update?robotId={Escape(robotId)}",
            new
            {
                openCallback = request.OpenCallback ? 1 : 0,
                replyAll = request.ReplyAll ? 1 : 0,
                callbackUrl = request.CallbackUrl.AbsoluteUri
            },
            cancellationToken);
        var result = await ReadMutationAsync(response, "configure_message_callback", cancellationToken);
        return new(
            result.Succeeded,
            request.OpenCallback,
            request.ReplyAll,
            result.FailureCode);
    }

    public async Task<IReadOnlyList<WorkToolEventCallbackRegistration>> ListEventCallbacksAsync(
        Guid robotConfigId,
        CancellationToken cancellationToken)
    {
        var robotId = await credentials.ResolveRobotIdAsync(robotConfigId, cancellationToken);
        using var response = await httpClient.GetAsync(
            $"robot/robotInfo/callBack/get?robotId={Escape(robotId)}&robotKey=",
            cancellationToken);
        var parsed = await ReadEnvelopeAsync<IReadOnlyList<CallbackData>>(
            response,
            "list_event_callbacks",
            cancellationToken);

        if (!response.IsSuccessStatusCode ||
            !parsed.Parsed ||
            parsed.Envelope?.Code != 0 ||
            parsed.Envelope.Data is null)
        {
            return [];
        }

        return parsed.Envelope.Data
            .Where(callback => callback.Type == 1)
            .Select(callback => new WorkToolEventCallbackRegistration(
                callback.Type,
                callback.CallBackUrl))
            .ToArray();
    }

    public async Task<WorkToolCallbackMutationResult> BindEventCallbackAsync(
        Guid robotConfigId,
        int type,
        Uri callbackUrl,
        CancellationToken cancellationToken)
    {
        EnsureSupportedEventType(type);
        var robotId = await credentials.ResolveRobotIdAsync(robotConfigId, cancellationToken);
        using var response = await httpClient.PostAsJsonAsync(
            $"robot/robotInfo/callBack/bind?robotId={Escape(robotId)}",
            new { type, callBackUrl = callbackUrl.AbsoluteUri },
            cancellationToken);
        return await ReadMutationAsync(response, "bind_event_callback", cancellationToken);
    }

    public async Task<WorkToolCallbackMutationResult> DeleteEventCallbackAsync(
        Guid robotConfigId,
        int type,
        CancellationToken cancellationToken)
    {
        EnsureSupportedEventType(type);
        var robotId = await credentials.ResolveRobotIdAsync(robotConfigId, cancellationToken);
        using var response = await httpClient.PostAsJsonAsync(
            $"robot/robotInfo/callBack/deleteByType?robotId={Escape(robotId)}",
            new { type },
            cancellationToken);
        return await ReadMutationAsync(response, "delete_event_callback", cancellationToken);
    }

    [Obsolete("Use GetRobotAsync.")]
    public async Task<WorkToolSendResult> TestConnectionAsync(
        Guid robotConfigId,
        CancellationToken cancellationToken)
    {
        var result = await GetRobotAsync(robotConfigId, cancellationToken);
        return result.Reachable
            ? WorkToolSendResult.Success()
            : WorkToolSendResult.Failed(result.FailureCode ?? "worktool_unreachable");
    }

    [Obsolete("Use BindEventCallbackAsync.")]
    public async Task<WorkToolSendResult> BindCallbackAsync(
        Guid robotConfigId,
        int type,
        Uri callbackUrl,
        CancellationToken cancellationToken)
    {
        var result = await BindEventCallbackAsync(
            robotConfigId,
            type,
            callbackUrl,
            cancellationToken);
        return result.Succeeded
            ? WorkToolSendResult.Success()
            : WorkToolSendResult.Failed(result.FailureCode ?? "worktool_callback_rejected");
    }

    private async Task<WorkToolCommandSubmission> SendCommandAsync(
        string robotId,
        object command,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"wework/sendRawMessage?robotId={Escape(robotId)}",
            new { socketType = 2, list = new[] { command } },
            cancellationToken);
        var parsed = await ReadEnvelopeAsync<string>(response, "submit_command", cancellationToken);
        return ToSubmission(response, parsed.Envelope, parsed.Parsed);
    }

    private async Task<WorkToolCallbackMutationResult> ReadMutationAsync(
        HttpResponseMessage response,
        string endpoint,
        CancellationToken cancellationToken)
    {
        var parsed = await ReadEnvelopeAsync<JsonElement>(response, endpoint, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new(false, HttpFailure(response));
        }

        if (!parsed.Parsed || parsed.Envelope is null)
        {
            return new(false, "worktool_invalid_response");
        }

        return parsed.Envelope.Code == 0
            ? new(true, null)
            : new(false, SafeFailureCode(parsed.Envelope.Code));
    }

    private async Task<ParsedEnvelope<T>> ReadEnvelopeAsync<T>(
        HttpResponseMessage response,
        string endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            var envelope = await response.Content.ReadFromJsonAsync<Envelope<T>>(
                JsonOptions,
                cancellationToken);
            if (envelope?.Code is not (0 or 200))
            {
                LogFailure(endpoint, envelope?.Code);
            }

            return new(envelope, true);
        }
        catch (JsonException)
        {
            return new(null, false);
        }
        catch (NotSupportedException)
        {
            return new(null, false);
        }
    }

    private WorkToolCommandSubmission ToSubmission(
        HttpResponseMessage response,
        Envelope<string>? envelope,
        bool bodyParsed)
    {
        if (!response.IsSuccessStatusCode)
        {
            return new(false, null, HttpFailure(response), true);
        }

        if (!bodyParsed || envelope is null)
        {
            return new(false, null, "worktool_invalid_response", true);
        }

        if (envelope.Code != 0)
        {
            return new(false, null, SafeFailureCode(envelope.Code), false);
        }

        if (string.IsNullOrWhiteSpace(envelope.Data))
        {
            return new(false, null, "worktool_message_id_missing", true);
        }

        return new(true, envelope.Data.Trim(), null, false);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string HttpFailure(HttpResponseMessage response) =>
        $"worktool_http_{(int)response.StatusCode}";

    private static string? SafeFailureCode(int? code) =>
        code is 0 or 200 ? null : code is null ? "worktool_invalid_response" : $"worktool_code_{code}";

    private static void EnsureSupportedEventType(int type)
    {
        if (type != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Only command-result callback type 1 is supported.");
        }
    }

    private void LogFailure(string endpoint, int? code) =>
        logger?.LogWarning(
            "WorkTool endpoint {Endpoint} returned code {Code}.",
            endpoint,
            code);

    private sealed record ParsedEnvelope<T>(Envelope<T>? Envelope, bool Parsed);

    private sealed record Envelope<T>(int? Code, string? Message, T? Data);

    private sealed record RobotData(
        string? RobotId,
        int OpenCallback,
        int ReplyAll);

    private sealed record OnlineData(
        bool? Online,
        int? Status)
    {
        public bool? ToBoolean() => Online ?? Status switch
        {
            0 => false,
            1 => true,
            _ => null
        };
    }

    private sealed record CallbackData(
        long Id,
        int Type,
        string CallBackUrl,
        string? TypeName);
}
