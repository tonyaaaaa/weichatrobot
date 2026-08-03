using System.Net;
using System.Text;
using WechatRobot.LegacyVisaImport;

namespace WechatRobot.LegacyVisaImportTests;

public sealed class KnowledgeApiClientTests
{
    [Fact]
    public async Task ApprovePreviews_refetches_revision_and_retries_a_conflict()
    {
        var requests = new List<(HttpMethod Method, string Path, string? Body)>();
        var handler = new StubHandler(async request =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            return requests.Count switch
            {
                1 => Response(HttpStatusCode.Conflict, "{\"error\":\"preview-revision-conflict\"}"),
                2 => Response(HttpStatusCode.OK,
                    "{\"versionId\":\"8cf64610-2f35-4499-a9b2-19089fb2f918\",\"revision\":2,\"items\":[{\"id\":\"95417542-185d-4358-a00d-bcecf20bec3d\",\"sequence\":0,\"text\":\"材料\"}]}"),
                _ => Response(HttpStatusCode.OK, "[]")
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new KnowledgeApiClient(http);

        await client.ApprovePreviewsAsync(Guid.NewGuid(), 1, CancellationToken.None);

        Assert.Equal([HttpMethod.Post, HttpMethod.Get, HttpMethod.Post], requests.Select(x => x.Method));
        Assert.Contains("\"expectedRevision\":2", requests[2].Body);
    }

    [Fact]
    public async Task Upload_failure_reports_only_operation_status_and_safe_error_code()
    {
        var handler = new StubHandler(_ => Task.FromResult(
            Response(HttpStatusCode.Conflict,
                "{\"error\":\"duplicate-content\",\"message\":\"secret upstream detail\"}")));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new KnowledgeApiClient(http);
        var document = new RenderedVisaDocument("visa.md", "# Visa", new string('a', 64));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.UploadAsync(document, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal("knowledge_api_failed:upload:409:duplicate-content", exception.Message);
        Assert.DoesNotContain("secret upstream detail", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetVersions_returns_sha_for_authoritative_duplicate_recovery()
    {
        var versionId = Guid.NewGuid();
        var sha = new string('b', 64);
        var handler = new StubHandler(_ => Task.FromResult(Response(HttpStatusCode.OK,
            $"[{{\"id\":\"{versionId:D}\",\"sha256\":\"{sha}\",\"status\":\"preview\"}}]")));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new KnowledgeApiClient(http);

        var version = Assert.Single(await client.GetVersionsAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(versionId, version.Id);
        Assert.Equal(sha, version.Sha256);
        Assert.Equal("preview", version.Status);
    }

    [Fact]
    public async Task QueueIndex_waits_and_retries_when_rate_limited()
    {
        var attempts = 0;
        var handler = new StubHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                var limited = Response(HttpStatusCode.TooManyRequests, "{\"error\":\"rate-limited\"}");
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(limited);
            }

            return Task.FromResult(Response(HttpStatusCode.OK,
                "{\"jobId\":\"8cf64610-2f35-4499-a9b2-19089fb2f918\"}"));
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new KnowledgeApiClient(http);

        var result = await client.QueueIndexAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal(Guid.Parse("8cf64610-2f35-4499-a9b2-19089fb2f918"), result.JobId);
    }

    [Fact]
    public async Task Get_requests_wait_and_retry_when_rate_limited()
    {
        var attempts = 0;
        var handler = new StubHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                var limited = Response(HttpStatusCode.TooManyRequests, "{\"error\":\"rate-limited\"}");
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(limited);
            }
            return Task.FromResult(Response(HttpStatusCode.OK, "[]"));
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new KnowledgeApiClient(http);

        var tags = await client.GetTagOptionsAsync(CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Empty(tags);
    }

    [Fact]
    public async Task Get_requests_retry_a_temporary_service_unavailable_response()
    {
        var attempts = 0;
        var handler = new StubHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                var unavailable = Response(
                    HttpStatusCode.ServiceUnavailable,
                    "{\"error\":\"qdrant-unavailable\"}");
                unavailable.Headers.RetryAfter =
                    new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(unavailable);
            }
            return Task.FromResult(Response(HttpStatusCode.OK, "[]"));
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new KnowledgeApiClient(http);

        var tags = await client.GetTagOptionsAsync(CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Empty(tags);
    }

    [Fact]
    public async Task RetryIndex_posts_the_failed_job_id()
    {
        var jobId = Guid.NewGuid();
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return Task.FromResult(Response(HttpStatusCode.Accepted, "{}"));
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new KnowledgeApiClient(http);

        await client.RetryIndexAsync(jobId, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal($"/api/knowledge/index-jobs/{jobId:D}/retry", captured.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Upload_returns_retryable_failed_result_from_service_unavailable_response()
    {
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var handler = new StubHandler(_ => Task.FromResult(Response(
            HttpStatusCode.ServiceUnavailable,
            $"{{\"documentId\":\"{documentId:D}\",\"versionId\":\"{versionId:D}\",\"version\":1,\"state\":\"failed\",\"safeFileName\":\"visa.md\"}}")));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new KnowledgeApiClient(http);

        var result = await client.UploadAsync(
            new RenderedVisaDocument("visa.md", "# Visa", new string('a', 64)),
            null,
            CancellationToken.None);

        Assert.Equal(documentId, result.DocumentId);
        Assert.Equal(versionId, result.VersionId);
        Assert.Equal("failed", result.State);
    }

    [Fact]
    public async Task RetryUpload_uses_current_document_state_version()
    {
        var documentId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var requests = new List<(HttpMethod Method, string Path, string? Body)>();
        var handler = new StubHandler(async request =>
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            return requests.Count == 1
                ? Response(HttpStatusCode.OK, $"{{\"id\":\"{documentId:D}\",\"stateVersion\":7}}")
                : Response(HttpStatusCode.OK,
                    $"{{\"documentId\":\"{documentId:D}\",\"versionId\":\"{versionId:D}\",\"version\":1,\"state\":\"uploaded\",\"safeFileName\":\"visa.md\"}}");
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new KnowledgeApiClient(http);

        var result = await client.RetryUploadAsync(documentId, CancellationToken.None);

        Assert.Equal("uploaded", result.State);
        Assert.Equal([HttpMethod.Get, HttpMethod.Post], requests.Select(item => item.Method));
        Assert.Contains("\"expectedStateVersion\":7", requests[1].Body);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
