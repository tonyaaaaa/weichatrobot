using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using WechatRobot.Application.Handoffs;
using WechatRobot.Infrastructure.Persistence.Entities;
using WechatRobot.Domain.Handoffs;

namespace WechatRobot.Infrastructure.Persistence;

public sealed class EfHandoffStore(WechatRobotDbContext db) : IHandoffStore
{
    public async Task<HandoffRecord> StartManualAsync(ManualStartHandoffCommand command, DateTime nowUtc, CancellationToken token)
    {
        var message = await db.ConversationMessages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == command.QuestionMessageId && x.Direction == "inbound", token)
            ?? throw new KeyNotFoundException("Source inbound message was not found.");
        var group = message.GroupProfileId is { } groupId
            ? await db.GroupProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == groupId && x.IsEnabled, token)
            : await db.GroupProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.RobotConfigId == message.RobotConfigId && x.IsEnabled &&
                (x.Name == message.GroupName || x.ExternalGroupId == message.GroupName), token);
        if (group is null) throw new HandoffStateException("Source message has no enabled group mapping.");
        var robot = await db.RobotConfigs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == group.RobotConfigId, token)
            ?? throw new HandoffStateException("Source message has no robot mapping.");
        string target = "人工客服";
        if (command.AssigneeUserId is { } assignee)
        {
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == assignee, token);
            if (user is null || string.IsNullOrWhiteSpace(user.UserName)) throw new HandoffStateException("Assignee has no validated mention profile.");
            target = user.UserName;
        }
        var stable = command.PauseScope == HandoffPauseScope.Sender ? message.StableSenderId : null;
        var reason = Normalize(command.Reason);
        return await StartAsync(new(message.Id, robot.Id, group.Id, robot.WorkToolRobotId, group.Name, "manual_transfer",
            JsonSerializer.Serialize(new { command.AuthenticatedActorUserId, Reason = reason }), command.PauseScope, stable,
            command.AssigneeUserId, target, command.IdempotencyKey, reason, command.AuthenticatedActorUserId), nowUtc, token);
    }
    public async Task<HandoffRecord> StartAsync(StartHandoffCommand command, DateTime nowUtc, CancellationToken token)
    {
        if (command.PauseScope == HandoffPauseScope.Sender && string.IsNullOrWhiteSpace(command.StableSenderId)) throw new ArgumentException("Stable sender id is required.");
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 96) throw new ArgumentException("Handoff idempotency key is required and must not exceed 96 characters.");
        await using var sendGate = await MySqlRobotSendCoordinator.AcquireAsync(db, command.RobotConfigId, token);
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        if (transaction is not null)
            _ = await db.GroupProfiles.FromSqlInterpolated($"SELECT * FROM group_profile WHERE Id = {command.GroupProfileId} FOR UPDATE")
                .AsNoTracking().SingleAsync(token);
        var fingerprint = RequestFingerprint(command);
        var matches = await db.HandoffCases.AsNoTracking().Where(x => x.QuestionMessageId == command.QuestionMessageId ||
            x.StartIdempotencyKey == command.IdempotencyKey).Take(2).ToArrayAsync(token);
        if (matches.Length > 0)
        {
            if (matches.Length != 1 || matches[0].RequestFingerprint is null ||
                !string.Equals(matches[0].RequestFingerprint, fingerprint, StringComparison.Ordinal))
                throw new HandoffStateException("Handoff idempotency key or source question was already used with a different payload.");
            if (transaction is not null) await transaction.CommitAsync(token);
            return Map(matches[0]);
        }
        var domain = HandoffCase.Start(Guid.NewGuid(), command.GroupProfileId, command.QuestionMessageId, command.ReasonCode, command.EvidenceJson,
            Enum.Parse<PauseScope>(command.PauseScope.ToString()), command.StableSenderId, nowUtc);
        var entity = new HandoffCaseEntity { QuestionMessageId = command.QuestionMessageId, RobotConfigId = command.RobotConfigId,
            Id = domain.Id,
            GroupProfileId = command.GroupProfileId, ReasonCode = command.ReasonCode, EvidenceJson = command.EvidenceJson,
            PauseScope = command.PauseScope.ToString(), StableSenderId = command.StableSenderId, State = domain.State.ToString(),
            StartIdempotencyKey = command.IdempotencyKey, RequestFingerprint = fingerprint, CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc };
        db.HandoffCases.Add(entity);
        AddTransition(entity.Id, command.AuthenticatedActorUserId, 1, "AIActive", "WaitingHuman", command.ReasonCode, $"handoff:{entity.Id:D}:create", nowUtc);
        if (command.AssigneeUserId is { } initialAssignee)
        {
            var from = domain.State.ToString(); domain.Assign(initialAssignee, nowUtc); Apply(entity, domain); entity.Version++;
            AddTransition(entity.Id, command.AuthenticatedActorUserId, 2, from, domain.State.ToString(), "initial_assignment", $"handoff:{entity.Id:D}:initial-assign", nowUtc);
        }
        var target = Safe(command.AssigneeTarget, 64);
        var reason = Safe(command.ReasonCode, 96);
        var sendStatus = await MySqlRobotSendCoordinator.InitialStatusAsync(db, command.RobotConfigId, token);
        db.SendCommands.Add(new SendCommandEntity { RobotConfigId = command.RobotConfigId, GroupProfileId = command.GroupProfileId,
            IdempotencyKey = command.IdempotencyKey, PayloadJson = JsonSerializer.Serialize(new { command.GroupName,
                Text = $"已转人工。原因：{reason}；关联：{entity.Id:N}", AtList = new[] { target } }), Status = sendStatus,
            NextAttemptAtUtc = nowUtc, CreatedAtUtc = nowUtc });
        try
        {
            await db.SaveChangesAsync(token);
            if (transaction is not null) await transaction.CommitAsync(token);
            return Map(entity);
        }
        catch (DbUpdateException ex) when (ex.InnerException is MySqlException { Number: 1062 })
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            var committed = await db.HandoffCases.AsNoTracking().Where(x => x.QuestionMessageId == command.QuestionMessageId ||
                x.StartIdempotencyKey == command.IdempotencyKey).Take(2).ToArrayAsync(token);
            if (committed.Length == 1 && string.Equals(committed[0].RequestFingerprint, fingerprint, StringComparison.Ordinal))
                return Map(committed[0]);
            throw new HandoffStateException("Handoff idempotency key or source question was already used with a different payload.");
        }
    }

    public async Task RecordUnverifiedWorkToolMessageAsync(Guid handoffId, string externalMessageId, string displayName, string text, DateTime nowUtc, CancellationToken token)
    {
        if (!await db.HandoffCases.AnyAsync(x => x.Id == handoffId && (x.State == "WaitingHuman" || x.State == "HumanHandling"), token))
            throw new HandoffStateException("The handoff is not accepting human messages.");
        if (await db.HandoffMessages.AnyAsync(x => x.ExternalMessageId == externalMessageId, token)) return;
        db.HandoffMessages.Add(new HandoffMessageEntity { HandoffCaseId = handoffId, ExternalMessageId = externalMessageId,
            SenderDisplayName = displayName, AuthenticationKind = "worktool_display_name_unverified", Text = text, CreatedAtUtc = nowUtc });
        await db.SaveChangesAsync(token);
    }

    public async Task<KnowledgeCandidateRecord> ResolveAsync(Guid handoffId, Guid actor, string finalAnswer, int expectedVersion, DateTime nowUtc, CancellationToken token)
    {
        var handoff = await db.HandoffCases.SingleOrDefaultAsync(x => x.Id == handoffId, token) ?? throw new KeyNotFoundException();
        var existing = await db.KnowledgeCandidates.AsNoTracking().SingleOrDefaultAsync(x => x.HandoffCaseId == handoffId, token);
        if (handoff.State == "Resolved" && existing is not null)
        {
            if (!string.Equals(existing.Answer, finalAnswer, StringComparison.Ordinal))
                throw new HandoffStateException("Resolve idempotency key was already used with a different final answer.");
            return Map(existing);
        }
        if (handoff.State != "HumanHandling") throw new HandoffStateException("Only a handled case can be resolved.");
        if (handoff.Version != expectedVersion) throw new HandoffConcurrencyException("The handoff was modified by another operator.");
        var question = await db.ConversationMessages.AsNoTracking().SingleAsync(x => x.Id == handoff.QuestionMessageId, token);
        var domain = Domain(handoff); var from = domain.State.ToString(); domain.Resolve(finalAnswer, nowUtc); Apply(handoff, domain);
        handoff.ResolvedByUserId = actor; handoff.Version++;
        AddTransition(handoff.Id, actor, expectedVersion + 2, from, domain.State.ToString(), "authenticated_resolution", $"handoff:{handoff.Id:D}:resolve:{expectedVersion}", nowUtc);
        db.HandoffMessages.Add(new HandoffMessageEntity { HandoffCaseId = handoffId, SenderDisplayName = actor.ToString("D"), AuthenticatedUserId = actor,
            AuthenticationKind = "authenticated_api", Text = finalAnswer, CreatedAtUtc = nowUtc });
        var candidate = new KnowledgeCandidateEntity { HandoffCaseId = handoffId, QuestionMessageId = question.Id, Question = question.Text,
            Answer = finalAnswer, EvidenceJson = handoff.EvidenceJson, CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc };
        db.KnowledgeCandidates.Add(candidate);
        try { await db.SaveChangesAsync(token); return Map(candidate); }
        catch (DbUpdateConcurrencyException) { throw new HandoffConcurrencyException("The handoff was modified by another operator."); }
        catch (DbUpdateException exception) when (exception.InnerException is MySqlException { Number: 1062 })
        {
            db.ChangeTracker.Clear();
            var duplicate = await db.KnowledgeCandidates.AsNoTracking().SingleOrDefaultAsync(x => x.HandoffCaseId == handoffId, token);
            if (duplicate is not null) return Map(duplicate);
            throw new HandoffConcurrencyException("The handoff was modified by another operator.");
        }
    }

    public async Task<HandoffRecord> AssignAsync(Guid handoffId, Guid actor, Guid assignee, int expectedVersion, DateTime nowUtc, CancellationToken token)
    {
        var entity = await db.HandoffCases.SingleOrDefaultAsync(x => x.Id == handoffId, token) ?? throw new KeyNotFoundException();
        if (entity.State == "HumanHandling" && entity.AssigneeUserId == assignee) return Map(entity);
        return await TransitionAsync(entity, actor, expectedVersion, nowUtc, "assignment", $"handoff:{handoffId:D}:assign:{expectedVersion}:{assignee:D}",
            domain => domain.Assign(assignee, nowUtc), token);
    }

    public async Task<HandoffRecord> RestoreAiAsync(Guid handoffId, Guid actor, int expectedVersion, DateTime nowUtc, CancellationToken token)
    {
        var entity = await db.HandoffCases.SingleOrDefaultAsync(x => x.Id == handoffId, token) ?? throw new KeyNotFoundException();
        if (entity.State == "AIActive") return Map(entity);
        return await TransitionAsync(entity, actor, expectedVersion, nowUtc, "manual_restore", $"handoff:{handoffId:D}:restore:{expectedVersion}",
            domain => domain.RestoreAi(nowUtc), token);
    }

    public Task<bool> IsPausedAsync(Guid groupProfileId, string? stableSenderId, CancellationToken token) => db.HandoffCases.AsNoTracking().AnyAsync(x =>
        x.GroupProfileId == groupProfileId && (x.State == "WaitingHuman" || x.State == "HumanHandling") &&
        (x.PauseScope == "Group" || x.PauseScope == "Sender" && stableSenderId != null && x.StableSenderId == stableSenderId), token);

    public Task<int> CountRecentSystemFailuresAsync(Guid groupProfileId, int maximum, CancellationToken token) => db.RetrievalAudits.AsNoTracking()
        .Where(x => x.GroupProfileId == groupProfileId).OrderByDescending(x => x.CreatedAtUtc).Take(maximum)
        .CountAsync(x => x.Decision == "SystemFailure", token);

    public async Task CapturePausedMessageAsync(Guid groupProfileId, string? stableSenderId, Guid conversationMessageId, string displayName, string text,
        DateTime nowUtc, CancellationToken token)
    {
        var handoffId = await db.HandoffCases.AsNoTracking().Where(x => x.GroupProfileId == groupProfileId &&
                (x.State == "WaitingHuman" || x.State == "HumanHandling") && (x.PauseScope == "Group" ||
                x.PauseScope == "Sender" && stableSenderId != null && x.StableSenderId == stableSenderId))
            .OrderByDescending(x => x.CreatedAtUtc).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(token);
        if (handoffId is null) return;
        var externalId = $"conversation:{conversationMessageId:D}";
        if (await db.HandoffMessages.AnyAsync(x => x.ExternalMessageId == externalId, token)) return;
        db.HandoffMessages.Add(new HandoffMessageEntity { HandoffCaseId = handoffId.Value, ExternalMessageId = externalId,
            SenderDisplayName = displayName, AuthenticationKind = "worktool_display_name_unverified", Text = text, CreatedAtUtc = nowUtc });
        await db.SaveChangesAsync(token);
    }

    private async Task<HandoffRecord> TransitionAsync(HandoffCaseEntity entity, Guid actor, int version, DateTime now, string reason,
        string idempotencyKey, Func<HandoffCase, bool> transition, CancellationToken token)
    {
        if (entity.Version != version) throw new HandoffConcurrencyException("The handoff was modified by another operator.");
        var domain = Domain(entity); var from = domain.State.ToString();
        try { if (!transition(domain)) return Map(entity); }
        catch (InvalidHandoffTransitionException exception) { throw new HandoffStateException(exception.Message); }
        if (db.Database.IsRelational())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var changed = await db.HandoffCases.Where(x => x.Id == entity.Id && x.Version == version)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, domain.State.ToString())
                    .SetProperty(x => x.AssigneeUserId, domain.AssigneeUserId).SetProperty(x => x.FinalAnswer, domain.FinalAnswer)
                    .SetProperty(x => x.Version, version + 1).SetProperty(x => x.UpdatedAtUtc, now), token);
            if (changed != 1) { await transaction.RollbackAsync(token); throw new HandoffConcurrencyException("The handoff was modified by another operator."); }
            AddTransition(entity.Id, actor, version + 2, from, domain.State.ToString(), reason, idempotencyKey, now);
            try { await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return new(entity.Id, domain.State.ToString(), domain.AssigneeUserId, version + 1); }
            catch (DbUpdateException exception) when (exception.InnerException is MySqlException { Number: 1062 })
            { await transaction.RollbackAsync(CancellationToken.None); throw new HandoffConcurrencyException("The handoff transition was already committed by another operator."); }
        }
        Apply(entity, domain); entity.Version++; AddTransition(entity.Id, actor, version + 2, from, domain.State.ToString(), reason, idempotencyKey, now);
        try { await db.SaveChangesAsync(token); return Map(entity); }
        catch (DbUpdateConcurrencyException) { throw new HandoffConcurrencyException("The handoff was modified by another operator."); }
        catch (DbUpdateException exception) when (exception.InnerException is MySqlException { Number: 1062 })
        { throw new HandoffConcurrencyException("The handoff transition was already committed by another operator."); }
    }

    private static string Safe(string value, int length) => new(value.Where(c => !char.IsControl(c)).Take(length).ToArray());
    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string RequestFingerprint(StartHandoffCommand command) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { command.QuestionMessageId, command.RobotConfigId, command.GroupProfileId,
            Reason = Normalize(command.RequestReason ?? command.ReasonCode), PauseScope = command.PauseScope.ToString(),
            StableSenderId = command.StableSenderId?.Trim(), command.AssigneeUserId, command.AuthenticatedActorUserId }))));
    private void AddTransition(Guid handoffId, Guid? actor, int sequence, string from, string to, string reason, string key, DateTime now) =>
        db.HandoffTransitions.Add(new() { HandoffCaseId = handoffId, ActorUserId = actor, Sequence = sequence, FromState = from, ToState = to,
            ReasonCode = reason, IdempotencyKey = key, CreatedAtUtc = now });
    private static HandoffCase Domain(HandoffCaseEntity x) => HandoffCase.Restore(x.Id, x.GroupProfileId, x.QuestionMessageId, x.ReasonCode,
        x.EvidenceJson, Enum.Parse<PauseScope>(x.PauseScope), x.StableSenderId, Enum.Parse<HandoffState>(x.State), x.AssigneeUserId,
        x.FinalAnswer, x.CreatedAtUtc, x.UpdatedAtUtc);
    private static void Apply(HandoffCaseEntity entity, HandoffCase domain)
    { entity.State = domain.State.ToString(); entity.AssigneeUserId = domain.AssigneeUserId; entity.FinalAnswer = domain.FinalAnswer; entity.UpdatedAtUtc = domain.UpdatedAtUtc; }
    private static HandoffRecord Map(HandoffCaseEntity x) => new(x.Id, x.State, x.AssigneeUserId, x.Version);
    private static KnowledgeCandidateRecord Map(KnowledgeCandidateEntity x) => new(
        x.Id,
        x.HandoffCaseId ?? Guid.Empty,
        x.Question,
        x.Answer,
        x.Status,
        x.Version);
}
