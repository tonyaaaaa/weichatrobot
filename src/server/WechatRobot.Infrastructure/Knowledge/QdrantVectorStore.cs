using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WechatRobot.Application.Knowledge;

namespace WechatRobot.Infrastructure.Knowledge;

public sealed class QdrantVectorStore(HttpClient httpClient) : IVectorStore
{
    public async Task EnsureCollectionAsync(VectorCollection collection, CancellationToken cancellationToken)
    {
        using var existing = await httpClient.GetAsync($"/collections/{Uri.EscapeDataString(collection.Name)}", cancellationToken);
        if (existing.StatusCode == HttpStatusCode.NotFound)
        {
            using var created = await httpClient.PutAsJsonAsync($"/collections/{Uri.EscapeDataString(collection.Name)}", new
            {
                vectors = new { size = collection.Dimension, distance = DistanceName(collection.Distance) }
            }, cancellationToken);
            if (!created.IsSuccessStatusCode && created.StatusCode != HttpStatusCode.Conflict)
                throw await MapFailureAsync(created, "create collection", cancellationToken);
            if (created.IsSuccessStatusCode) return;
            using var raced = await httpClient.GetAsync($"/collections/{Uri.EscapeDataString(collection.Name)}", cancellationToken);
            await ValidateCollectionAsync(raced, collection, cancellationToken);
            return;
        }
        await ValidateCollectionAsync(existing, collection, cancellationToken);
    }

    public async Task UpsertAsync(VectorCollection collection, IReadOnlyList<VectorPoint> points, CancellationToken cancellationToken)
    {
        if (points.Count == 0) return;
        using var response = await httpClient.PutAsJsonAsync($"/collections/{Uri.EscapeDataString(collection.Name)}/points?wait=true", new
        {
            points = points.Select(point => new
            {
                id = point.Id.ToString("D"),
                vector = point.Vector,
                payload = new
                {
                    chunk_id = point.Id.ToString("D"), document_id = point.DocumentId.ToString("D"),
                    version_id = point.VersionId.ToString("D"), tag_ids = point.TagIds.Select(tag => tag.ToString("D")).ToArray(), active = point.Active,
                    generation = point.Generation
                }
            })
        }, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await MapFailureAsync(response, "upsert points", cancellationToken);
    }

    public async Task SetVersionActiveAsync(VectorCollection collection, Guid versionId, bool active, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"/collections/{Uri.EscapeDataString(collection.Name)}/points/payload?wait=true", new
        {
            payload = new { active }, filter = VersionFilter(versionId)
        }, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await MapFailureAsync(response, "set version payload", cancellationToken);
    }

    public async Task DeleteCollectionAsync(VectorCollection collection, CancellationToken cancellationToken)
    {
        using var response = await httpClient.DeleteAsync($"/collections/{Uri.EscapeDataString(collection.Name)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        if (!response.IsSuccessStatusCode) throw await MapFailureAsync(response, "delete collection", cancellationToken);
    }

    public async Task<VectorCollection?> InspectCollectionAsync(string collectionName, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"/collections/{Uri.EscapeDataString(collectionName)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw await MapFailureAsync(response, "read collection", cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var vectors = json.RootElement.GetProperty("result").GetProperty("config").GetProperty("params").GetProperty("vectors");
        return new VectorCollection(collectionName, vectors.GetProperty("size").GetInt32(),
            Enum.Parse<VectorDistance>(vectors.GetProperty("distance").GetString()!, true));
    }

    public async Task DeleteVersionAsync(VectorCollection collection, Guid versionId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"/collections/{Uri.EscapeDataString(collection.Name)}/points/delete?wait=true", new
        {
            filter = VersionFilter(versionId)
        }, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        if (!response.IsSuccessStatusCode) throw await MapFailureAsync(response, "delete version", cancellationToken);
    }

    public async Task<IReadOnlyList<VectorPointMetadata>> InspectVersionAsync(VectorCollection collection, Guid versionId, CancellationToken cancellationToken)
    {
        var result = new List<VectorPointMetadata>();
        string? offset = null;
        do
        {
            using var response = await httpClient.PostAsJsonAsync($"/collections/{Uri.EscapeDataString(collection.Name)}/points/scroll", new
            {
                filter = VersionFilter(versionId), limit = 256, offset, with_payload = true, with_vector = false
            }, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return [];
            if (!response.IsSuccessStatusCode) throw await MapFailureAsync(response, "inspect version", cancellationToken);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var page = json.RootElement.GetProperty("result");
            foreach (var item in page.GetProperty("points").EnumerateArray())
            {
                var payload = item.GetProperty("payload");
                result.Add(new VectorPointMetadata(Guid.Parse(payload.GetProperty("chunk_id").GetString()!),
                    Guid.Parse(payload.GetProperty("document_id").GetString()!), Guid.Parse(payload.GetProperty("version_id").GetString()!),
                    payload.GetProperty("tag_ids").EnumerateArray().Select(value => Guid.Parse(value.GetString()!)).ToArray(),
                    payload.GetProperty("active").GetBoolean(), payload.TryGetProperty("generation", out var generation) ? generation.GetInt32() : 1));
            }
            offset = page.TryGetProperty("next_page_offset", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        } while (offset is not null);
        return result;
    }

    public async Task<IReadOnlyList<VectorSearchHit>> SearchAsync(VectorSearchRequest request, CancellationToken cancellationToken)
    {
        var tagIds = request.AllowedTagIds.Concat(request.GlobalPublicTagId is { } global ? [global] : []).Distinct().Select(id => id.ToString("D")).ToArray();
        if (tagIds.Length == 0 || request.ActiveVersionIds.Count == 0) return [];
        using var response = await httpClient.PostAsJsonAsync($"/collections/{Uri.EscapeDataString(request.Collection.Name)}/points/search", new
        {
            vector = request.Vector,
            limit = request.Limit,
            with_payload = true,
            with_vector = false,
            filter = new
            {
                must = new object[]
                {
                    new { key = "active", match = new { value = true } },
                    new { key = "version_id", match = new { any = request.ActiveVersionIds.Select(id => id.ToString("D")).ToArray() } },
                    new { key = "tag_ids", match = new { any = tagIds } }
                }
            }
        }, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await MapFailureAsync(response, "search points", cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return json.RootElement.GetProperty("result").EnumerateArray().Select(item =>
        {
            var payload = item.GetProperty("payload");
            return new VectorSearchHit(Guid.Parse(payload.GetProperty("chunk_id").GetString()!),
                Guid.Parse(payload.GetProperty("document_id").GetString()!), Guid.Parse(payload.GetProperty("version_id").GetString()!),
                item.GetProperty("score").GetDouble());
        }).ToArray();
    }

    private static object VersionFilter(Guid versionId) => new
    {
        must = new[] { new { key = "version_id", match = new { value = versionId.ToString("D") } } }
    };

    private static async Task ValidateCollectionAsync(HttpResponseMessage response, VectorCollection expected, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode) throw await MapFailureAsync(response, "read collection", token);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
        var vectors = json.RootElement.GetProperty("result").GetProperty("config").GetProperty("params").GetProperty("vectors");
        var dimension = vectors.GetProperty("size").GetInt32();
        var distance = vectors.GetProperty("distance").GetString();
        if (dimension != expected.Dimension || !string.Equals(distance, DistanceName(expected.Distance), StringComparison.OrdinalIgnoreCase))
            throw new VectorCollectionConfigurationException($"Collection '{expected.Name}' is {distance}/{dimension}; explicit reindex is required for {DistanceName(expected.Distance)}/{expected.Dimension}.");
    }

    private static async Task<Exception> MapFailureAsync(HttpResponseMessage response, string operation, CancellationToken token)
    {
        var detail = await response.Content.ReadAsStringAsync(token);
        var message = $"Qdrant {operation} failed with HTTP {(int)response.StatusCode}: {detail}";
        return (int)response.StatusCode >= 500 ? new VectorStoreUnavailableException(message) : new VectorCollectionConfigurationException(message);
    }

    private static string DistanceName(VectorDistance distance) => distance switch
    {
        VectorDistance.Cosine => "Cosine", VectorDistance.Dot => "Dot", VectorDistance.Euclid => "Euclid", _ => throw new ArgumentOutOfRangeException(nameof(distance))
    };
}
