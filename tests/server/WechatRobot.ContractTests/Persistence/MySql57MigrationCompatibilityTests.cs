using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WechatRobot.Infrastructure.Persistence;

namespace WechatRobot.ContractTests.Persistence;

public sealed class MySql57MigrationCompatibilityTests
{
    [Fact]
    public void Migration_script_does_not_use_json_expression_defaults()
    {
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseMySQL("Server=localhost;Database=wechatrobot;User Id=test;Password=test")
            .Options;
        using var context = new WechatRobotDbContext(options);

        var script = context.GetService<IMigrator>().GenerateScript();

        Assert.DoesNotContain("DEFAULT (JSON_ARRAY())", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DEFAULT (JSON_OBJECT())", script, StringComparison.OrdinalIgnoreCase);
    }
}
