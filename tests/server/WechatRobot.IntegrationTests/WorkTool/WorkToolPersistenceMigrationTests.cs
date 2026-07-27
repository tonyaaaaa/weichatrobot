using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.WorkTool;

public sealed class WorkToolPersistenceMigrationTests(MySqlFixture fixture) : IClassFixture<MySqlFixture>
{
    [Fact]
    public void Model_exposes_callback_group_identity_and_command_receipt_fields()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseMySQL("Server=localhost;Database=model_only;User Id=model_only")
            .Options;
        using var database = new WechatRobotDbContext(options);
        var model = database.Model;

        AssertProperty<RobotConfigEntity>(model, "EncryptedCallbackSecret", true, 1024);
        AssertProperty<RobotConfigEntity>(model, "PreviousCallbackSecretHash", true, 128);
        AssertProperty<GroupProfileEntity>(model, "WorkToolGroupRemark", true, 256);
        AssertProperty<ConversationMessageEntity>(model, "GroupRemark", true, 256);
        AssertReceipt<SendCommandEntity>(model);
        AssertReceipt<WorkToolOperationAuditEntity>(model);

        var group = model.FindEntityType(typeof(GroupProfileEntity))!;
        Assert.DoesNotContain(
            group.GetIndexes(),
            index => index.IsUnique &&
                     index.Properties.Select(property => property.Name)
                         .SequenceEqual(["RobotConfigId", "ExternalGroupId"]));
        Assert.Contains(
            group.GetIndexes(),
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual(["RobotConfigId", "Name", "WorkToolGroupRemark"]));
    }

    [Fact]
    public async Task Migration_preserves_legacy_rows_and_maps_acceptance_without_claiming_execution()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseMySQL(fixture.ConnectionString)
            .Options;
        await using var database = new WechatRobotDbContext(options);
        await database.Database.MigrateAsync(
            "20260724014009_HardenModelConfigurationManagement",
            TestContext.Current.CancellationToken);

        var robotId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var sendId = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO robot_config
                (Id, Name, WorkToolRobotId, CallbackSecretHash, IsEnabled,
                 SendRateLimitPerMinute, SendRateTokens, SendRateUpdatedAtUtc,
                 SendCoordinationVersion, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                ({robotId}, {"p0-migration-robot"}, {Guid.NewGuid().ToString("N")},
                 {"fake-hash"}, {true}, {50}, {50m}, {now}, {0}, {now}, {now});

            INSERT INTO group_profile
                (Id, RobotConfigId, ExternalGroupId, Name, IsEnabled,
                 HandoffPausePolicy, ConfigurationVersion, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                ({groupId}, {robotId}, {"legacy-unverified-id"}, {"legacy-group"},
                 {true}, {"Group"}, {0}, {now}, {now});

            INSERT INTO send_command
                (Id, RobotConfigId, GroupProfileId, IdempotencyKey, PayloadJson,
                 Status, AttemptCount, NextAttemptAtUtc, CompletedAtUtc, Version,
                 CreatedAtUtc, SentAtUtc)
            VALUES
                ({sendId}, {robotId}, {groupId}, {Guid.NewGuid().ToString("N")},
                 {"{}"}, {"completed"}, {1}, {now}, {now}, {0}, {now}, {now});

            INSERT INTO worktool_operation_audit
                (Id, OperatorName, Operation, WorkToolCommandNumber,
                 SanitizedRequestJson, Status, RobotConfigId, AttemptCount,
                 CompletedAtUtc, Version, CreatedAtUtc)
            VALUES
                ({auditId}, {"migration"}, {"Rename"}, {207}, {"{}"},
                 {"Succeeded"}, {robotId}, {1}, {now}, {0}, {now});
            """, TestContext.Current.CancellationToken);

        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        database.ChangeTracker.Clear();

        var group = await database.GroupProfiles.AsNoTracking().SingleAsync(
            item => item.Id == groupId,
            TestContext.Current.CancellationToken);
        var send = await database.SendCommands.AsNoTracking().SingleAsync(
            item => item.Id == sendId,
            TestContext.Current.CancellationToken);
        var audit = await database.WorkToolOperationAudits.AsNoTracking().SingleAsync(
            item => item.Id == auditId,
            TestContext.Current.CancellationToken);

        Assert.Equal("legacy-unverified-id", group.ExternalGroupId);
        Assert.Null(ReadProperty<string>(group, "WorkToolGroupRemark"));
        Assert.Equal("accepted", send.Status);
        Assert.Equal(send.SentAtUtc, ReadProperty<DateTime?>(send, "AcceptedAtUtc"));
        Assert.Equal("accepted", audit.Status);
        Assert.Null(ReadProperty<DateTime?>(audit, "AcceptedAtUtc"));
    }

    private static void AssertReceipt<TEntity>(IModel model)
    {
        AssertProperty<TEntity>(model, "WorkToolCommandMessageId", true, 128);
        AssertProperty<TEntity>(model, "AcceptedAtUtc", true, null);
        AssertProperty<TEntity>(model, "WorkToolResultCode", true, null);
        AssertProperty<TEntity>(model, "WorkToolResultAtUtc", true, null);
        AssertProperty<TEntity>(model, "WorkToolSuccessListJson", true, null);
        AssertProperty<TEntity>(model, "WorkToolFailListJson", true, null);

        var entity = model.FindEntityType(typeof(TEntity))!;
        Assert.Contains(
            entity.GetIndexes(),
            index => index.IsUnique &&
                     index.Properties.Select(property => property.Name)
                         .SequenceEqual(["WorkToolCommandMessageId"]));
    }

    private static void AssertProperty<TEntity>(
        IModel model,
        string propertyName,
        bool nullable,
        int? maxLength)
    {
        var entity = model.FindEntityType(typeof(TEntity));
        Assert.NotNull(entity);
        var property = entity.FindProperty(propertyName);
        Assert.NotNull(property);
        Assert.Equal(nullable, property.IsNullable);
        Assert.Equal(maxLength, property.GetMaxLength());
    }

    private static T? ReadProperty<T>(object entity, string propertyName) =>
        (T?)entity.GetType().GetProperty(propertyName)!.GetValue(entity);
}
