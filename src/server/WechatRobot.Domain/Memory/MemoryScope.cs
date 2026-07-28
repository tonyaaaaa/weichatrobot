using System.Globalization;
using System.Text;

namespace WechatRobot.Domain.Memory;

public enum MemoryScopeType
{
    Global,
    Robot,
    Group,
    User
}

public sealed record MemoryScope(
    MemoryScopeType Type,
    Guid? RobotConfigId,
    Guid? GroupProfileId,
    string? SubjectKey,
    string? SubjectDisplayName)
{
    public static MemoryScope Create(
        MemoryScopeType type,
        Guid? robotConfigId,
        Guid? groupProfileId,
        string? subjectKey,
        string? subjectDisplayName)
    {
        var normalizedSubject = NormalizeSubject(subjectKey);

        var valid = type switch
        {
            MemoryScopeType.Global =>
                robotConfigId is null && groupProfileId is null && normalizedSubject is null,
            MemoryScopeType.Robot =>
                robotConfigId is not null && groupProfileId is null && normalizedSubject is null,
            MemoryScopeType.Group =>
                robotConfigId is not null && groupProfileId is not null && normalizedSubject is null,
            MemoryScopeType.User =>
                robotConfigId is not null && groupProfileId is not null && normalizedSubject is not null,
            _ => false
        };

        if (!valid)
        {
            throw new ArgumentException($"Invalid identity parts for {type} memory scope.");
        }

        return new MemoryScope(
            type,
            robotConfigId,
            groupProfileId,
            normalizedSubject,
            string.IsNullOrWhiteSpace(subjectDisplayName)
                ? null
                : subjectDisplayName.Trim().Normalize(NormalizationForm.FormKC));
    }

    public static string? NormalizeSubject(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value
            .Trim()
            .Normalize(NormalizationForm.FormKC)
            .ToLower(CultureInfo.InvariantCulture);
    }
}
