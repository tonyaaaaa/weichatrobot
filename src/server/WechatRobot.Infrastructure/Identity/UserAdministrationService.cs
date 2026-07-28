using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WechatRobot.Infrastructure.Persistence;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Identity;

public sealed record ManagedUser(
    Guid Id,
    string Email,
    string DisplayName,
    bool IsEnabled,
    IReadOnlyList<string> Roles);

public sealed record ManagedUserPage(
    IReadOnlyList<ManagedUser> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record CreateManagedUser(
    string Email,
    string DisplayName,
    string TemporaryPassword,
    IReadOnlyCollection<string> Roles);

public sealed record SetManagedUserRoles(IReadOnlyCollection<string> Roles);

public sealed class UserAdministrationException(
    string code,
    IReadOnlyCollection<string>? errors = null) : Exception(code)
{
    public string Code { get; } = code;
    public IReadOnlyCollection<string> Errors { get; } = errors ?? [];
}

public sealed class UserAdministrationService(
    UserManager<ApplicationUser> users,
    RoleManager<IdentityRole<Guid>> roles,
    WechatRobotDbContext database,
    TimeProvider timeProvider)
{
    public async Task<ManagedUserPage> ListAsync(
        string? query,
        bool? isEnabled,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = query?.Trim();
        var source = users.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            source = source.Where(user =>
                user.Email!.Contains(normalizedQuery) ||
                user.DisplayName.Contains(normalizedQuery));
        }
        if (isEnabled.HasValue)
            source = source.Where(user => user.IsEnabled == isEnabled.Value);

        var total = await source.CountAsync(cancellationToken);
        var selected = await source
            .OrderBy(user => user.Email)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);

        var items = new List<ManagedUser>(selected.Length);
        foreach (var user in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(ToManagedUser(user, await users.GetRolesAsync(user).WaitAsync(cancellationToken)));
        }
        return new ManagedUserPage(items, total, page, pageSize);
    }

    public async Task<ManagedUser> CreateAsync(
        string actor,
        CreateManagedUser request,
        CancellationToken cancellationToken)
    {
        var requestedRoles = await ValidateRolesAsync(request.Roles, cancellationToken);
        var email = request.Email.Trim();
        var displayName = request.DisplayName.Trim();
        if (email.Length == 0 || displayName.Length == 0 || request.TemporaryPassword.Length == 0)
            throw new UserAdministrationException("required-fields");

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var user = new ApplicationUser
        {
            Email = email,
            UserName = email,
            DisplayName = displayName,
            EmailConfirmed = true,
            IsEnabled = true
        };
        EnsureIdentity(await users.CreateAsync(user, request.TemporaryPassword).WaitAsync(cancellationToken));
        if (requestedRoles.Count > 0)
            EnsureIdentity(await users.AddToRolesAsync(user, requestedRoles).WaitAsync(cancellationToken));

        AddAudit(actor, "user_created", user.Id, new
        {
            user.Email,
            user.DisplayName,
            user.IsEnabled,
            Roles = requestedRoles
        });
        await database.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return ToManagedUser(user, requestedRoles);
    }

    public async Task<ManagedUser> SetEnabledAsync(
        string actor,
        Guid userId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var user = await users.FindByIdAsync(userId.ToString()).WaitAsync(cancellationToken)
            ?? throw new UserAdministrationException("user-not-found");
        var currentRoles = await users.GetRolesAsync(user).WaitAsync(cancellationToken);
        if (user.IsEnabled == isEnabled)
            return ToManagedUser(user, currentRoles);

        if (!isEnabled && currentRoles.Contains(SystemRoles.Admin, StringComparer.Ordinal))
            await EnsureAnotherEnabledAdministratorAsync(user.Id, cancellationToken);

        user.IsEnabled = isEnabled;
        EnsureIdentity(await users.UpdateAsync(user).WaitAsync(cancellationToken));
        EnsureIdentity(await users.UpdateSecurityStampAsync(user).WaitAsync(cancellationToken));
        AddAudit(actor, "user_enabled_changed", user.Id, new { user.Email, IsEnabled = isEnabled });
        await database.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return ToManagedUser(user, currentRoles);
    }

    public async Task<ManagedUser> SetRolesAsync(
        string actor,
        Guid userId,
        SetManagedUserRoles request,
        CancellationToken cancellationToken)
    {
        var requestedRoles = await ValidateRolesAsync(request.Roles, cancellationToken);
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var user = await users.FindByIdAsync(userId.ToString()).WaitAsync(cancellationToken)
            ?? throw new UserAdministrationException("user-not-found");
        var currentRoles = await users.GetRolesAsync(user).WaitAsync(cancellationToken);

        if (user.IsEnabled &&
            currentRoles.Contains(SystemRoles.Admin, StringComparer.Ordinal) &&
            !requestedRoles.Contains(SystemRoles.Admin, StringComparer.Ordinal))
        {
            await EnsureAnotherEnabledAdministratorAsync(user.Id, cancellationToken);
        }

        var removed = currentRoles.Except(requestedRoles, StringComparer.Ordinal).ToArray();
        var added = requestedRoles.Except(currentRoles, StringComparer.Ordinal).ToArray();
        if (removed.Length > 0)
            EnsureIdentity(await users.RemoveFromRolesAsync(user, removed).WaitAsync(cancellationToken));
        if (added.Length > 0)
            EnsureIdentity(await users.AddToRolesAsync(user, added).WaitAsync(cancellationToken));
        if (removed.Length > 0 || added.Length > 0)
        {
            EnsureIdentity(await users.UpdateSecurityStampAsync(user).WaitAsync(cancellationToken));
            AddAudit(actor, "user_roles_changed", user.Id, new
            {
                user.Email,
                AddedRoles = added,
                RemovedRoles = removed,
                Roles = requestedRoles
            });
            await database.SaveChangesAsync(cancellationToken);
        }
        await CommitAsync(transaction, cancellationToken);
        return ToManagedUser(user, requestedRoles);
    }

    private async Task<List<string>> ValidateRolesAsync(
        IReadOnlyCollection<string> requestedRoles,
        CancellationToken cancellationToken)
    {
        var normalized = requestedRoles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (normalized.Any(role => !SystemRoles.Assignable.Contains(role, StringComparer.Ordinal)))
            throw new UserAdministrationException("unknown-role");
        foreach (var role in normalized)
        {
            if (!await roles.RoleExistsAsync(role).WaitAsync(cancellationToken))
                throw new UserAdministrationException("unknown-role");
        }
        return normalized;
    }

    private async Task EnsureAnotherEnabledAdministratorAsync(Guid excludedUserId, CancellationToken cancellationToken)
    {
        var adminRoleId = await database.Roles
            .Where(role => role.NormalizedName == SystemRoles.Admin.ToUpperInvariant())
            .Select(role => role.Id)
            .SingleAsync(cancellationToken);
        var anotherExists = await database.UserRoles
            .Where(link => link.RoleId == adminRoleId && link.UserId != excludedUserId)
            .Join(database.Users.Where(user => user.IsEnabled),
                link => link.UserId,
                user => user.Id,
                (_, _) => 1)
            .AnyAsync(cancellationToken);
        if (!anotherExists)
            throw new UserAdministrationException("last-enabled-admin");
    }

    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (string.Equals(
            database.Database.ProviderName,
            "Microsoft.EntityFrameworkCore.InMemory",
            StringComparison.Ordinal))
            return null;
        return await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    }

    private static Task CommitAsync(IDbContextTransaction? transaction, CancellationToken cancellationToken) =>
        transaction is null ? Task.CompletedTask : transaction.CommitAsync(cancellationToken);

    private static void EnsureIdentity(IdentityResult result)
    {
        if (result.Succeeded)
            return;
        throw new UserAdministrationException(
            "identity-validation",
            result.Errors.Select(error => error.Code).Distinct(StringComparer.Ordinal).ToArray());
    }

    private void AddAudit(string actor, string action, Guid userId, object detail)
    {
        database.AdministrationAudits.Add(new AdministrationAuditEntity
        {
            Actor = actor,
            Action = action,
            TargetType = "ApplicationUser",
            TargetId = userId.ToString("D"),
            SanitizedDetailJson = JsonSerializer.Serialize(detail),
            CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime
        });
    }

    private static ManagedUser ToManagedUser(ApplicationUser user, IEnumerable<string> assignedRoles) =>
        new(
            user.Id,
            user.Email ?? string.Empty,
            user.DisplayName,
            user.IsEnabled,
            assignedRoles.Order(StringComparer.Ordinal).ToArray());
}
