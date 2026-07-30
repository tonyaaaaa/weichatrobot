# Group Detail Configuration Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the mixed single-group configuration page with the approved business-first detail layout: `知识与回答`, `上下文与记忆`, `运行记录`, and `高级设置`.

**Architecture:** Extend the existing group configuration response with authoritative read-only header metadata, keep one shared client-side draft across editable tabs, reuse current group/context/memory/audit/send-queue contracts, and isolate matching rules under advanced settings. The UI never fabricates WorkTool data or member capabilities.

**Tech Stack:** ASP.NET Core 10 Minimal APIs, EF Core/MySQL, Vue 3 Composition API, TypeScript, Vue Router, Element Plus, Vitest.

## Global Constraints

- Execute after or independently from `2026-07-28-member-aware-conversation-prompts.md`; the plans touch different primary code paths but share final verification.
- Preserve all existing working-tree changes, especially current edits in `GroupEndpoints.cs`, `groups.ts`, context views, audit views, memory views, and new group selector files.
- Use the approved option B layout; do not redesign the group list page.
- The four tabs are exactly: `知识与回答`, `上下文与记忆`, `运行记录`, `高级设置`.
- WorkTool-imported groups are already registered groups. Do not auto-create match rules or require a second manual registration step.
- Do not show an editable internal group ID.
- Do not restore human-agent or human-handoff UI.
- Use Element Plus dialogs/messages; no native `alert`, `confirm`, or `prompt`.
- Keep a single shared draft, preserve it when switching tabs, show the fixed save bar only when dirty, and never overwrite a 409 conflict.
- Run-record links and summaries must use real existing contracts. Missing data stays absent rather than mocked.

---

## Task 1: Extend the group-detail response with authoritative header metadata

**Files:**

- Modify: `src/server/WechatRobot.Api/Groups/GroupEndpoints.cs`
- Modify: `src/web/wechatrobot-admin/src/api/groups.ts`
- Test: `tests/server/WechatRobot.IntegrationTests/Groups/GroupConfigurationTests.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Groups/GroupConfigurationMySqlTests.cs`
- Test: `src/web/wechatrobot-admin/src/api/groups.spec.ts`

- [ ] Add failing backend tests for `GET /api/groups/{id}/configuration` asserting:
  - `robotName` comes from `RobotConfigEntity.Name`;
  - `workToolGroupRemark`, `registrationSource`, lifecycle `state`, `stateVersion`, and `isEnabled` come from the group row;
  - archived state wins over disabled state.

- [ ] Run focused group configuration tests and confirm failure.

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class "*GroupConfigurationTests|*GroupConfigurationMySqlTests"
```

- [ ] Query the associated robot name in `ToResponseAsync`; return a sanitized placeholder such as `未找到机器人配置` only when referential data is genuinely missing.

- [ ] Extend `GroupConfigurationResponse` with an explicit nested header contract.

```csharp
public sealed record GroupIdentityResponse(
    string RobotName,
    string? WorkToolGroupRemark,
    string RegistrationSource,
    string State,
    bool IsEnabled,
    int StateVersion);
```

- [ ] Extend the frontend type with the same field names and lifecycle union; do not infer these fields client-side.

```ts
identity: {
  robotName: string;
  workToolGroupRemark?: string | null;
  registrationSource: string;
  state: GroupLifecycleStatus;
  isEnabled: boolean;
  stateVersion: number;
};
```

- [ ] Add an API serialization test for the frontend client and run it.

```powershell
Set-Location src/web/wechatrobot-admin
npm test -- --run src/api/groups.spec.ts
```

- [ ] Run the backend focused tests again and commit the contract change.

```powershell
git add src/server/WechatRobot.Api/Groups/GroupEndpoints.cs src/web/wechatrobot-admin/src/api/groups.ts tests/server/WechatRobot.IntegrationTests/Groups/GroupConfigurationTests.cs tests/server/WechatRobot.IntegrationTests/Groups/GroupConfigurationMySqlTests.cs src/web/wechatrobot-admin/src/api/groups.spec.ts
git commit -m "feat: expose group detail identity metadata"
```

## Task 2: Extract deterministic draft and dirty-state helpers

**Files:**

- Create: `src/web/wechatrobot-admin/src/views/groups/groupConfigurationDraft.ts`
- Create: `src/web/wechatrobot-admin/src/views/groups/groupConfigurationDraft.spec.ts`

- [ ] Write failing tests for:
  - creating a deep editable draft from `GroupConfiguration`;
  - normalizing rule and tag order before comparison;
  - ignoring read-only response fields;
  - reporting clean immediately after load/save and dirty after any editable change.

- [ ] Run the focused test and confirm failure.

```powershell
Set-Location src/web/wechatrobot-admin
npm test -- --run src/views/groups/groupConfigurationDraft.spec.ts
```

- [ ] Implement explicit helpers rather than comparing the full API object.

```ts
export interface GroupConfigurationDraft {
  includeRules: GroupRule[];
  excludeRules: GroupRule[];
  boundTagIds: string[];
  context: ContextOverrides;
  answerFallback: AnswerFallbackSettings;
}

export function draftSignature(draft: GroupConfigurationDraft): string {
  return JSON.stringify(normalizeDraft(draft));
}
```

- [ ] Ensure cloning does not retain reactive references and that tag/rule normalization does not change the visible draft order.

- [ ] Run the focused test and commit.

```powershell
git add src/web/wechatrobot-admin/src/views/groups/groupConfigurationDraft.ts src/web/wechatrobot-admin/src/views/groups/groupConfigurationDraft.spec.ts
git commit -m "test: define group configuration draft semantics"
```

## Task 3: Build the four-tab business-first detail shell

**Files:**

- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue`
- Create: `src/web/wechatrobot-admin/src/components/groups/GroupKnowledgeAnswerPanel.vue`
- Create: `src/web/wechatrobot-admin/src/components/groups/GroupContextMemoryPanel.vue`
- Create: `src/web/wechatrobot-admin/src/components/groups/GroupRunRecordsPanel.vue`
- Create: `src/web/wechatrobot-admin/src/components/groups/GroupAdvancedSettingsPanel.vue`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts`

- [ ] Replace the old layout test with a failing test that asserts:
  - one header and one `返回群列表` action;
  - group name, robot name, remark, source, and state are read-only;
  - the default selected tab is `知识与回答`;
  - all four exact tab labels exist;
  - matching rules appear only after selecting `高级设置`;
  - internal group ID and human-agent controls are absent.

- [ ] Add failing tests showing a draft edit survives tab switches.

- [ ] Run the focused component test and confirm failure.

```powershell
Set-Location src/web/wechatrobot-admin
npm test -- --run src/views/groups/GroupRulesView.spec.ts
```

- [ ] Build the header with Element Plus tags for lifecycle/source and remove the duplicated identity/back area.

- [ ] Implement tab responsibilities:
  - `知识与回答`: knowledge-tag bindings and answer fallback order/settings;
  - `上下文与记忆`: existing `ContextPolicyForm`, read-only navigation to current context and filtered memory center, and the separate clear-context action;
  - `运行记录`: navigation cards to current context, conversation audit, filtered memory center, and send queue;
  - `高级设置`: existing `RuleEditor` and `RulePreview`, with explicit text that imported WorkTool groups do not require generated rules.

- [ ] Pass draft state via props and update events; do not let panels save independently.

- [ ] Use existing route query contracts:

```ts
{ name: 'group-context', params: { id: props.groupId } }
{ name: 'memory-center', query: { groupId: props.groupId } }
{ name: 'audit', query: { groupId: props.groupId } }
{ name: 'send-queue', query: { group: props.groupName } }
```

- [ ] If a destination currently ignores its documented query, add the smallest route/view change and a focused test; do not add a parallel API.

- [ ] Run the component test again and commit the shell.

```powershell
git add src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts src/web/wechatrobot-admin/src/components/groups/GroupKnowledgeAnswerPanel.vue src/web/wechatrobot-admin/src/components/groups/GroupContextMemoryPanel.vue src/web/wechatrobot-admin/src/components/groups/GroupRunRecordsPanel.vue src/web/wechatrobot-admin/src/components/groups/GroupAdvancedSettingsPanel.vue
git commit -m "feat: redesign the group configuration detail page"
```

## Task 4: Implement one dirty-only save workflow and safe conflict handling

**Files:**

- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/utils/dialogs.ts` only if the existing helper cannot express the approved wording.

- [ ] Add failing tests proving:
  - the fixed save bar is hidden after load;
  - any editable change shows it;
  - tab switching retains the draft and dirty state;
  - a successful save sends the complete draft with `expectedConfigurationVersion`, advances the snapshot/version, hides the bar, and calls `ElMessage.success`;
  - a 409 reloads authoritative data, discards the stale draft only after notifying the operator, and never retries automatically;
  - other failures keep the draft and save bar visible.

- [ ] Run the focused test and confirm failure.

- [ ] Implement `isDirty` from the normalized draft signature and keep one loaded snapshot.

- [ ] Use a dirty-only fixed footer with one primary label: `保存群配置`.

- [ ] Use `ElMessage.success('群配置已保存')` and a stable error message. Do not render stale inline notices from a prior save.

- [ ] On HTTP 409, fetch the latest configuration and show that it was reloaded for review; never submit the stale version again automatically.

- [ ] Run the focused test and commit.

```powershell
git add src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts src/web/wechatrobot-admin/src/utils/dialogs.ts
git commit -m "feat: add safe shared save workflow to group detail"
```

## Task 5: Guard navigation and separate clear-context deletion semantics

**Files:**

- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts`

- [ ] Add failing tests proving:
  - route leave while clean does not open a dialog;
  - route leave while dirty uses `ElMessageBox`, and cancellation blocks navigation;
  - browser `beforeunload` is registered only while dirty and removed on unmount;
  - clear context uses `groupApi.clearContext`, not `updateConfiguration(clearContext: true)`;
  - clear context has its own danger confirmation, updates the returned configuration version, and does not discard unsaved configuration edits.

- [ ] Run the focused test and confirm failure.

- [ ] Add `onBeforeRouteLeave` with the exact warning that unsaved group configuration will be lost.

- [ ] Add and clean up a `beforeunload` handler for browser/tab close.

- [ ] Wire the clear action to:

```ts
await props.api.clearContext(props.id, configurationVersion.value);
```

- [ ] Preserve the draft signature after clearing context; only advance `configurationVersion`.

- [ ] Use `ElMessage.success` to report the number of cleared sessions.

- [ ] Run the focused tests and commit.

```powershell
git add src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts
git commit -m "fix: separate context clearing from group configuration saves"
```

## Task 6: Make run-record filters actually open with the selected group

**Files:**

- Modify: `src/web/wechatrobot-admin/src/router/index.ts`
- Modify: `src/web/wechatrobot-admin/src/views/operations/SendQueueView.vue`
- Test: `src/web/wechatrobot-admin/src/router/index.spec.ts`
- Test: `src/web/wechatrobot-admin/src/views/operations/SendQueueView.spec.ts`
- Verify unchanged: `src/web/wechatrobot-admin/src/views/audit/ConversationAuditView.vue`
- Verify unchanged: `src/web/wechatrobot-admin/src/views/memory/MemoryCenterView.vue`

- [ ] Confirm the existing audit and memory routes already pass `initialGroupId`; preserve that behavior without editing those views.

- [ ] Add failing router and send-queue tests proving `?group=技术群` becomes `initialGroup="技术群"` and is included in the first queue request.

- [ ] Pass `initialGroup` to the send-queue view through router props and initialize its group filter before the first load.

```ts
props: route => ({ initialGroup: String(route.query.group ?? '') })
```

- [ ] Re-run the existing conversation-audit and memory-center selector tests to prove their `groupId` query behavior remains intact and raw UUID entry fields are not reintroduced.

- [ ] Run all changed destination tests and commit only files that actually required changes.

```powershell
Set-Location src/web/wechatrobot-admin
npm test -- --run src/router/index.spec.ts src/views/operations/SendQueueView.spec.ts src/views/audit/ConversationAuditView.spec.ts
```

```powershell
npm test -- --run src/views/memory/MemoryCenterView.spec.ts
```

- [ ] Commit the send-queue query wiring.

```powershell
git add src/web/wechatrobot-admin/src/router/index.ts src/web/wechatrobot-admin/src/router/index.spec.ts src/web/wechatrobot-admin/src/views/operations/SendQueueView.vue src/web/wechatrobot-admin/src/views/operations/SendQueueView.spec.ts
git commit -m "feat: open send queue with the selected group"
```

## Task 7: Frontend and cross-boundary verification

**Files:**

- Review: all files changed in Tasks 1-6
- Review: `docs/superpowers/specs/2026-07-28-group-lifecycle-and-context-management-design.md`

- [ ] Run the focused group API and page tests.

```powershell
Set-Location src/web/wechatrobot-admin
npm test -- --run src/api/groups.spec.ts src/views/groups/groupConfigurationDraft.spec.ts src/views/groups/GroupRulesView.spec.ts
```

- [ ] Run the complete frontend suite, typecheck, and production build.

```powershell
npm run typecheck
npm test -- --run
npm run build
```

- [ ] Return to the repository root and run the group backend integration tests.

```powershell
Set-Location H:\Codex\WechatRobot
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class "*GroupConfigurationTests|*GroupConfigurationMySqlTests"
```

- [ ] Run `git diff --check`, review responsive CSS at desktop and narrow widths, and verify with a real browser that:
  - the tab content does not overflow;
  - the save bar appears only after edits;
  - keyboard focus remains visible;
  - canceling the leave dialog keeps the draft;
  - the page contains no native browser dialog.

- [ ] Inspect the final diff for fabricated platform fields, human-handoff restoration, raw IDs, secrets, and unrelated formatting.

```powershell
git diff --check
git diff --stat
git status --short
```

- [ ] Commit any verification-only fixes, then stop. Do not deploy, push, or merge unless the user separately requests it.
