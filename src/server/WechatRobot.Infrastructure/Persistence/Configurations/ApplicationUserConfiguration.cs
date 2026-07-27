using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WechatRobot.Infrastructure.Identity;

namespace WechatRobot.Infrastructure.Persistence.Configurations;

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(user => user.DisplayName).HasMaxLength(128).IsRequired();
        builder.Property(user => user.IsEnabled).HasDefaultValue(true);
        builder.Property(user => user.WorkToolDisplayName)
            .HasMaxLength(128)
            .UseCollation("utf8mb4_bin");
        builder.HasIndex(user => user.WorkToolDisplayName).IsUnique();
    }
}
