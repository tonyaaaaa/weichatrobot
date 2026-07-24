using System.Linq.Expressions;

namespace WechatRobot.Infrastructure.Persistence;

public static class GuidBatchQuery
{
    public const int MaximumBatchSize = 100;

    public static IReadOnlyList<IReadOnlyList<Guid>> CreateBatches(IEnumerable<Guid> ids, int batchSize = MaximumBatchSize)
    {
        if (batchSize is < 1 or > MaximumBatchSize) throw new ArgumentOutOfRangeException(nameof(batchSize));
        return ids.Distinct().Chunk(batchSize).Select(batch => (IReadOnlyList<Guid>)batch).ToArray();
    }

    public static int RequiredBatchCount(IEnumerable<Guid> ids, int batchSize = MaximumBatchSize) =>
        CreateBatches(ids, batchSize).Count;

    public static Expression<Func<TEntity, bool>> BuildPredicate<TEntity>(
        IReadOnlyCollection<Guid> ids,
        Expression<Func<TEntity, Guid>> selector)
    {
        if (ids.Count > MaximumBatchSize) throw new ArgumentOutOfRangeException(nameof(ids));
        var parameter = selector.Parameters[0];
        Expression body = Expression.Constant(false);
        foreach (var id in ids.Distinct())
            body = Expression.OrElse(body, Expression.Equal(selector.Body, Expression.Constant(id)));
        return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
    }
}
