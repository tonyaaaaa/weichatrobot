using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WechatRobot.Application.WorkTool;

namespace WechatRobot.Infrastructure.WorkTool;

public sealed class WorkToolClient(
    HttpClient httpClient,
    IWorkToolCredentialResolver credentials,
    ILogger<WorkToolClient>? logger = null) : IWorkToolClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<WorkToolCommandSubmission> SendTextAsync(
        WorkToolSendRequest request,
        CancellationToken cancellationToken)
    {
        var robotId = await credentials.ResolveEnabledRobotIdAsync(request.RobotConfigId, cancellationToken);
        return await SendCommandAsync(
            robotId,
            new
            {
                type = 203,
                titleList = new[] { request.GroupName },
                receivedContent = request.Text,
                atList = request.AtList is { Count: > 0 } ? request.AtList : null
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

        var robotId = await credentials.ResolveEnabledRobotIdAsync(request.RobotConfigId, cancellationToken);
        return await SendCommandAsync(robotId, command, cancellationToken);
    }

    public async Task<WorkToolCommandSubmission> RequestGroupMemberSnapshotAsync(
        Guid robotConfigId,
        string groupName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        var robotId = await credentials.ResolveEnabledRobotIdAsync(
            robotConfigId,
            cancellationToken);
        return await SendCommandAsync(
            robotId,
            new
            {
                type = 512,
                groupName = groupName.Trim()
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<WorkToolRawCommandResult>>
        ListGroupMemberSnapshotResultsAsync(
            Guid robotConfigId,
            string messageId,
            DateTimeOffset startTime,
            DateTimeOffset endTime,
            CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        if (endTime < startTime)
            throw new ArgumentOutOfRangeException(
                nameof(endTime),
                "End time must not precede start time.");

        var robotId = await credentials.ResolveConfiguredRobotIdAsync(
            robotConfigId,
            cancellationToken);
        var query = string.Join(
            "&",
            $"robotId={Escape(robotId)}",
            "page=1",
            "size=10",
            $"sort={Escape("run_time,desc")}",
            $"startTime={Escape(startTime.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}",
            $"endTime={Escape(endTime.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}",
            "type=512",
            $"messageId={Escape(messageId.Trim())}");
        using var response = await SendAdministrativeAsync(
            () => CreateAdministrativeRequest(
                HttpMethod.Get,
                $"robot/rawMsg/list?{query}"),
            "list_raw_results",
            cancellationToken);
        var parsed = await ReadEnvelopeAsync<IReadOnlyList<RawCommandResultData>>(
            response,
            "list_raw_results",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new WorkToolRawResultException(HttpFailure(response));
        if (!parsed.Parsed || parsed.Envelope?.Code != 200
                           || parsed.Envelope.Data is null)
        {
            throw new WorkToolRawResultException(
                parsed.Parsed
                    ? SafeFailureCode(parsed.Envelope?.Code)
                      ?? "worktool_invalid_response"
                    : "worktool_invalid_response");
        }

        return parsed.Envelope.Data.Select(item =>
        {
            if (string.IsNullOrWhiteSpace(item.MessageId))
                throw new WorkToolRawResultException("worktool_invalid_response");
            return new WorkToolRawCommandResult(
                OpaqueRaw(item.RawMsg),
                item.RawSuccess,
                item.ErrorReason,
                OpaqueRaw(item.RunTime),
                item.ApiSend,
                item.Type,
                item.MessageId.Trim(),
                OpaqueRaw(item.SuccessList),
                OpaqueRaw(item.FailList),
                item.TimeCost);
        }).ToArray();
    }

    public async Task<WorkToolGroupPage> ListGroupsAsync(
        Guid robotConfigId,
        string? groupName,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (page < 1)
            throw new ArgumentOutOfRangeException(nameof(page));
        if (pageSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (groupName?.Length > 256)
            throw new ArgumentOutOfRangeException(nameof(groupName));

        var robotId = await credentials.ResolveConfiguredRobotIdAsync(
            robotConfigId,
            cancellationToken);
        using var response = await SendAdministrativeAsync(
            () => CreateAdministrativeRequest(
                HttpMethod.Get,
                $"robot/wework/group/list?robotId={Escape(robotId)}&groupName={Escape(groupName?.Trim() ?? string.Empty)}&page={page}&size={pageSize}"),
            "list_groups",
            cancellationToken);
        var parsed = await ReadEnvelopeAsync<GroupPageData>(
            response,
            "list_groups",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new WorkToolGroupListException(HttpFailure(response));
        if (!parsed.Parsed || parsed.Envelope?.Code != 0 || parsed.Envelope.Data is null)
            throw new WorkToolGroupListException(
                parsed.Parsed
                    ? SafeFailureCode(parsed.Envelope?.Code) ?? "worktool_invalid_response"
                    : "worktool_invalid_response");

        var data = parsed.Envelope.Data;
        if (data.PageNum < 0
            || data.PageSize < 0
            || data.TotalPage < 0
            || data.Total < 0
            || data.List is null)
            throw new WorkToolGroupListException("worktool_invalid_response");

        var items = data.List
            .Where(group => !string.IsNullOrWhiteSpace(group.GroupName))
            .Select(group => new WorkToolGroupSummary(
                group.GroupName.Trim(),
                group.MasterName,
                Math.Max(0, group.MembersNum),
                group.GroupAnnouncement))
            .ToArray();

        return new(
            data.PageNum,
            data.PageSize,
            data.TotalPage,
            data.Total,
            items);
    }

    public async Task<WorkToolRobotSnapshot> GetRobotAsync(
        Guid robotConfigId,
        CancellationToken cancellationToken)
    {
        var robotId = await credentials.ResolveConfiguredRobotIdAsync(robotConfigId, cancellationToken);
        using var response = await SendAdministrativeAsync(
            () => CreateAdministrativeRequest(
                HttpMethod.Get,
                $"robot/robotInfo/get?robotId={Escape(robotId)}"),
            "get_robot",
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
        var robotId = await credentials.ResolveConfiguredRobotIdAsync(robotConfigId, cancellationToken);
        using var response = await SendAdministrativeAsync(
            () => CreateAdministrativeRequest(
                HttpMethod.Get,
                $"robot/robotInfo/online?robotId={Escape(robotId)}"),
            "get_online",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new(null, HttpFailure(response));
        }

        return new(null, null);
    }

    public async Task<WorkToolMessageCallbackConfiguration> ConfigureMessageCallbackAsync(
        Guid robotConfigId,
        WorkToolMessageCallbackRequest request,
        CancellationToken cancellationToken)
    {
        var robotId = await credentials.ResolveConfiguredRobotIdAsync(robotConfigId, cancellationToken);
        var body = new
        {
            openCallback = request.OpenCallback ? 1 : 0,
            replyAll = request.ReplyAll ? 1 : 0,
            callbackUrl = request.CallbackUrl.AbsoluteUri
        };
        using var response = await SendAdministrativeAsync(
            () => CreateAdministrativeRequest(
                HttpMethod.Post,
                $"robot/robotInfo/update?robotId={Escape(robotId)}",
                body),
            "configure_message_callback",
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
        var robotId = await credentials.ResolveConfiguredRobotIdAsync(robotConfigId, cancellationToken);
        using var response = await SendAdministrativeAsync(
            () => CreateAdministrativeRequest(
                HttpMethod.Get,
                $"robot/robotInfo/callBack/get?robotId={Escape(robotId)}&robotKey="),
            "list_event_callbacks",
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
        var robotId = await credentials.ResolveConfiguredRobotIdAsync(robotConfigId, cancellationToken);
        var body = new { type, callBackUrl = callbackUrl.AbsoluteUri };
        using var response = await SendAdministrativeAsync(
            () => CreateAdministrativeRequest(
                HttpMethod.Post,
                $"robot/robotInfo/callBack/bind?robotId={Escape(robotId)}",
                body),
            "bind_event_callback",
            cancellationToken);
        return await ReadMutationAsync(response, "bind_event_callback", cancellationToken);
    }

    public async Task<WorkToolCallbackMutationResult> DeleteEventCallbackAsync(
        Guid robotConfigId,
        int type,
        CancellationToken cancellationToken)
    {
        EnsureSupportedEventType(type);
        var robotId = await credentials.ResolveConfiguredRobotIdAsync(robotConfigId, cancellationToken);
        var body = new { type };
        using var response = await SendAdministrativeAsync(
            () => CreateAdministrativeRequest(
                HttpMethod.Post,
                $"robot/robotInfo/callBack/deleteByType?robotId={Escape(robotId)}",
                body),
            "delete_event_callback",
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
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"wework/sendRawMessage?robotId={Escape(robotId)}")
        {
            Content = JsonContent.Create(
                new { socketType = 2, list = new[] { command } },
                mediaType: null,
                options: JsonOptions)
        };
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var parsed = await ReadEnvelopeAsync<string>(response, "submit_command", cancellationToken);
        return ToSubmission(response, parsed.Envelope, parsed.Parsed);
    }

    private async Task<HttpResponseMessage> SendAdministrativeAsync(
        Func<HttpRequestMessage> requestFactory,
        string endpoint,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = requestFactory();
            try
            {
                return await httpClient.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException exception) when (
                attempt == 0 &&
                exception is not WorkToolRateLimitException &&
                !cancellationToken.IsCancellationRequested)
            {
                logger?.LogWarning(
                    exception,
                    "WorkTool administrative endpoint {Endpoint} had a transport failure; retrying once with a fresh HTTP/1.1 connection.",
                    endpoint);
            }
        }
    }

    private static HttpRequestMessage CreateAdministrativeRequest(
        HttpMethod method,
        string requestUri,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, requestUri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.ConnectionClose = true;
        if (body is not null)
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json");
        return request;
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

    private static string? OpaqueRaw(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => value.GetRawText()
        };

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

    private sealed record GroupPageData(
        int PageNum,
        int PageSize,
        int TotalPage,
        int Total,
        IReadOnlyList<GroupData>? List);

    private sealed record GroupData(
        string GroupName,
        string? MasterName,
        int MembersNum,
        string? GroupAnnouncement);

    private sealed record RawCommandResultData(
        JsonElement RawMsg,
        int RawSuccess,
        string? ErrorReason,
        JsonElement RunTime,
        int ApiSend,
        int Type,
        string MessageId,
        JsonElement SuccessList,
        JsonElement FailList,
        decimal? TimeCost);

    private sealed record CallbackData(
        long Id,
        int Type,
        string CallBackUrl,
        string? TypeName);
}

public sealed class WorkToolGroupListException(string failureCode)
    : InvalidOperationException("WorkTool group list request failed.")
{
    public string FailureCode { get; } = failureCode;
}

public sealed class WorkToolRawResultException(string failureCode)
    : InvalidOperationException("WorkTool raw result request failed.")
{
    public string FailureCode { get; } = failureCode;
}
