using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using WechatRobot.Application.WorkTool;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.Infrastructure.WorkTool;

public sealed class MySqlWorkToolGlobalRateLimiter(
    IDbContextFactory<WechatRobotDbContext> databaseFactory,
    IOptions<WorkToolRateLimitOptions> configuredOptions)
    : IWorkToolGlobalRateLimiter
{
    public async Task<WorkToolRateLimitLease> AcquireAsync(
        string operation,
        CancellationToken cancellationToken)
    {
        _ = operation;
        var options = configuredOptions.Value;
        try
        {
            await using var database = await databaseFactory.CreateDbContextAsync(
                cancellationToken);
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT IGNORE INTO worktool_rate_limit_bucket
                     (ScopeKey, NextPermitAtUtc, Version)
                 VALUES ({options.ScopeKey}, UTC_TIMESTAMP(6), 0)
                 """,
                cancellationToken);

            await using var transaction = await database.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            var databaseNow = await ReadDatabaseUtcNowAsync(
                database,
                transaction,
                cancellationToken);
            var bucket = await database.WorkToolRateLimitBuckets
                .FromSqlInterpolated(
                    $"""
                     SELECT ScopeKey, NextPermitAtUtc, Version
                     FROM worktool_rate_limit_bucket
                     WHERE ScopeKey = {options.ScopeKey}
                     FOR UPDATE
                     """)
                .SingleAsync(cancellationToken);

            var permitAt = bucket.NextPermitAtUtc > databaseNow
                ? bucket.NextPermitAtUtc
                : databaseNow;
            var wait = permitAt - databaseNow;
            if (wait > TimeSpan.FromSeconds(options.MaxWaitSeconds))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(false, "worktool_global_rate_limited");
            }

            bucket.NextPermitAtUtc = permitAt.AddSeconds(
                60d / options.RequestsPerMinute);
            bucket.Version++;
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await Task.Delay(wait, cancellationToken);
            return new(true, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(false, "worktool_global_rate_limiter_unavailable");
        }
    }

    private static async Task<DateTime> ReadDatabaseUtcNowAsync(
        WechatRobotDbContext database,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        var connection = database.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "SELECT UTC_TIMESTAMP(6)";
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return DateTime.SpecifyKind(
            Convert.ToDateTime(scalar, CultureInfo.InvariantCulture),
            DateTimeKind.Utc);
    }
}
