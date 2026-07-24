namespace WechatRobot.Infrastructure.Identity;

public static class SystemRoles
{
    public const string Admin = "Admin";
    public const string KnowledgeOperator = "KnowledgeOperator";
    public const string HumanAgent = "HumanAgent";

    public static readonly IReadOnlyList<string> All = [Admin, KnowledgeOperator, HumanAgent];
}
