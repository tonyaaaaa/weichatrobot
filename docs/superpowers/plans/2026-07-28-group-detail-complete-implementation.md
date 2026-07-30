# Group Detail Complete Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the current group-detail skeleton with the approved complete, data-backed four-panel administration experience.

**Architecture:** Extend the existing group configuration query with bounded read-only model-capability and memory-summary projections. Split the Vue page into four focused panels while the parent retains the single draft, configuration version, save, conflict, and navigation-guard responsibilities.

**Tech Stack:** ASP.NET Core 10 Minimal APIs, EF Core/MySQL, xUnit v3, Vue 3 Composition API, TypeScript, Element Plus, Vitest.

## Global Constraints

- Work directly on the current `master` checkout; do not create a branch or worktree.
- Do not commit, stage, push, deploy, or modify unrelated user-owned changes.
- Write a failing test and observe the expected failure before every production change.
- Never return model secrets, memory contents, WorkTool identifiers, or stable-identity claims.
- Preserve the existing group update request, authorization, audit, and optimistic-concurrency behavior.
- Use existing global design tokens; do not introduce another color system, font package, form library, or animation dependency.
- Use Element Plus controls, visible labels, keyboard focus, announced errors, and responsive layouts.

---

### Task 1: Add the default chat model capability projection

**Files:**
- Modify: `tests/server/WechatRobot.IntegrationTests/Groups/GroupConfigurationTests.cs`
- Modify: `src/server/WechatRobot.Api/Groups/GroupEndpoints.cs`
- Modify: `src/web/wechatrobot-admin/src/api/groups.ts`

**Interfaces:**
- Produces backend record:

```csharp
public sealed record DefaultChatModelCapabilityResponse(
    bool IsConfigured,
    string? ConfigurationName,
    string? ConnectionStatus,
    string WebSearchMode,
    bool CanUseWebSearch,
    string UnavailableReason);
```

- Produces frontend field:

```ts
defaultChatModel: {
  isConfigured: boolean;
  configurationName?: string | null;
  connectionStatus?: string | null;
  webSearchMode: string;
  canUseWebSearch: boolean;
  unavailableReason: 'none' | 'not_configured' | 'disabled' | 'connection_not_succeeded' | 'unsupported';
};
```

- [ ] **Step 1: Write failing integration tests for each stable capability state**

Add cases that seed no chat model, a disabled default model, a failed-connection model, a connected `WebSearchMode=None` model, and a connected `ZaiChatCompletions` model. Assert only non-secret fields:

```csharp
Assert.False(body.DefaultChatModel.IsConfigured);
Assert.Equal("not_configured", body.DefaultChatModel.UnavailableReason);

Assert.True(body.DefaultChatModel.CanUseWebSearch);
Assert.Equal("none", body.DefaultChatModel.UnavailableReason);
```

- [ ] **Step 2: Run the focused tests and observe the missing-property failure**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class "WechatRobot.IntegrationTests.Groups.GroupConfigurationTests"
```

Expected: compile or assertion failure because `DefaultChatModel` is absent.

- [ ] **Step 3: Add one database projection to `ToResponseAsync`**

Select the same effective chat model ordering used by runtime:

```csharp
var defaultChatModel = await database.ModelConfigs.AsNoTracking()
    .Where(model => model.ConfigurationType == "chat")
    .OrderByDescending(model => model.IsDefault)
    .ThenBy(model => model.CreatedAtUtc)
    .Select(model => new
    {
        model.Name,
        model.IsEnabled,
        model.ConnectionStatus,
        model.WebSearchMode
    })
    .FirstOrDefaultAsync(cancellationToken);
```

Map `CanUseWebSearch` only when enabled, connection status is `Succeeded`, and mode is not `None`. Return the first matching stable reason in the order: `not_configured`, `disabled`, `connection_not_succeeded`, `unsupported`, `none`.

- [ ] **Step 4: Add the response field and aligned TypeScript type**

Append `DefaultChatModelCapabilityResponse DefaultChatModel` to `GroupConfigurationResponse`; do not alter update request fields.

- [ ] **Step 5: Run focused backend tests and frontend typecheck**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~GroupConfigurationTests"
Set-Location src/web/wechatrobot-admin
npm run typecheck
```

Expected: PASS.

### Task 2: Add bounded group memory summary counts

**Files:**
- Modify: `tests/server/WechatRobot.IntegrationTests/Groups/GroupConfigurationMySqlTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Groups/GroupConfigurationTests.cs`
- Modify: `src/server/WechatRobot.Api/Groups/GroupEndpoints.cs`
- Modify: `src/web/wechatrobot-admin/src/api/groups.ts`

**Interfaces:**

```csharp
public sealed record GroupMemorySummaryResponse(
    int ActiveGroupMemoryCount,
    int ActiveMemberMemoryCount,
    int PendingCandidateCount,
    int PendingOrRunningJobCount);
```

```ts
memorySummary: {
  activeGroupMemoryCount: number;
  activeMemberMemoryCount: number;
  pendingCandidateCount: number;
  pendingOrRunningJobCount: number;
};
```

- [ ] **Step 1: Write failing SQLite and MySQL integration tests**

Seed records for two group IDs and mixed statuses. Assert the response counts only:

- active entries for the requested group;
- `scopeType == "Group"` separately from member/nickname scope;
- pending candidates for the requested group;
- pending, retrying, leased, or dispatching memory jobs for the requested group.

The test must also seed forgotten/rejected/completed records and assert they are excluded.

- [ ] **Step 2: Run both focused test classes and observe missing summary failures**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class "WechatRobot.IntegrationTests.Groups.GroupConfigurationTests" "WechatRobot.IntegrationTests.Groups.GroupConfigurationMySqlTests"
```

- [ ] **Step 3: Implement database-side count queries**

Use four `CountAsync` calls with `GroupProfileId == group.Id` and exact persisted status values already used by `MemoryEndpoints`. For durable jobs, filter memory job types and match the group ID from the existing structured job metadata; reuse an existing query helper if present rather than parsing arbitrary text in memory.

- [ ] **Step 4: Return only counts and align TypeScript**

Add `GroupMemorySummaryResponse MemorySummary` to `GroupConfigurationResponse`. Do not return memory content, subject names, evidence, payload JSON, or job errors.

- [ ] **Step 5: Run SQLite/MySQL integration tests**

Expected: all focused group configuration tests pass on both providers.

### Task 3: Define complete panel behavior with failing component tests

**Files:**
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts`
- Create: `src/web/wechatrobot-admin/src/components/groups/GroupKnowledgeAnswerPanel.spec.ts`
- Create: `src/web/wechatrobot-admin/src/components/groups/GroupContextMemoryPanel.spec.ts`
- Create: `src/web/wechatrobot-admin/src/components/groups/GroupRunRecordsPanel.spec.ts`
- Create: `src/web/wechatrobot-admin/src/components/groups/GroupAdvancedSettingsPanel.spec.ts`

**Interfaces:**

```ts
type GroupKnowledgeAnswerPanelProps = {
  availableTags: GroupConfiguration['availableTags'];
  boundTagIds: string[];
  answerFallback: AnswerFallbackSettings;
  defaultChatModel: GroupConfiguration['defaultChatModel'];
};
```

```ts
type GroupContextMemoryPanelProps = {
  configured: ContextOverrides;
  effective: EffectiveContext;
  memorySummary: GroupConfiguration['memorySummary'];
  groupId: string;
};
```

- [ ] **Step 1: Add failing knowledge-panel tests**

Assert three ordered sections, searchable tag selection, read-only global tag explanation, conditional search parameters, and the unsupported-model degradation warning.

- [ ] **Step 2: Add failing context-panel tests**

Assert all six context fields use Element Plus components, all effective inherited values are visible, all four real summary counts render, and clear-context emits a dedicated event.

- [ ] **Step 3: Add failing records and advanced-panel tests**

Assert four filtered destination links, the name-filter explanation on send queue, two collapsed advanced sections by default, and WorkTool-import guidance.

- [ ] **Step 4: Extend parent-page tests**

Assert panel components receive the same shared draft, edits remain after tab switches, one save request contains all fields, and no raw group ID or handoff control renders.

- [ ] **Step 5: Run the five component test files and observe missing-component failures**

```powershell
Set-Location src/web/wechatrobot-admin
npm test -- --run src/views/groups/GroupRulesView.spec.ts src/components/groups/GroupKnowledgeAnswerPanel.spec.ts src/components/groups/GroupContextMemoryPanel.spec.ts src/components/groups/GroupRunRecordsPanel.spec.ts src/components/groups/GroupAdvancedSettingsPanel.spec.ts
```

Expected: FAIL because the four components and new response fields do not exist.

### Task 4: Implement the knowledge and answer panel

**Files:**
- Create: `src/web/wechatrobot-admin/src/components/groups/GroupKnowledgeAnswerPanel.vue`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue`

**Interfaces:**
- Emits `update:boundTagIds` with a copied `string[]`.
- Emits `update:answerFallback` with a copied `AnswerFallbackSettings`.

- [ ] **Step 1: Implement the three-step answer flow**

Use semantic ordered sections titled `先查知识库`, `未命中时继续尝试`, and `仍无可靠答案时`. Use `ElSelect` multiple/filterable for tags and `ElSwitch`, `ElInputNumber`, `ElSelect`, and `ElInput` for fallback settings.

- [ ] **Step 2: Implement progressive disclosure**

Render search result count, recency, domains, content size, and source display only while Web Search is enabled. Turning Web Search on for the first time enables model-knowledge fallback without enabling source display.

- [ ] **Step 3: Render real model capability**

Use `ElAlert` with the stable reason mapping. When enabled but unsupported, state that runtime skips Web Search and continues the configured fallback chain.

- [ ] **Step 4: Wire immutable update events into the parent draft**

Do not let the panel call the API or own the configuration version.

- [ ] **Step 5: Run the knowledge-panel and parent tests**

Expected: PASS.

### Task 5: Implement context, memory, records, and advanced panels

**Files:**
- Rewrite: `src/web/wechatrobot-admin/src/components/groups/ContextPolicyForm.vue`
- Rewrite: `src/web/wechatrobot-admin/src/components/groups/RuleEditor.vue`
- Create: `src/web/wechatrobot-admin/src/components/groups/GroupContextMemoryPanel.vue`
- Create: `src/web/wechatrobot-admin/src/components/groups/GroupRunRecordsPanel.vue`
- Create: `src/web/wechatrobot-admin/src/components/groups/GroupAdvancedSettingsPanel.vue`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue`

- [ ] **Step 1: Convert context controls to Element Plus**

Use `ElSelect`, `ElInputNumber`, and `ElSwitch`-compatible nullable choices. Preserve `null` as inherit and show `effective` values beside every inherited field.

- [ ] **Step 2: Add four memory summary cards and two real links**

Label member memory as “成员昵称作用域记忆” rather than “用户数量”. Use counts from `memorySummary`; do not fetch or calculate client-side.

- [ ] **Step 3: Add the four run-record cards**

Use the existing route queries. Do not display invented recent activity or success totals.

- [ ] **Step 4: Convert rules to Element Plus and wrap advanced content in `ElCollapse`**

Default active collapse names to `[]`. Preserve exact, contains, regex, exclude, ignore-case, and preview behavior.

- [ ] **Step 5: Run all focused panel and page tests**

Expected: PASS.

### Task 6: Finish parent layout, responsive behavior, and save/error states

**Files:**
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/styles.css` only for truly shared tokens or utilities

- [ ] **Step 1: Add failing tests for accessible error recovery and responsive classes**

Assert load failure has `role="alert"` and a retry action, the save bar remains after non-409 failure, and all tabs have accessible labels.

- [ ] **Step 2: Implement one header, one content container, and one dirty save bar**

Keep the parent responsible for `load`, `save`, `clearContext`, `isDirty`, configuration version, and route guard.

- [ ] **Step 3: Add scoped responsive CSS**

At widths below 820 pixels, turn multi-column panel grids into one column. Ensure controls use `min-width: 0` and `width: 100%`; preserve a 44-pixel minimum interactive height.

- [ ] **Step 4: Run focused frontend tests, typecheck, and build**

```powershell
Set-Location src/web/wechatrobot-admin
npm test -- --run src/views/groups/GroupRulesView.spec.ts src/components/groups/GroupKnowledgeAnswerPanel.spec.ts src/components/groups/GroupContextMemoryPanel.spec.ts src/components/groups/GroupRunRecordsPanel.spec.ts src/components/groups/GroupAdvancedSettingsPanel.spec.ts
npm run typecheck
npm run build
```

Expected: PASS with no Vue warnings.

### Task 7: Cross-boundary verification

**Files:**
- No product file changes unless a failing verification exposes a scoped regression.

- [ ] **Step 1: Run complete server unit, contract, and integration projects**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore
```

- [ ] **Step 2: Run the complete frontend suite**

```powershell
Set-Location src/web/wechatrobot-admin
npm test -- --run
npm run typecheck
npm run build
```

- [ ] **Step 3: Build the solution and check the diff**

```powershell
Set-Location H:\Codex\WechatRobot
dotnet build WechatRobot.slnx --no-restore
git diff --check
```

- [ ] **Step 4: Restart affected local processes and verify runtime**

Use `.local/.env` through `WECHATROBOT_ENV_FILE`, keep `.local` as API/Worker working directory, and verify API live, authenticated readiness, Worker heartbeat, and frontend HTTP 200.

- [ ] **Step 5: Perform authenticated browser visual acceptance**

Inspect the group detail at 1440, 768, and 375 pixels. Verify all tabs, progressive settings, memory counts, empty/error states, sticky save bar, dialogs, keyboard focus, and no horizontal overflow.

- [ ] **Step 6: Record verification without committing**

Report exact commands, pass counts, runtime evidence, any unavailable external dependency, and all pre-existing working-tree changes kept untouched.
