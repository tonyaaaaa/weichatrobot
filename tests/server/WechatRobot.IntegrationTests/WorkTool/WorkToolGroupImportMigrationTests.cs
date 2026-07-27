using Microsoft.EntityFrameworkCore;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.IntegrationTests.WorkTool;

public sealed class WorkToolGroupImportMigrationTests
{
    [Fact]
    public void Model_enforces_group_import_and_agent_nickname_invariants()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseMySQL("Server=localhost;Database=model_only;User Id=model_only")
            .Options;
        using var database = new WechatRobotDbContext(options);

        var user = database.Model.FindEntityType(typeof(ApplicationUser));
        Assert.NotNull(user);
        var nickname = user.FindProperty(nameof(ApplicationUser.WorkToolDisplayName));
        Assert.NotNull(nickname);
        Assert.Equal(128, nickname.GetMaxLength());
        Assert.Contains(
            user.GetIndexes(),
            index => index.IsUnique && index.Properties.Count == 1
                && index.Properties[0].Name == nameof(ApplicationUser.WorkToolDisplayName));

        var group = database.Model.FindEntityType(typeof(GroupProfileEntity));
        Assert.NotNull(group);
        Assert.NotNull(group.FindProperty(nameof(GroupProfileEntity.RegistrationSource)));
        Assert.NotNull(group.FindProperty(nameof(GroupProfileEntity.WorkToolImportedAtUtc)));
        Assert.NotNull(group.FindProperty(nameof(GroupProfileEntity.WorkToolLastSeenAtUtc)));

        var agent = database.Model.FindEntityType(typeof(GroupHumanAgentEntity));
        Assert.NotNull(agent);
        Assert.Equal("group_human_agent", agent.GetTableName());
        Assert.Contains(
            agent.GetIndexes(),
            index => index.IsUnique
                && index.Properties.Any(property =>
                    property.Name == "DefaultGroupProfileId"));
    }
}
