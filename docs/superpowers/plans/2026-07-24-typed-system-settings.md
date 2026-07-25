# Typed System Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the placeholder system-settings page with a typed, versioned, auditable settings surface whose saved values are consumed by the running conversation pipeline.

**Architecture:** A code-owned allowlist defines every editable key, JSON type, default, validation range, UI control, runtime consumer, and restart behavior. `system_setting` remains the current-value projection; a new append-only history table records every successful update and rollback. The Worker loads one typed conversation-settings snapshot from MySQL for each leased inbound message, then passes that immutable snapshot through retrieval-query construction, summarization, and grounded-answer generation.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, Entity Framework Core/MySQL, xUnit v3 with Microsoft Testing Platform, Vue 3, TypeScript, Element Plus, Vitest, Vite.

## Global Constraints

- Work in `H:\Codex\WechatRobot\.worktrees\codex-wechatrobot-mvp` on `codex/wechatrobot-mvp`.
- Human handoff, Enterprise WeChat member sync, agent selectors, proactive handoff, and handoff pause-policy editing remain deferred.
- Do not create an arbitrary key/value or JSON editor.
- The registry is the only source of editable setting keys.
- Secret values, credentials, callback tokens, JWT material, model keys, OCR keys, OSS keys, and WorkTool robot identifiers must never enter `system_setting`, history, API responses, frontend state, or audit details.
- Every key declares JSON type, default value, validation, runtime consumer, and restart behavior.
- Every initial setting in this phase has `restartBehavior: "hot-reload"` and must affect the next leased inbound message without restarting API or Worker.
- A missing database row means “use the code/configuration fallback”; reading settings must not insert defaults.
- Updates and rollbacks require `ExpectedVersion`; a missing row has version `0`.
- Rollback creates a new monotonically increasing version and never edits or deletes history.
- One inbound message uses one immutable settings snapshot so a concurrent admin edit cannot mix versions within that message.
- API and Worker must remain available when MySQL is available but no settings rows exist.
- Do not stop the running API PID `18436` or Worker PID `32828`; use isolated build outputs while their default binaries are locked.

---

## Approved Initial Registry

Only settings already consumed by the non-handoff conversation pipeline are in scope:

| Key | Type | Default | Validation | Runtime consumer |
|---|---|---:|---|---|
| `conversation.grounded.confidenceThreshold` | number | `0.7` | `0..1` | `GroundedAnswerService` |
| `conversation.grounded.maximumEvidence` | integer | `8` | `1..50` | `GroundedAnswerService` |
| `conversation.grounded.noEvidencePolicy` | enum | `InsufficientEvidence` | `InsufficientEvidence`, `Clarification`, `Handoff` | `GroundedAnswerService` |
| `conversation.retrieval.tokenCap` | integer | `512` | `8..100000` | `RetrievalQueryBuilder` |
| `conversation.summary.maxInputTokens` | integer | `512` | `16..100000` | `ChatConversationSummarizer` |
| `conversation.summary.maxOutputCharacters` | integer | `1200` | `32..20000` | `ChatConversationSummarizer` |

The existing handoff failure threshold and phrases remain configuration-file values because artificial handoff expansion is deferred. Upload, parsing, OCR, indexing, health, CORS, rate-limit, and authentication settings are also excluded: changing them safely requires cross-process coordination or startup validation not provided by this phase.

## Public Contracts

Create `src/server/WechatRobot.Application/Administration/SystemSettingContracts.cs`:

```csharp
public enum SystemSettingValueType { Integer, Number, Enum }
public enum SystemSettingRestartBehavior { HotReload }

public sealed record SystemSettingDefinition(
    string Key,
    string DisplayName,
    string Description,
    string Group,
    SystemSettingValueType ValueType,
    JsonElement DefaultValue,
    decimal? Minimum,
    decimal? Maximum,
    IReadOnlyList<string> AllowedValues,
    string RuntimeConsumer,
    SystemSettingRestartBehavior RestartBehavior);

public sealed record SystemSettingValue(
    SystemSettingDefinition Definition,
    JsonElement Value,
    int Version,
    bool IsOverridden,
    DateTime? UpdatedAtUtc);

public sealed record SystemSettingRevision(
    Guid Id,
    string Key,
    int Version,
    JsonElement Value,
    string ChangeKind,
    int? SourceVersion,
    string Actor,
    DateTime CreatedAtUtc);

public sealed record SystemSettingRevisionPage(
    IReadOnlyList<SystemSettingRevision> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record UpdateSystemSettingCommand(
    string Key,
    JsonElement Value,
    int ExpectedVersion,
    string Actor);

public sealed record RollbackSystemSettingCommand(
    string Key,
    int TargetVersion,
    int ExpectedVersion,
    string Actor);
```

Create `src/server/WechatRobot.Application/Administration/ConversationRuntimeSettings.cs`:

```csharp
public sealed record ConversationRuntimeSettingsSnapshot(
    GroundedAnswerOptions GroundedAnswer,
    RetrievalQueryOptions RetrievalQuery,
    ConversationSummaryOptions ConversationSummary,
    IReadOnlyDictionary<string, int> SourceVersions);

public interface IConversationRuntimeSettingsProvider
{
    Task<ConversationRuntimeSettingsSnapshot> GetAsync(CancellationToken token);
}
```

The management API is Admin-only:

```text
GET  /api/admin/system-settings
PUT  /api/admin/system-settings/{key}
GET  /api/admin/system-settings/{key}/history?page=1&pageSize=20
POST /api/admin/system-settings/{key}/rollback
```

Update body:

```json
{ "value": 0.75, "expectedVersion": 0 }
```

Rollback body:

```json
{ "targetVersion": 1, "expectedVersion": 3 }
```

A version conflict returns HTTP 409:

```json
{
  "error": "system-setting-version-conflict",
  "key": "conversation.grounded.confidenceThreshold",
  "currentVersion": 3
}
```

---

### Task 1: Typed registry and validation

**Files:**

- Create: `src/server/WechatRobot.Application/Administration/SystemSettingContracts.cs`
- Create: `src/server/WechatRobot.Application/Administration/SystemSettingRegistry.cs`
- Test: `tests/server/WechatRobot.UnitTests/Administration/SystemSettingRegistryTests.cs`

**Interfaces:**

- Produces: `SystemSettingRegistry.All`, `SystemSettingRegistry.GetRequired(string)`, and `SystemSettingRegistry.Validate(string, JsonElement)`.
- Validation returns a cloned, normalized `JsonElement`; integer values reject fractions, enums are ordinal/case-sensitive, and unknown keys fail closed.

- [ ] **Step 1: Write failing registry tests**

```csharp
[Fact]
public void Registry_contains_only_the_six_approved_non_secret_keys()
{
    Assert.Equal(6, SystemSettingRegistry.All.Count);
    Assert.All(SystemSettingRegistry.All, item =>
    {
        Assert.Equal(SystemSettingRestartBehavior.HotReload, item.RestartBehavior);
        Assert.DoesNotContain("secret", item.Key, StringComparison.OrdinalIgnoreCase);
    });
}

[Theory]
[InlineData("conversation.grounded.confidenceThreshold", "1.01")]
[InlineData("conversation.grounded.maximumEvidence", "8.5")]
[InlineData("conversation.retrieval.tokenCap", "7")]
[InlineData("conversation.grounded.noEvidencePolicy", "\"unknown\"")]
public void Validation_rejects_wrong_types_ranges_and_enum_values(string key, string json)
{
    using var document = JsonDocument.Parse(json);
    Assert.Throws<SystemSettingValidationException>(
        () => SystemSettingRegistry.Validate(key, document.RootElement));
}
```

- [ ] **Step 2: Run the tests and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter-class "WechatRobot.UnitTests.Administration.SystemSettingRegistryTests"
```

Expected: compilation fails because the registry and contracts do not exist.

- [ ] **Step 3: Implement the immutable registry**

Define all six entries from “Approved Initial Registry”. Implement validation by inspecting `JsonValueKind`, using `TryGetInt32` for integer keys, `TryGetDecimal` for numeric keys, and exact membership for enum keys. Return `JsonDocument.Parse(value.GetRawText()).RootElement.Clone()` so no response retains a disposed document.

- [ ] **Step 4: Run registry tests and the existing option tests**

Run the new class plus:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter-class "WechatRobot.UnitTests.Conversations.GroundedAnswerTests" --filter-class "WechatRobot.UnitTests.Conversations.RetrievalQueryBuilderTests" --filter-class "WechatRobot.UnitTests.Conversations.ConversationSummarizerTests"
```

Expected: all selected tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/server/WechatRobot.Application/Administration tests/server/WechatRobot.UnitTests/Administration
git commit -m "feat: define typed system settings"
```

### Task 2: Append-only persistence, concurrency, and audit

**Files:**

- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/AdministrationEntities.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Configurations/AdministrationConfigurations.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/WechatRobotDbContext.cs`
- Create: `src/server/WechatRobot.Infrastructure/Administration/SystemSettingManager.cs`
- Create: EF migration with suffix `AddSystemSettingHistory` under `src/server/WechatRobot.Infrastructure/Persistence/Migrations/`
- Test: `tests/server/WechatRobot.IntegrationTests/Administration/SystemSettingManagerTests.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Administration/SystemSettingMySqlConcurrencyTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Persistence/MigrationTests.cs`

**Interfaces:**

- Produces: `ListAsync`, `GetHistoryAsync`, `UpdateAsync`, and `RollbackAsync`.
- `system_setting_history` is append-only and has a unique index on `(SettingKey, Version)`.
- Both update and rollback write `AdministrationAuditEntity` with key, old version, new version, change kind, and rollback source version only; raw values are omitted from audit details.

- [ ] **Step 1: Write failing lifecycle tests**

Cover these exact cases:

```csharp
// Missing row reads registry default at version 0 without inserting.
// First update with ExpectedVersion=0 inserts current row version 1 and history version 1.
// Stale update returns SystemSettingConcurrencyException containing the current version.
// Rollback from current version 2 to history version 1 creates current/history version 3.
// History remains [3, 2, 1] and old rows are byte-for-byte unchanged.
// Unknown keys and invalid JSON never reach SaveChangesAsync.
// Audit JSON contains no "value", secret-like key, or raw JSON.
```

- [ ] **Step 2: Run InMemory and MySQL tests and verify RED**

Run the two new test classes. Expected: compilation fails because the entity and manager do not exist.

- [ ] **Step 3: Add the history entity and EF configuration**

```csharp
public sealed class SystemSettingRevisionEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SettingKey { get; set; } = string.Empty;
    public int Version { get; set; }
    public string ValueJson { get; set; } = "null";
    public string ChangeKind { get; set; } = string.Empty;
    public int? SourceVersion { get; set; }
    public string Actor { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
```

Configure `ValueJson` as MySQL `json`, lengths of 128/32/256 for key/change kind/actor, and unique `(SettingKey, Version)`.

- [ ] **Step 4: Generate and inspect the migration**

Run:

```powershell
dotnet ef migrations add AddSystemSettingHistory --project src/server/WechatRobot.Infrastructure --startup-project src/server/WechatRobot.Api
```

Verify `Up` only creates `system_setting_history` and its indexes. Verify `Down` only drops that table. Do not rewrite existing `system_setting` rows.

- [ ] **Step 5: Implement transactional update and rollback**

For MySQL, update the current row with `WHERE Key = key AND Version = expectedVersion`; require one affected row. For expected version `0`, insert and translate a unique-key race into `SystemSettingConcurrencyException`. Insert history and audit in the same transaction. Use an EF tracked fallback for InMemory tests because `ExecuteUpdateAsync` and relational transactions are unavailable there.

- [ ] **Step 6: Run lifecycle, race, migration, and audit tests**

Expected: all new tests pass; two parallel writes with the same expected version yield exactly one success, one 409-equivalent conflict, one new history row, and one audit row.

- [ ] **Step 7: Commit**

```powershell
git add src/server/WechatRobot.Infrastructure tests/server/WechatRobot.IntegrationTests/Administration tests/server/WechatRobot.IntegrationTests/Persistence/MigrationTests.cs
git commit -m "feat: version system settings"
```

### Task 3: Authorized management API

**Files:**

- Create: `src/server/WechatRobot.Api/Administration/SystemSettingEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Program.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Administration/SystemSettingEndpointTests.cs`

**Interfaces:**

- Consumes: `SystemSettingManager` and the contracts from Tasks 1–2.
- Produces: the four Admin-only routes defined above.

- [ ] **Step 1: Write failing endpoint tests**

Test anonymous `401`, non-Admin `403`, Admin list `200`, update `200`, history pagination and bounds, rollback `200`, invalid value `400`, unknown key `404`, and stale version `409` with the exact conflict shape.

- [ ] **Step 2: Run endpoint tests and verify RED**

Expected: all requests return `404`.

- [ ] **Step 3: Map the endpoints**

Use:

```csharp
var group = endpoints.MapGroup("/api/admin/system-settings")
    .RequireAuthorization(SystemRoles.Admin)
    .RequireRateLimiting(Security.RateLimitPolicies.Ordinary);
```

Resolve actor from `ClaimTypes.NameIdentifier`, then `Identity.Name`; reject a missing actor instead of writing an anonymous audit. Clamp history page size to `1..100`. Do not map provider/DB failures to validation errors.

- [ ] **Step 4: Run endpoint and role-authorization tests**

Expected: all pass and serialized responses contain none of `secret`, `credential`, `signingKey`, `accessKey`, `robotId`, or arbitrary unregistered keys.

- [ ] **Step 5: Commit**

```powershell
git add src/server/WechatRobot.Api/Administration src/server/WechatRobot.Api/Program.cs tests/server/WechatRobot.IntegrationTests/Administration/SystemSettingEndpointTests.cs
git commit -m "feat: expose typed system settings"
```

### Task 4: Prove saved settings affect the Worker

**Files:**

- Create: `src/server/WechatRobot.Infrastructure/Administration/ConversationRuntimeSettingsProvider.cs`
- Modify: `src/server/WechatRobot.Application/Conversations/GroundedAnswerService.cs`
- Modify: `src/server/WechatRobot.Application/Conversations/RetrievalQueryBuilder.cs`
- Modify: `src/server/WechatRobot.Application/Conversations/ConversationSummarizer.cs`
- Modify: `src/server/WechatRobot.Application/Messaging/InboundMessageProcessor.cs`
- Modify: `src/server/WechatRobot.Worker/Program.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Administration/ConversationRuntimeSettingsTests.cs`
- Modify: affected tests under `tests/server/WechatRobot.UnitTests/Conversations/`

**Interfaces:**

- Consumes: `IConversationRuntimeSettingsProvider.GetAsync`.
- Produces: one immutable snapshot per `InboundMessageProcessor.ProcessAsync` call.

- [ ] **Step 1: Write failing runtime-consumer tests**

Seed database overrides for all six keys, load a snapshot, and assert every typed option property. Add a pipeline test that writes confidence threshold `0.95`, processes one message, updates it to `0.10`, processes the next message, and proves the second decision uses the new value without rebuilding the service provider.

- [ ] **Step 2: Run tests and verify RED**

Expected: the current singleton options ignore database rows.

- [ ] **Step 3: Implement fallback plus override loading**

`ConversationRuntimeSettingsProvider` receives the validated Worker configuration fallbacks and a scoped `WechatRobotDbContext`. Query only the six registry keys, validate every stored value again, and fail the leased message explicitly if persisted JSON is corrupt instead of silently using unsafe data.

- [ ] **Step 4: Pass options explicitly through the pipeline**

Change signatures to:

```csharp
RetrievalQueryResult Build(
    string currentQuestion,
    ConversationContextResult context,
    RetrievalQueryOptions options);

Task<string> SummarizeAsync(
    ModelProviderConfiguration configuration,
    string? existingSummary,
    IReadOnlyList<ConversationHistoryMessage> evictedMessages,
    ConversationSummaryOptions options,
    CancellationToken token);

Task<GroundedAnswerResult> AnswerAsync(
    GroundedAnswerRequest request,
    GroundedAnswerOptions options,
    CancellationToken token);
```

`InboundMessageProcessor.ProcessAsync` loads exactly one snapshot and supplies its three option records to all calls for that message. Remove the three option singletons from DI; retain validated configuration instances only as provider fallbacks.

- [ ] **Step 5: Run runtime, conversation, and pipeline regression tests**

Expected: saved values are read on the next message; a single message cannot observe two setting versions; existing default-behavior tests remain unchanged.

- [ ] **Step 6: Commit**

```powershell
git add src/server/WechatRobot.Application src/server/WechatRobot.Infrastructure/Administration src/server/WechatRobot.Worker/Program.cs tests/server/WechatRobot.UnitTests/Conversations tests/server/WechatRobot.IntegrationTests/Administration
git commit -m "feat: apply runtime conversation settings"
```

### Task 5: Typed frontend client

**Files:**

- Create: `src/web/wechatrobot-admin/src/api/systemSettings.ts`
- Test: `src/web/wechatrobot-admin/src/api/systemSettings.spec.ts`

**Interfaces:**

- Produces: discriminated TypeScript definitions and `list`, `update`, `history`, and `rollback` methods.

- [ ] **Step 1: Write failing client tests**

Assert exact HTTP methods, encoded keys, body placement, history query parameters, and 409 payload preservation.

- [ ] **Step 2: Implement the typed client**

```ts
export type SystemSettingValueType = 'integer' | 'number' | 'enum';
export type SystemSettingRestartBehavior = 'hotReload';
export type SystemSettingPrimitive = number | string;

export interface SystemSettingDefinition {
  key: string;
  displayName: string;
  description: string;
  group: string;
  valueType: SystemSettingValueType;
  defaultValue: SystemSettingPrimitive;
  minimum: number | null;
  maximum: number | null;
  allowedValues: string[];
  runtimeConsumer: string;
  restartBehavior: SystemSettingRestartBehavior;
}
```

Do not add an index signature or `Record<string, unknown>` escape hatch.

- [ ] **Step 3: Run client tests and typecheck**

Expected: pass.

- [ ] **Step 4: Commit**

```powershell
git add src/web/wechatrobot-admin/src/api/systemSettings.ts src/web/wechatrobot-admin/src/api/systemSettings.spec.ts
git commit -m "feat: add system settings client"
```

### Task 6: Settings management page

**Files:**

- Replace: `src/web/wechatrobot-admin/src/views/settings/SystemSettingsView.vue`
- Create: `src/web/wechatrobot-admin/src/views/settings/SystemSettingsView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/task16-operational.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/element-plus-operational.spec.ts`

**Interfaces:**

- Consumes: the typed client from Task 5.
- Produces: grouped numeric/select controls, per-setting save, version conflict recovery, history drawer, and rollback confirmation.

- [ ] **Step 1: Write failing page tests**

Test loading/empty/error states; numeric and enum controls; disabled save when unchanged or invalid; exact expected-version request; `409` merge and conflict message; newest-first history; rollback confirmation and refresh; and absence of arbitrary JSON textarea or secret-related labels.

- [ ] **Step 2: Implement registry-driven controls**

Render `ElInputNumber` for integer/number definitions and `ElSelect` for enum definitions. Show:

- current version and whether the value is a database override;
- “立即生效（下一条消息）” for `hotReload`;
- validation range or allowed values;
- runtime consumer as operational evidence;
- separate load, save, history, and rollback errors using `role="alert"`/`aria-live`.

Do not show the obsolete claims that settings API is unavailable or that handoff thresholds are editable.

- [ ] **Step 3: Implement history and rollback UX**

Open a paged drawer for one key. The rollback button sends the selected history version plus the current setting version. Confirmation text:

```text
回滚会复制历史值并创建一个新版本，不会删除现有历史。确认继续？
```

After success, reload both current values and history. On `409`, display the server’s current version and reload before enabling another mutation.

- [ ] **Step 4: Run page tests, frontend typecheck, and accessibility regressions**

Expected: all pass; interactive targets remain at least 44px; narrow screens scroll tables/drawers without clipping actions.

- [ ] **Step 5: Commit**

```powershell
git add src/web/wechatrobot-admin/src/views/settings src/web/wechatrobot-admin/src/views/task16-operational.spec.ts src/web/wechatrobot-admin/src/views/element-plus-operational.spec.ts
git commit -m "feat: manage typed system settings"
```

### Task 7: Phase acceptance and roadmap update

**Files:**

- Modify: `docs/superpowers/plans/2026-07-24-frontend-backend-alignment-roadmap.md`

- [ ] **Step 1: Run the complete acceptance gate**

Run registry, persistence, MySQL concurrency, endpoint authorization, runtime-consumer, and frontend settings tests. Then run full server unit tests, contract tests, an isolated solution build, frontend typecheck, all frontend tests, and production build.

- [ ] **Step 2: Run invariant scans**

Search API contracts, history/audit serializers, Vue production views, and built assets for secret fields, arbitrary JSON editors, handoff setting controls, stale placeholder copy, and rollback wording that implies history deletion.

- [ ] **Step 3: Record completion**

Set “P1 Typed system settings” to `Completed`, add exact commits/test counts/skips, and set “P1 Dashboard and operational summary” to `Planned` with a new detailed-plan path. Keep user/role and handoff mapping states unchanged.

- [ ] **Step 4: Commit**

```powershell
git add docs/superpowers/plans/2026-07-24-frontend-backend-alignment-roadmap.md
git commit -m "docs: complete typed system settings"
```

---

## Self-Review Record

- Spec coverage: typed registry, current values, optimistic concurrency, append-only history, rollback-as-new-version, audit, real runtime consumption, Admin authorization, and frontend conflict/rollback flows each have an implementation task and acceptance test.
- Scope boundary: handoff settings, secrets, startup-only infrastructure configuration, arbitrary keys, and unrelated user/member management are explicitly excluded.
- Type consistency: registry keys and ranges match the existing `GroundedAnswerOptions`, `RetrievalQueryOptions`, and `ConversationSummaryOptions` validators; runtime method signatures use the same option records.
- Placeholder scan: passed; every implementation and error path is concrete.
