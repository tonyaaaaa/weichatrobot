using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WechatRobot.Application.Knowledge;
using WechatRobot.Application.Memory;

namespace WechatRobot.Infrastructure.Memory;

public sealed class QdrantMemoryVectorIndex(HttpClient httpClient) : IMemoryVectorIndex
{
    public async Task IndexAsync(
        MemoryVectorDocument document,
        int dimension,
        VectorDistance distance,
        CancellationToken cancellationToken = default)
    {
        var collection = CollectionName(dimension, distance, document.Generation);
        await EnsureCollectionAsync(collection, dimension, distance, cancellationToken);
        using var response = await httpClient.PutAsJsonAsync(
            $"/collections/{Uri.EscapeDataString(collection)}/points?wait=true",
            new
            {
                points = new[]
                {
                    new
                    {
                        id = document.MemoryEntryId.ToString("D"),
                        vector = document.Vector,
                        payload = new
                        {
                            memory_id = document.MemoryEntryId.ToString("D"),
                            scope_type = document.ScopeType,
                            robot_id = document.RobotConfigId?.ToString("D"),
                            group_id = document.GroupProfileId?.ToString("D"),
                            subject_key = document.SubjectKey,
                            memory_type = document.MemoryType,
                            status_version = document.StatusVersion,
                            generation = document.Generation
                        }
                    }
                }
            },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<MemoryVectorHit>> SearchAsync(
        IReadOnlyList<float> vector,
        int dimension,
        VectorDistance distance,
        int generation,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var collection = CollectionName(dimension, distance, generation);
        using var response = await httpClient.PostAsJsonAsync(
            $"/collections/{Uri.EscapeDataString(collection)}/points/search",
            new
            {
                vector,
                limit = Math.Clamp(limit, 1, 50),
                with_payload = true,
                with_vector = false
            },
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return json.RootElement.GetProperty("result")
            .EnumerateArray()
            .Select(item => new MemoryVectorHit(
                Guid.Parse(item.GetProperty("payload").GetProperty("memory_id").GetString()!),
                item.GetProperty("score").GetDouble()))
            .ToArray();
    }

    public async Task RemoveAsync(
        Guid memoryEntryId,
        int dimension,
        VectorDistance distance,
        int generation,
        CancellationToken cancellationToken = default)
    {
        var collection = CollectionName(dimension, distance, generation);
        using var response = await httpClient.PostAsJsonAsync(
            $"/collections/{Uri.EscapeDataString(collection)}/points/delete?wait=true",
            new { points = new[] { memoryEntryId.ToString("D") } },
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task EnsureCollectionAsync(
        string name,
        int dimension,
        VectorDistance distance,
        CancellationToken cancellationToken)
    {
        using var existing = await httpClient.GetAsync(
            $"/collections/{Uri.EscapeDataString(name)}",
            cancellationToken);
        if (existing.IsSuccessStatusCode)
        {
            return;
        }
        if (existing.StatusCode != HttpStatusCode.NotFound)
        {
            existing.EnsureSuccessStatusCode();
        }

        using var created = await httpClient.PutAsJsonAsync(
            $"/collections/{Uri.EscapeDataString(name)}",
            new
            {
                vectors = new
                {
                    size = dimension,
                    distance = distance.ToString()
                }
            },
            cancellationToken);
        if (created.StatusCode != HttpStatusCode.Conflict)
        {
            created.EnsureSuccessStatusCode();
        }
    }

    public static string CollectionName(int dimension, VectorDistance distance, int generation) =>
        $"memory_{distance.ToString().ToLowerInvariant()}_{dimension}_g{generation}";
}
