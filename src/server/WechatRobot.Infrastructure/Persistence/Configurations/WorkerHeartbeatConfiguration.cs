using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Persistence.Configurations;

internal sealed class WorkerHeartbeatConfiguration : IEntityTypeConfiguration<WorkerHeartbeatEntity>
{
    public void Configure(EntityTypeBuilder<WorkerHeartbeatEntity> builder)
    {
        builder.ToTable("worker_heartbeat");
        builder.HasKey(value => value.Name);
        builder.Property(value => value.Name).HasMaxLength(64);
    }
}
