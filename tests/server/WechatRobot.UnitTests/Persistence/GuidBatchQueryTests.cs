using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.UnitTests.Persistence;

public sealed class GuidBatchQueryTests
{
    [Fact]
    public void Batches_are_deduplicated_bounded_and_scale_by_batch_count()
    {
        var ids = Enumerable.Range(0, 251).Select(_ => Guid.NewGuid()).Append(Guid.Empty).Append(Guid.Empty).ToArray();

        var batches = GuidBatchQuery.CreateBatches(ids, 100);

        Assert.Equal([100, 100, 52], batches.Select(batch => batch.Count));
        Assert.Equal(3, GuidBatchQuery.RequiredBatchCount(ids, 100));
        Assert.All(batches, batch => Assert.InRange(batch.Count, 1, 100));
    }

    [Fact]
    public void Predicate_uses_parameterized_or_shape_without_collection_contains()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var predicate = GuidBatchQuery.BuildPredicate<Row>([first, second], row => row.Id);

        Assert.True(predicate.Compile()(new Row(first)));
        Assert.True(predicate.Compile()(new Row(second)));
        Assert.False(predicate.Compile()(new Row(Guid.NewGuid())));
        Assert.DoesNotContain("Contains", predicate.ToString(), StringComparison.Ordinal);
        Assert.Contains("OrElse", predicate.Body.NodeType.ToString(), StringComparison.Ordinal);
    }

    private sealed record Row(Guid Id);
}
