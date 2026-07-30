using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WechatRobot.Application.Conversations;

public enum ConversationChannelType
{
    Group,
    Private
}

public sealed record PrivateConversationScope(
    Guid RobotConfigId,
    int RoomType,
    string PeerDisplayName,
    string ScopeHash)
{
    private static readonly Regex Whitespace = new(
        "\\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static PrivateConversationScope Create(
        Guid robotConfigId,
        int roomType,
        string peerDisplayName)
    {
        if (roomType is not (2 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(roomType));
        }
        var display = Whitespace.Replace(peerDisplayName.Trim(), " ");
        if (display.Length is 0 or > 128)
        {
            throw new ArgumentException(
                "Private peer display name is invalid.",
                nameof(peerDisplayName));
        }
        var normalized = display.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"{robotConfigId:D}\n{roomType}\n{normalized}")));
        return new PrivateConversationScope(
            robotConfigId,
            roomType,
            display,
            hash);
    }
}
