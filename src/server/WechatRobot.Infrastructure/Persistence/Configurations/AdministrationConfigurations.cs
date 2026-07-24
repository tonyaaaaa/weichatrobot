using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Persistence.Configurations;

internal sealed class SystemSettingConfiguration : IEntityTypeConfiguration<SystemSettingEntity>
{
    public void Configure(EntityTypeBuilder<SystemSettingEntity> builder)
    {
        builder.ToTable("system_setting");
        builder.HasKey(item => item.Key);
        builder.Property(item => item.Key).HasMaxLength(128);
        builder.Property(item => item.ValueJson).HasColumnType("json").IsRequired();
        builder.Property(item => item.Version).IsConcurrencyToken();
    }
}

internal sealed class AdministrationAuditConfiguration : IEntityTypeConfiguration<AdministrationAuditEntity>
{
    public void Configure(EntityTypeBuilder<AdministrationAuditEntity> builder)
    {
        builder.ToTable("administration_audit");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Actor).HasMaxLength(256).IsRequired();
        builder.Property(item => item.Action).HasMaxLength(64).IsRequired();
        builder.Property(item => item.TargetType).HasMaxLength(64).IsRequired();
        builder.Property(item => item.TargetId).HasMaxLength(128).IsRequired();
        builder.Property(item => item.SanitizedDetailJson).HasColumnType("json").IsRequired();
        builder.HasIndex(item => item.CreatedAtUtc);
    }
}
