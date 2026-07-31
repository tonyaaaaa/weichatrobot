using System.Security.Cryptography;
using System.Text;

namespace WechatRobot.Infrastructure.Persistence;

public static class KnowledgeDocumentCleanupJobIdentity
{
    public static Guid Create(Guid documentId)
    {
        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"CleanupKnowledgeDocument:{documentId:N}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
