namespace WechatRobot.Infrastructure.WorkTool;

public static class WorkToolHttpTransport
{
    public static HttpMessageHandler CreatePrimaryHandler() =>
        new SocketsHttpHandler();
}
