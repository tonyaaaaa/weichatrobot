using System.Net;
using System.Text;
using System.Text.Json;
using WechatRobot.Application.Knowledge;
using WechatRobot.Infrastructure.Knowledge;

namespace WechatRobot.ContractTests.Knowledge;

public sealed class QdrantVectorStoreContractTests
{
    [Fact]
    public async Task Shared_collection_creates_required_payload_indexes()
    {
        var handler = new QdrantContractHandler();
        var store = Store(handler);
        var collection = new VectorCollection("kb_shared_0123456789abcdef_cosine_3", 3, VectorDistance.Cosine);

        await store.EnsurePayloadIndexesAsync(collection, TestContext.Current.CancellationToken);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["active"] = "bool",
                ["version_id"] = "keyword",
                ["document_id"] = "keyword",
                ["tag_ids"] = "keyword"
            },
            handler.PayloadIndexes);
    }

    [Fact]
    public async Task Existing_vectors_are_read_in_bounded_pages_with_payload()
    {
        var versionId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var handler = new QdrantContractHandler(
            Point(firstId, documentId, versionId, tagId, [1f, 0f, 0f]),
            Point(secondId, documentId, versionId, tagId, [0f, 1f, 0f]));
        var store = Store(handler);
        var collection = new VectorCollection("kb_legacy", 3, VectorDistance.Cosine);

        var first = await store.ReadVersionPointsAsync(
            collection, versionId, null, 1, TestContext.Current.CancellationToken);
        var second = await store.ReadVersionPointsAsync(
            collection, versionId, first.NextOffset, 1, TestContext.Current.CancellationToken);

        Assert.Equal(firstId, Assert.Single(first.Points).Id);
        Assert.Equal(secondId, Assert.Single(second.Points).Id);
        Assert.Equal([1f, 0f, 0f], first.Points[0].Vector);
        Assert.Equal([0f, 1f, 0f], second.Points[0].Vector);
        Assert.Equal(firstId.ToString("D"), first.NextOffset);
        Assert.Null(second.NextOffset);
        Assert.All(first.Points.Concat(second.Points), point =>
        {
            Assert.Equal(documentId, point.DocumentId);
            Assert.Equal(versionId, point.VersionId);
            Assert.Equal([tagId], point.TagIds);
            Assert.True(point.Active);
            Assert.Equal(3, point.Generation);
        });
        Assert.All(handler.ScrollPageSizes, size => Assert.Equal(1, size));
    }

    private static QdrantVectorStore Store(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://qdrant.example.test") });

    private static object Point(
        Guid id,
        Guid documentId,
        Guid versionId,
        Guid tagId,
        float[] vector) =>
        new
        {
            id = id.ToString("D"),
            vector,
            payload = new
            {
                chunk_id = id.ToString("D"),
                document_id = documentId.ToString("D"),
                version_id = versionId.ToString("D"),
                tag_ids = new[] { tagId.ToString("D") },
                active = true,
                generation = 3
            }
        };

    private sealed class QdrantContractHandler(params object[] points) : HttpMessageHandler
    {
        public Dictionary<string, string> PayloadIndexes { get; } = new(StringComparer.Ordinal);
        public List<int> ScrollPageSizes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put && path.EndsWith("/index", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(cancellationToken));
                PayloadIndexes[body.RootElement.GetProperty("field_name").GetString()!] =
                    body.RootElement.GetProperty("field_schema").GetString()!;
                return Json(HttpStatusCode.OK, new { result = true, status = "ok" });
            }

            if (request.Method == HttpMethod.Get && path.Contains("/collections/", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, new
                {
                    result = new
                    {
                        payload_schema = PayloadIndexes.ToDictionary(
                            item => item.Key,
                            item => new { data_type = item.Value })
                    },
                    status = "ok"
                });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/points/scroll", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStreamAsync(cancellationToken));
                ScrollPageSizes.Add(body.RootElement.GetProperty("limit").GetInt32());
                var offset = body.RootElement.GetProperty("offset");
                var index = offset.ValueKind == JsonValueKind.Null ? 0 : 1;
                var next = index == 0 && points.Length > 1
                    ? JsonSerializer.SerializeToElement(((JsonElement)JsonSerializer.SerializeToElement(points[0])).GetProperty("id").GetString())
                    : JsonSerializer.SerializeToElement<string?>(null);
                return Json(HttpStatusCode.OK, new
                {
                    result = new
                    {
                        points = points.Skip(index).Take(1).ToArray(),
                        next_page_offset = next
                    },
                    status = "ok"
                });
            }

            return Json(HttpStatusCode.NotFound, new { status = "not-found" });
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object value) =>
            new(status)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
            };
    }
}
