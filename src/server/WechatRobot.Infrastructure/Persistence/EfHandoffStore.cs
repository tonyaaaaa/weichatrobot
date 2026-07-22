using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using WechatRobot.Application.Handoffs;
using WechatRobot.Infrastructure.Persistence.Entities;

namespace WechatRobot.Infrastructure.Persistence;

public sealed class EfHandoffStore(WechatRobotDbContext db) : IHandoffStore
{
    public async Task<HandoffRecord> StartAsync(StartHandoffCommand command, DateTime nowUtc, CancellationToken token)
    {
        var existing = await db.HandoffCases.AsNoTracking().SingleOrDefaultAsync(x => x.QuestionMessageId == command.QuestionMessageId, token);
        if (existing is not null) return Map(existing);
        if (command.PauseScope == HandoffPauseScope.Sender && string.IsNullOrWhiteSpace(command.StableSenderId)) throw new ArgumentException("Stable sender id is required.");
        var entity = new HandoffCaseEntity { QuestionMessageId = command.QuestionMessageId, RobotConfigId = command.RobotConfigId,
            GroupProfileId = command.GroupProfileId, ReasonCode = command.ReasonCode, EvidenceJson = command.EvidenceJson,
            PauseScope = command.PauseScope.ToString(), StableSenderId = command.StableSenderId, AssigneeUserId = command.AssigneeUserId,
            State = command.AssigneeUserId is null ? "WaitingHuman" : "HumanHandling", Version = command.AssigneeUserId is null ? 0 : 1,
            CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc };
        db.HandoffCases.Add(entity);
        var target = Safe(command.AssigneeTarget, 64);
        var reason = Safe(command.ReasonCode, 96);
        db.SendCommands.Add(new SendCommandEntity { RobotConfigId = command.RobotConfigId, GroupProfileId = command.GroupProfileId,
            IdempotencyKey = command.IdempotencyKey, PayloadJson = JsonSerializer.Serialize(new { command.WorkToolRobotId, command.GroupName,
                Text = $"@{target} 已转人工。原因：{reason}；关联：{entity.Id:N}" }), NextAttemptAtUtc = nowUtc, CreatedAtUtc = nowUtc });
        try { await db.SaveChangesAsync(token); return Map(entity); }
        catch (DbUpdateException ex) when (ex.InnerException is MySqlException { Number: 1062 })
        { db.ChangeTracker.Clear(); return Map(await db.HandoffCases.AsNoTracking().SingleAsync(x => x.QuestionMessageId == command.QuestionMessageId, token)); }
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
        if (handoff.State == "Resolved" && existing is not null) return Map(existing);
        if (handoff.State != "HumanHandling") throw new HandoffStateException("Only a handled case can be resolved.");
        if (handoff.Version != expectedVersion) throw new HandoffConcurrencyException("The handoff was modified by another operator.");
        var question = await db.ConversationMessages.AsNoTracking().SingleAsync(x => x.Id == handoff.QuestionMessageId, token);
        handoff.State = "Resolved"; handoff.ResolvedByUserId = actor; handoff.FinalAnswer = finalAnswer; handoff.Version++; handoff.UpdatedAtUtc = nowUtc;
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
        return await MutateAsync(entity, expectedVersion, nowUtc, x => { if (x.State is not ("WaitingHuman" or "HumanHandling"))
            throw new HandoffStateException("Case cannot be assigned."); x.State = "HumanHandling"; x.AssigneeUserId = assignee; }, token);
    }

    public async Task<HandoffRecord> RestoreAiAsync(Guid handoffId, Guid actor, int expectedVersion, DateTime nowUtc, CancellationToken token)
    {
        var entity = await db.HandoffCases.SingleOrDefaultAsync(x => x.Id == handoffId, token) ?? throw new KeyNotFoundException();
        if (entity.State == "AIActive") return Map(entity);
        return await MutateAsync(entity, expectedVersion, nowUtc, x => { if (x.State != "Resolved")
            throw new HandoffStateException("Only resolved cases can restore AI."); x.State = "AIActive"; }, token);
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

    private async Task<HandoffRecord> MutateAsync(HandoffCaseEntity entity, int version, DateTime now, Action<HandoffCaseEntity> mutation, CancellationToken token)
    {
        if (entity.Version != version) throw new HandoffConcurrencyException("The handoff was modified by another operator.");
        mutation(entity); entity.Version++; entity.UpdatedAtUtc = now;
        try { await db.SaveChangesAsync(token); return Map(entity); }
        catch (DbUpdateConcurrencyException) { throw new HandoffConcurrencyException("The handoff was modified by another operator."); }
    }

    private static string Safe(string value, int length) => new(value.Where(c => !char.IsControl(c)).Take(length).ToArray());
    private static HandoffRecord Map(HandoffCaseEntity x) => new(x.Id, x.State, x.AssigneeUserId, x.Version);
    private static KnowledgeCandidateRecord Map(KnowledgeCandidateEntity x) => new(x.Id, x.HandoffCaseId, x.Question, x.Answer, x.Status, x.Version);
}
