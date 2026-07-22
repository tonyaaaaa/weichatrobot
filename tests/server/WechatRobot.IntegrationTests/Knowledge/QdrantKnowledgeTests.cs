using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Knowledge;

namespace WechatRobot.IntegrationTests.Knowledge;

public sealed class QdrantKnowledgeTests : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("qdrant/qdrant:v1.18.2")
        .WithPortBinding(6333, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(6333).ForPath("/readyz")))
        .Build();
    private HttpClient _http = null!;
    private QdrantVectorStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_container.GetMappedPublicPort(6333)}") };
        _store = new QdrantVectorStore(_http);
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _container.DisposeAsync();
    }

    [Fact]
    public async Task Search_enforces_tag_or_global_and_active_version_filters_without_storing_text()
    {
        var collection = new VectorCollection("kb_cosine_3_test", 3, VectorDistance.Cosine);
        var version = Guid.NewGuid();
        var inactiveVersion = Guid.NewGuid();
        var product = Guid.NewGuid();
        var support = Guid.NewGuid();
        var unrelated = Guid.NewGuid();
        var global = Guid.NewGuid();
        var productChunk = Guid.NewGuid();
        var supportChunk = Guid.NewGuid();
        var unrelatedChunk = Guid.NewGuid();
        var globalChunk = Guid.NewGuid();
        var inactiveChunk = Guid.NewGuid();
        await _store.EnsureCollectionAsync(collection, TestContext.Current.CancellationToken);
        await _store.UpsertAsync(collection,
        [
            Point(productChunk, version, product), Point(supportChunk, version, support), Point(unrelatedChunk, version, unrelated),
            Point(globalChunk, version, global), Point(inactiveChunk, inactiveVersion, product)
        ], TestContext.Current.CancellationToken);
        Assert.Empty(await _store.SearchAsync(new VectorSearchRequest(collection, [1, 0, 0], [product], [version], global, 10), TestContext.Current.CancellationToken));
        await _store.SetVersionActiveAsync(collection, version, true, TestContext.Current.CancellationToken);
        await _store.SetVersionActiveAsync(collection, inactiveVersion, true, TestContext.Current.CancellationToken);

        var hits = await _store.SearchAsync(new VectorSearchRequest(collection, [1, 0, 0], [product, support], [version], global, 10), TestContext.Current.CancellationToken);

        Assert.Equal(3, hits.Count);
        Assert.Contains(hits, hit => hit.ChunkId == productChunk);
        Assert.Contains(hits, hit => hit.ChunkId == supportChunk);
        Assert.Contains(hits, hit => hit.ChunkId == globalChunk);
        Assert.DoesNotContain(hits, hit => hit.ChunkId is var id && (id == unrelatedChunk || id == inactiveChunk));

        using var payload = await ReadPayloadAsync(collection.Name, productChunk);
        Assert.False(payload.RootElement.ToString().Contains("text", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(version.ToString("D"), payload.RootElement.GetProperty("version_id").GetString());

        await _store.DeleteVersionAsync(collection, version, TestContext.Current.CancellationToken);
        Assert.Empty(await _store.SearchAsync(new VectorSearchRequest(collection, [1, 0, 0], [product], [version], global, 10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Existing_collection_with_changed_dimension_or_metric_requires_explicit_reindex()
    {
        await _store.EnsureCollectionAsync(new VectorCollection("kb_contract_test", 3, VectorDistance.Cosine), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<VectorCollectionConfigurationException>(() =>
            _store.EnsureCollectionAsync(new VectorCollection("kb_contract_test", 4, VectorDistance.Cosine), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<VectorCollectionConfigurationException>(() =>
            _store.EnsureCollectionAsync(new VectorCollection("kb_contract_test", 3, VectorDistance.Dot), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Deleting_unique_generation_collection_removes_accepted_write_and_rejects_late_upsert_without_recreation()
    {
        var collection = new VectorCollection($"kb_cosine_3_g1_{Guid.NewGuid():N}", 3, VectorDistance.Cosine);
        var version = Guid.NewGuid();
        await _store.EnsureCollectionAsync(collection, TestContext.Current.CancellationToken);
        await _store.UpsertAsync(collection, [Point(Guid.NewGuid(), version, Guid.NewGuid())], TestContext.Current.CancellationToken);
        Assert.Equal(collection, await _store.InspectCollectionAsync(collection.Name, TestContext.Current.CancellationToken));

        await _store.DeleteCollectionAsync(collection, TestContext.Current.CancellationToken);

        Assert.Null(await _store.InspectCollectionAsync(collection.Name, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<VectorCollectionConfigurationException>(() => _store.UpsertAsync(collection,
            [Point(Guid.NewGuid(), version, Guid.NewGuid())], TestContext.Current.CancellationToken));
        Assert.Null(await _store.InspectCollectionAsync(collection.Name, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Same_version_reindex_stages_in_another_generation_and_failure_leaves_live_points_retrievable()
    {
        var version = Guid.NewGuid();
        var tag = Guid.NewGuid();
        var chunk = Guid.NewGuid();
        var live = new VectorCollection("kb_cosine_3_live", 3, VectorDistance.Cosine);
        var staging = new VectorCollection("kb_cosine_3_staging", 3, VectorDistance.Cosine);
        await _store.EnsureCollectionAsync(live, TestContext.Current.CancellationToken);
        await _store.EnsureCollectionAsync(staging, TestContext.Current.CancellationToken);
        await _store.UpsertAsync(live, [new VectorPoint(chunk, Guid.NewGuid(), version, [tag], [1, 0, 0], true, 1)], TestContext.Current.CancellationToken);
        await _store.UpsertAsync(staging, [new VectorPoint(chunk, Guid.NewGuid(), version, [tag], [0, 1, 0], false, 2)], TestContext.Current.CancellationToken);

        Assert.Single(await _store.SearchAsync(new VectorSearchRequest(live, [1, 0, 0], [tag], [version], null), TestContext.Current.CancellationToken));
        Assert.Empty(await _store.SearchAsync(new VectorSearchRequest(staging, [0, 1, 0], [tag], [version], null), TestContext.Current.CancellationToken));
        var staged = Assert.Single(await _store.InspectVersionAsync(staging, version, TestContext.Current.CancellationToken));
        Assert.Equal(2, staged.Generation);

        await _store.SetVersionActiveAsync(staging, version, true, TestContext.Current.CancellationToken);
        Assert.Single(await _store.SearchAsync(new VectorSearchRequest(staging, [0, 1, 0], [tag], [version], null), TestContext.Current.CancellationToken));
        await _store.DeleteVersionAsync(live, version, TestContext.Current.CancellationToken);
        Assert.Empty(await _store.SearchAsync(new VectorSearchRequest(live, [1, 0, 0], [tag], [version], null), TestContext.Current.CancellationToken));
    }

    private static VectorPoint Point(Guid id, Guid version, Guid tag) => new(id, Guid.NewGuid(), version, [tag], [1, 0, 0], false);

    private async Task<JsonDocument> ReadPayloadAsync(string collection, Guid id)
    {
        var response = await _http.PostAsJsonAsync($"/collections/{collection}/points", new { ids = new[] { id.ToString("D") }, with_payload = true, with_vector = false }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        var payload = json.RootElement.GetProperty("result")[0].GetProperty("payload");
        return JsonDocument.Parse(payload.GetRawText());
    }
}
