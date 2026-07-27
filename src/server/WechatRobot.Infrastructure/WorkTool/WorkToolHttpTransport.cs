namespace WechatRobot.Infrastructure.WorkTool;

public static class WorkToolHttpTransport
{
    public static HttpMessageHandler CreatePrimaryHandler() =>
        OperatingSystem.IsWindows()
            ? new WinHttpHandler()
            : new SocketsHttpHandler();
}
