using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace WechatRobot.Infrastructure.Persistence;

public sealed class MySqlRobotSendLock : IAsyncDisposable
{
    private readonly WechatRobotDbContext? _database;
    private readonly DbConnection? _connection;
    private readonly string? _name;
    private readonly bool _closeConnection;
    private bool _released;

    private MySqlRobotSendLock(WechatRobotDbContext? database = null, DbConnection? connection = null, string? name = null,
        bool closeConnection = false)
    {
        _database = database;
        _connection = connection;
        _name = name;
        _closeConnection = closeConnection;
    }

    public static async Task<MySqlRobotSendLock> AcquireAsync(WechatRobotDbContext database, Guid robotId,
        CancellationToken cancellationToken)
    {
        if (!database.Database.IsRelational()) return new MySqlRobotSendLock();

        var connection = database.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection) await database.Database.OpenConnectionAsync(cancellationToken);
        var name = NameFor(robotId);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT GET_LOCK(@name, 30)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@name";
            parameter.Value = name;
            command.Parameters.Add(parameter);
            var acquired = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
            if (acquired != 1) throw new TimeoutException("Timed out waiting for the robot send gate.");
            return new MySqlRobotSendLock(database, connection, name, closeConnection);
        }
        catch
        {
            if (closeConnection) await database.Database.CloseConnectionAsync();
            throw;
        }
    }

    public static string NameFor(Guid robotId) => $"wechatrobot:send:{robotId:N}";

    public async ValueTask DisposeAsync()
    {
        if (_released || _connection is null || _name is null) return;
        _released = true;
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT RELEASE_LOCK(@name)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@name";
            parameter.Value = _name;
            command.Parameters.Add(parameter);
            _ = await command.ExecuteScalarAsync(CancellationToken.None);
        }
        finally
        {
            if (_closeConnection && _database is not null) await _database.Database.CloseConnectionAsync();
        }
    }
}
