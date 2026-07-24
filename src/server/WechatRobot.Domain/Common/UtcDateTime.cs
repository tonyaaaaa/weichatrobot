namespace WechatRobot.Domain.Common;

public readonly record struct UtcDateTime
{
    public UtcDateTime(DateTime value)
    {
        Value = value.Kind == DateTimeKind.Utc
            ? value
            : value.ToUniversalTime();
    }

    public DateTime Value { get; }

    public static UtcDateTime Now() => new(DateTime.UtcNow);

    public static implicit operator DateTime(UtcDateTime value) => value.Value;
}
