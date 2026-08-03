using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace WechatRobot.LegacyVisaImport;

public sealed record KnowledgeUploadResult(
    Guid DocumentId,
    Guid VersionId,
    int Version,
    string State,
    string SafeFileName);
public sealed record PreviewSet(Guid VersionId, int Revision, IReadOnlyList<PreviewItem> Items);
public sealed record PreviewItem(Guid Id, int Sequence, string Text, string? Status);
public sealed record KnowledgeIndexJob(Guid JobId);
public sealed record KnowledgeDocumentVersionMatch(
    Guid Id,
    string Sha256,
    string Status,
    IReadOnlyList<KnowledgeIndexJobMatch>? IndexJobs = null);
public sealed record KnowledgeIndexJobMatch(Guid Id, string Status);
public sealed record KnowledgeIndexStatus(
    Guid DocumentId,
    Guid? ActiveVersionId,
    string DocumentStatus,
    int ApprovedChunkCount,
    int? ActivePointCount,
    string Consistency,
    IReadOnlyList<string> DriftDetails);

public sealed class KnowledgeApiClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        using var response = await SendWithRateLimitAsync(() => new(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email, password }, options: JsonOptions)
        }, cancellationToken);
        var login = await ReadAsync<LoginResponse>(response, "login", cancellationToken);
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.AccessToken);
    }

    public async Task<IReadOnlyList<KnowledgeTagOption>> GetTagOptionsAsync(
        CancellationToken cancellationToken) =>
        await GetAsync<KnowledgeTagOption[]>("/api/knowledge/tags/options", cancellationToken);

    public async Task<IReadOnlyList<KnowledgeDocumentMatch>> GetAllDocumentsAsync(
        CancellationToken cancellationToken)
    {
        var result = new List<KnowledgeDocumentMatch>();
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await GetAsync<DocumentPage>(
                $"/api/knowledge/documents?page={pageNumber}&pageSize=100",
                cancellationToken);
            result.AddRange(page.Items.Select(item =>
                new KnowledgeDocumentMatch(item.Id, item.Title, item.Status)));
            if (result.Count >= page.Total || page.Items.Count == 0) return result;
        }
    }

    public async Task<IReadOnlyList<KnowledgeDocumentVersionMatch>> GetVersionsAsync(
        Guid documentId,
        CancellationToken cancellationToken) =>
        await GetAsync<KnowledgeDocumentVersionMatch[]>(
            $"/api/knowledge/documents/{documentId:D}/versions",
            cancellationToken);

    public async Task<KnowledgeUploadResult> UploadAsync(
        RenderedVisaDocument rendered,
        Guid? documentId,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithRateLimitAsync(() =>
        {
            var form = new MultipartFormDataContent();
            var content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(rendered.Markdown));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/markdown") { CharSet = "utf-8" };
            form.Add(content, "file", rendered.FileName);
            if (documentId is not null)
                form.Add(new StringContent(documentId.Value.ToString("D")), "documentId");
            return new HttpRequestMessage(HttpMethod.Post, "/api/knowledge/documents") { Content = form };
        }, cancellationToken);
        return await ReadUploadAsync(response, "upload", cancellationToken);
    }

    public async Task<KnowledgeUploadResult> RetryUploadAsync(
        Guid documentId,
        CancellationToken cancellationToken)
    {
        KnowledgeUploadResult? latest = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var state = await GetAsync<DocumentState>(
                $"/api/knowledge/documents/{documentId:D}", cancellationToken);
            using var response = await SendWithRateLimitAsync(() => new(
                HttpMethod.Post, $"/api/knowledge/documents/{documentId:D}/retry-upload")
            {
                Content = JsonContent.Create(
                    new { expectedStateVersion = state.StateVersion }, options: JsonOptions)
            }, cancellationToken);
            latest = await ReadUploadAsync(response, "retry-upload", cancellationToken);
            if (!string.Equals(latest.State, "failed", StringComparison.Ordinal)) return latest;
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt * 2, 10)), cancellationToken);
        }
        return latest ?? throw new InvalidOperationException("retry_upload_empty_response");
    }

    public Task<PreviewSet> GetPreviewsAsync(
        Guid versionId,
        CancellationToken cancellationToken) =>
        GetAsync<PreviewSet>(
            $"/api/knowledge/versions/{versionId:D}/previews",
            cancellationToken);

    public async Task ApprovePreviewsAsync(
        Guid versionId,
        int revision,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var response = await SendWithRateLimitAsync(() => new(
                HttpMethod.Post, $"/api/knowledge/versions/{versionId:D}/previews/approve")
            {
                Content = JsonContent.Create(new { expectedRevision = revision }, options: JsonOptions)
            }, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                await EnsureSuccessAsync(response, "approve-previews", cancellationToken);
                return;
            }
            if (response.StatusCode != System.Net.HttpStatusCode.Conflict || attempt >= 3)
            {
                await EnsureSuccessAsync(response, "approve-previews", cancellationToken);
                return;
            }
            var current = await GetPreviewsAsync(versionId, cancellationToken);
            if (current.Items.Count == 0)
                throw new InvalidOperationException("preview_disappeared_during_approval");
            revision = current.Revision;
            await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
        }
    }

    public async Task<KnowledgeIndexJob> QueueIndexAsync(
        Guid documentId,
        Guid versionId,
        Guid tagId,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithRateLimitAsync(() => new(
            HttpMethod.Post,
            $"/api/knowledge/documents/{documentId:D}/versions/{versionId:D}/index")
        {
            Content = JsonContent.Create(new { tagIds = new[] { tagId } }, options: JsonOptions)
        }, cancellationToken);
        return await ReadAsync<KnowledgeIndexJob>(response, "queue-index", cancellationToken);
    }

    public async Task RetryIndexAsync(Guid jobId, CancellationToken cancellationToken)
    {
        using var response = await SendWithRateLimitAsync(() => new(
            HttpMethod.Post, $"/api/knowledge/index-jobs/{jobId:D}/retry"), cancellationToken);
        await EnsureSuccessAsync(response, "retry-index", cancellationToken);
    }

    public Task<KnowledgeIndexStatus> GetIndexStatusAsync(
        Guid documentId,
        CancellationToken cancellationToken) =>
        GetAsync<KnowledgeIndexStatus>(
            $"/api/knowledge/documents/{documentId:D}/index-status?checkConsistency=true",
            cancellationToken);

    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var response = await SendWithRateLimitAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
        return await ReadAsync<T>(response, "get", cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithRateLimitAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = requestFactory();
            var response = await httpClient.SendAsync(request, cancellationToken);
            var retryableRateLimit = response.StatusCode ==
                                     System.Net.HttpStatusCode.TooManyRequests;
            var retryableUnavailable = request.Method == HttpMethod.Get &&
                                       response.StatusCode ==
                                       System.Net.HttpStatusCode.ServiceUnavailable;
            if ((!retryableRateLimit && !retryableUnavailable) || attempt >= 12)
                return response;

            var delay = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                        ?? TimeSpan.FromSeconds(Math.Min(attempt * 2, 15));
            response.Dispose();
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, operation, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("knowledge_api_empty_response");
    }

    private static async Task<KnowledgeUploadResult> ReadUploadAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable)
            await EnsureSuccessAsync(response, operation, cancellationToken);
        return await response.Content.ReadFromJsonAsync<KnowledgeUploadResult>(
                   JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("knowledge_api_empty_response");
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        string? error = null;
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions, cancellationToken);
            if (payload?.Error is { Length: > 0 } candidate &&
                candidate.Length <= 64 &&
                candidate.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
                error = candidate;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            error = null;
        }
        throw new HttpRequestException(
            $"knowledge_api_failed:{operation}:{(int)response.StatusCode}" +
            (error is null ? string.Empty : $":{error}"),
            null,
            response.StatusCode);
    }

    private sealed record LoginResponse(string AccessToken);
    private sealed record ApiError(string? Error);
    private sealed record DocumentPage(IReadOnlyList<DocumentItem> Items, int Total);
    private sealed record DocumentItem(Guid Id, string Title, string Status);
    private sealed record DocumentState(Guid Id, int StateVersion);
}
