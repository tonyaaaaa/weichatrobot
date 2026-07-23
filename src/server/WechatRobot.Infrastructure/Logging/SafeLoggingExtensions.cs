using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WechatRobot.Infrastructure.Logging;

public static class SafeLoggingExtensions
{
    public static ILoggingBuilder AddRedactingConsole(this ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.Services.AddSingleton<ILoggerProvider, RedactingConsoleLoggerProvider>();
        return logging;
    }
}
