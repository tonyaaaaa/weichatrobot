using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.IntegrationTests.Infrastructure;

namespace WechatRobot.IntegrationTests.Models;

public sealed class ModelConfigurationReferenceMigrationTests(MySqlFixture fixture) : IClassFixture<MySqlFixture>
{
    [Fact]
    public async Task Retrieval_audit_model_reference_is_backfilled_from_legacy_input_summary()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseMySQL(fixture.ConnectionString)
            .Options;
        await using var database = new WechatRobotDbContext(options);
        await database.Database.MigrateAsync(
            "20260723103629_AddAdministrationSurfaces",
            TestContext.Current.CancellationToken);

        var robotId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var summary = $$"""{"ModelConfigurationId":"{{modelId}}"}""";

        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO robot_config
                (Id, Name, WorkToolRobotId, CallbackSecretHash, IsEnabled,
                 SendRateLimitPerMinute, SendRateTokens, SendRateUpdatedAtUtc,
                 SendCoordinationVersion, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                ({robotId}, {"migration-robot"}, {Guid.NewGuid().ToString("N")},
                 {"hash"}, {true}, {50}, {50m}, {now}, {0}, {now}, {now});

            INSERT INTO group_profile
                (Id, RobotConfigId, ExternalGroupId, Name, IsEnabled,
                 HandoffPausePolicy, ConfigurationVersion, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                ({groupId}, {robotId}, {Guid.NewGuid().ToString("N")},
                 {"migration-group"}, {true}, {"Group"}, {0}, {now}, {now});

            INSERT INTO conversation_message
                (Id, RobotConfigId, GroupProfileId, ProcessingState, Direction, Role,
                 FallbackHash, FallbackWindowStartUtc, GroupName, SenderDisplayName,
                 Text, ReceivedAtUtc, CreatedAtUtc)
            VALUES
                ({messageId}, {robotId}, {groupId}, {"completed"}, {"inbound"},
                 {"user"}, {Guid.NewGuid().ToString("N")}, {now}, {"migration-group"},
                 {"member"}, {"question"}, {now}, {now});

            INSERT INTO model_config
                (Id, Name, Provider, ConfigurationType, BaseUrl, Model,
                 TimeoutSeconds, MaxRetries, IsEnabled, IsDefault,
                 CreatedAtUtc, UpdatedAtUtc)
            VALUES
                ({modelId}, {"migration-model"}, {"fake"}, {"chat"},
                 {"https://fake.test"}, {"fake"}, {30}, {0}, {true}, {false},
                 {now}, {now});

            INSERT INTO retrieval_audit
                (Id, ConversationMessageId, GroupProfileId, Decision,
                 ConfidenceThreshold, ContextPolicy, EvidenceJson,
                 InputSummaryJson, CreatedAtUtc)
            VALUES
                ({auditId}, {messageId}, {groupId}, {"Answer"}, {0.7},
                 {"group"}, {"[]"}, {summary}, {now});
            """, TestContext.Current.CancellationToken);

        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);

        var migrated = await database.RetrievalAudits
            .AsNoTracking()
            .SingleAsync(item => item.Id == auditId, TestContext.Current.CancellationToken);
        Assert.Equal(modelId, migrated.ModelConfigurationId);
    }
}
