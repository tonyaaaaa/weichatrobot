namespace WechatRobot.Application.WorkTool;

public static class WorkToolCommandStatuses
{
    public const string Pending = "pending";
    public const string Queued = "queued";
    public const string Leased = "leased";
    public const string Retrying = "retrying";
    public const string Dispatching = "dispatching";
    public const string DispatchFailed = "dispatchFailed";
    public const string Rejected = "rejected";
    public const string Accepted = "accepted";
    public const string ExecutedSucceeded = "executedSucceeded";
    public const string ExecutedPartially = "executedPartially";
    public const string ExecutedFailed = "executedFailed";
    public const string DeliveryUnknown = "deliveryUnknown";
    public const string ResultTimeout = "resultTimeout";
    public const string Blocked = "blocked";
    public const string DeadLetter = "deadLetter";
    public const string Cancelled = "cancelled";
    public const string DeliveryUnknownResolved = "deliveryUnknownResolved";
}
