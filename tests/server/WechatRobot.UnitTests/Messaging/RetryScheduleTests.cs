using WechatRobot.Application.Messaging;

namespace WechatRobot.UnitTests.Messaging;

public sealed class RetryScheduleTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 15)]
    [InlineData(3, 45)]
    public void Retry_delay_uses_the_documented_schedule(int failedAttempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), SendCommandService.GetRetryDelay(failedAttempt));
    }

    [Fact]
    public void Fourth_failure_is_not_retryable()
    {
        Assert.Null(SendCommandService.GetRetryDelay(4));
    }
}
