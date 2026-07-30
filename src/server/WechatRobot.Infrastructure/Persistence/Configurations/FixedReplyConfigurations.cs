using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WechatRobot.Infrastructure.Identity;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Persistence.Configurations;

internal sealed class FixedReplyTemplateConfiguration
    : IEntityTypeConfiguration<FixedReplyTemplateEntity>
{
    public void Configure(EntityTypeBuilder<FixedReplyTemplateEntity> builder)
    {
        builder.ToTable("fixed_reply_template");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Name).HasMaxLength(128).IsRequired();
        builder.Property(item => item.NormalizedName).HasMaxLength(128).IsRequired();
        builder.Property(item => item.IntentDescription).HasMaxLength(1000).IsRequired();
        builder.Property(item => item.ReplyText).HasColumnType("text").IsRequired();
        builder.Property(item => item.ScopeType).HasMaxLength(32).IsRequired();
        builder.Property(item => item.Version).IsConcurrencyToken();
        builder.HasIndex(item => item.NormalizedName).IsUnique();
        builder.HasIndex(item => new { item.IsEnabled, item.DeletedAtUtc, item.Priority });
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.UpdatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
internal sealed class FixedReplyTemplateExampleConfiguration
    : IEntityTypeConfiguration<FixedReplyTemplateExampleEntity>
{
    public void Configure(EntityTypeBuilder<FixedReplyTemplateExampleEntity> builder)
    {
        builder.ToTable("fixed_reply_template_example");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.ExampleText).HasMaxLength(500).IsRequired();
        builder.Property(item => item.NormalizedText).HasMaxLength(500).IsRequired();
        builder.HasIndex(item => new { item.TemplateId, item.NormalizedText }).IsUnique();
        builder.HasOne<FixedReplyTemplateEntity>().WithMany()
            .HasForeignKey(item => item.TemplateId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class FixedReplyTemplateGroupRuleConfiguration
    : IEntityTypeConfiguration<FixedReplyTemplateGroupRuleEntity>
{
    public void Configure(EntityTypeBuilder<FixedReplyTemplateGroupRuleEntity> builder)
    {
        builder.ToTable("fixed_reply_template_group_rule");
        builder.HasKey(item => new { item.TemplateId, item.GroupProfileId });
        builder.Property(item => item.Effect).HasMaxLength(16).IsRequired();
        builder.HasOne<FixedReplyTemplateEntity>().WithMany()
            .HasForeignKey(item => item.TemplateId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<GroupProfileEntity>().WithMany()
            .HasForeignKey(item => item.GroupProfileId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany()
            .HasForeignKey(item => item.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(item => new { item.GroupProfileId, item.Effect });
    }
}
