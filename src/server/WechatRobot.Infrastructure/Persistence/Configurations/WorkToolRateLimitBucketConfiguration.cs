using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Persistence.Configurations;

internal sealed class WorkToolRateLimitBucketConfiguration
    : IEntityTypeConfiguration<WorkToolRateLimitBucketEntity>
{
    public void Configure(EntityTypeBuilder<WorkToolRateLimitBucketEntity> builder)
    {
        builder.ToTable("worktool_rate_limit_bucket");
        builder.HasKey(bucket => bucket.ScopeKey);
        builder.Property(bucket => bucket.ScopeKey).HasMaxLength(128);
        builder.Property(bucket => bucket.Version).IsConcurrencyToken();
    }
}
