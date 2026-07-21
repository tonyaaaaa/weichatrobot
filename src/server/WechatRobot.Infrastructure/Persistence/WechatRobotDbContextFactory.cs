using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WechatRobot.Infrastructure.Persistence;

public sealed class WechatRobotDbContextFactory : IDesignTimeDbContextFactory<WechatRobotDbContext>
{
    public WechatRobotDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__WechatRobot")
            ?? "Server=localhost;Port=3306;Database=wechatrobot;User Id=wechatrobot";
        var options = new DbContextOptionsBuilder<WechatRobotDbContext>()
            .UseMySQL(connectionString)
            .Options;

        return new WechatRobotDbContext(options);
    }
}
