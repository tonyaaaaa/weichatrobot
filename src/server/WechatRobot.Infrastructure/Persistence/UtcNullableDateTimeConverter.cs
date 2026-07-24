using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace WechatRobot.Infrastructure.Persistence;

internal sealed class UtcNullableDateTimeConverter : ValueConverter<DateTime?, DateTime?>
{
    public UtcNullableDateTimeConverter()
        : base(
            value => value.HasValue && value.Value.Kind != DateTimeKind.Utc ? value.Value.ToUniversalTime() : value,
            value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : value)
    {
    }
}
