# Remove Manual Internal ID Inputs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace every user-editable internal entity ID with a real, permission-correct selector while preserving the existing API write contracts.

**Architecture:** Add narrow read-only option endpoints beside the business APIs whose roles already own each screen. Reuse the existing WorkTool robot list for admin group operations, and add audit-backed option discovery for historical administration filters. Vue pages render searchable Element Plus selectors and continue submitting the existing ID fields.

**Tech Stack:** ASP.NET Core Minimal APIs, EF Core, ASP.NET Core Identity, Vue 3, TypeScript, Element Plus, Vitest, xUnit integration tests.

## Global Constraints

- Users select readable names; the frontend submits internal IDs.
- Do not expose secrets, tokens, callback credentials, or authentication headers in option responses.
- Do not change internal keys, foreign keys, or existing write request field names.
- WorkTool robot IDs, group names, remarks, and member display names remain text inputs because they are external values.
- Preserve unrelated dirty work in `.gitignore`, health checks, Vite proxy configuration, and their tests.

---

### Task 1: Conversation Audit Group Options

**Files:**
- Modify: `src/server/WechatRobot.Api/Audit/ConversationAuditEndpoints.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Conversations/ConversationAuditEndpointTests.cs`
- Modify: `src/web/wechatrobot-admin/src/api/audit.ts`
- Modify: `src/web/wechatrobot-admin/src/api/audit.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/audit/ConversationAuditView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/audit/ConversationAuditView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/task16-operational.spec.ts`

**Interfaces:**
- Produces: `GET /api/audit/group-options`
- Produces: `AuditGroupOption { id, name, workToolGroupRemark, robotName, isEnabled }`
- Consumes: existing `AuditQuery.groupId`

- [ ] **Step 1: Write failing backend authorization and response tests**

Add coverage proving `KnowledgeOperator` can read all registered groups, including disabled groups, while `HumanAgent` is forbidden. Assert that each response object contains only the five option fields.

- [ ] **Step 2: Run the focused backend test and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter FullyQualifiedName~ConversationAuditEndpointTests
```

Expected: FAIL because `/api/audit/group-options` is not mapped.

- [ ] **Step 3: Implement the group options endpoint**

Map the endpoint under the same `KnowledgeOperator` policy as conversation audit. Query `GroupProfiles` joined to `RobotConfigs`, order by group name and ID, and project:

```csharp
new
{
    profile.Id,
    profile.Name,
    profile.WorkToolGroupRemark,
    RobotName = robot.Name,
    profile.IsEnabled
}
```

- [ ] **Step 4: Run the focused backend test and verify GREEN**

Run the command from Step 2 and require zero failures.

- [ ] **Step 5: Write failing frontend API and component tests**

Require `auditApi.groupOptions()` to call `/api/audit/group-options`. Mount the audit page with options including an enabled and disabled group, verify readable labels, select an option, and assert `capability()` receives its ID. Assert no editable text input with the “群 ID” label remains.

- [ ] **Step 6: Run focused frontend tests and verify RED**

Run:

```powershell
npm test -- --run src/api/audit.spec.ts src/views/audit/ConversationAuditView.spec.ts
```

Expected: FAIL because `groupOptions()` and the group selector do not exist.

- [ ] **Step 7: Implement the audit group selector**

Extend `AuditApi` with:

```ts
groupOptions(): Promise<AuditGroupOption[]>;
```

Load options with the audit page, render an `ElSelect` using searchable and clearable behavior, show status in each label, and retain `groupId` as the query field.

- [ ] **Step 8: Run focused frontend tests and verify GREEN**

Run the command from Step 6 and require zero failures.

- [ ] **Step 9: Commit Task 1**

Stage only Task 1 files and commit:

```powershell
git commit -m "feat: select groups in conversation audit"
```

---

### Task 2: Group Operation Robot Selectors

**Files:**
- Modify: `src/web/wechatrobot-admin/src/api/worktool.ts`
- Modify: `src/web/wechatrobot-admin/src/api/worktool.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupOperationsView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupOperationsView.spec.ts`

**Interfaces:**
- Consumes: existing `GET /api/admin/worktool/robots`
- Produces: `WorkToolRobotOption { id, name, isEnabled }`
- Preserves: `registration.robotConfigId` and `operation.robotConfigId`

- [ ] **Step 1: Write failing API and component tests**

Require `listRobots()` on `WorkToolOperationsApi`. Mount the page with enabled and disabled robots, verify both ID text boxes are absent, verify only enabled robots are selectable, and assert register/preview payloads contain the selected ID.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
npm test -- --run src/api/worktool.spec.ts src/views/groups/GroupOperationsView.spec.ts
```

Expected: FAIL because the page does not load robot options and still renders ID inputs.

- [ ] **Step 3: Implement robot option loading and selectors**

Add:

```ts
listRobots(): Promise<WorkToolRobotOption[]>;
```

to the page API using `/api/admin/worktool/robots`. Render two labeled searchable `ElSelect` controls. Disable inactive options, preserve automatic selection when an existing group is chosen, and show an empty-state message when no enabled robot exists.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the command from Step 2 and require zero failures.

- [ ] **Step 5: Commit Task 2**

Stage only Task 2 files and commit:

```powershell
git commit -m "feat: select robots in group operations"
```

---

### Task 3: Handoff Assignee Options

**Files:**
- Modify: `src/server/WechatRobot.Api/Handoffs/HandoffEndpoints.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Handoffs/HandoffReadEndpointTests.cs`
- Modify: `src/web/wechatrobot-admin/src/api/handoffs.ts`
- Create: `src/web/wechatrobot-admin/src/api/handoffs.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/handoffs/HandoffQueueView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/task16-operational.spec.ts`

**Interfaces:**
- Produces: `GET /api/handoffs/assignees`
- Produces: `HandoffAssigneeOption { id, displayName, email, roles, isEnabled }`
- Consumes: existing `assign(id, assigneeUserId, expectedVersion)`

- [ ] **Step 1: Write failing backend tests**

Seed enabled and disabled users with `HumanAgent`, `Admin`, and unrelated roles. Assert a `HumanAgent` caller receives only enabled assignable users and that an unrelated role is forbidden.

- [ ] **Step 2: Run focused backend tests and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter FullyQualifiedName~HandoffReadEndpointTests
```

Expected: FAIL because `/api/handoffs/assignees` is not mapped.

- [ ] **Step 3: Implement the assignee options endpoint**

Use `UserManager<ApplicationUser>` and role membership data to return enabled users in `HumanAgent` or `Admin`, ordered by display name and email. Return only ID, display name, email, roles, and enabled state.

- [ ] **Step 4: Run focused backend tests and verify GREEN**

Run the command from Step 2 and require zero failures.

- [ ] **Step 5: Write failing frontend tests**

Require `handoffApi.assignees()` and assert the queue page renders readable assignee labels, preserves a historical disabled assignment as a disabled option, and sends the chosen ID to `assign()`.

- [ ] **Step 6: Run focused frontend tests and verify RED**

Run:

```powershell
npm test -- --run src/api/handoffs.spec.ts src/views/task16-operational.spec.ts
```

Expected: FAIL because the assignee endpoint and selector do not exist.

- [ ] **Step 7: Implement the assignee selector**

Load assignees on mount, merge the current historical assignee into the visible option set when necessary, replace the ID input with searchable `ElSelect`, and update validation copy to “请选择客服”.

- [ ] **Step 8: Run focused frontend tests and verify GREEN**

Run the command from Step 6 and require zero failures.

- [ ] **Step 9: Commit Task 3**

Stage only Task 3 files and commit:

```powershell
git commit -m "feat: select handoff assignees"
```

---

### Task 4: Administration Audit Filter Options

**Files:**
- Modify: `src/server/WechatRobot.Api/Audit/AdministrationAuditEndpoints.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Operations/AdministrationAuditEndpointTests.cs`
- Modify: `src/web/wechatrobot-admin/src/api/administrationAudit.ts`
- Modify: `src/web/wechatrobot-admin/src/api/administrationAudit.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/audit/AdministrationAuditView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/audit/AdministrationAuditView.spec.ts`

**Interfaces:**
- Produces: `GET /api/admin/administration-audits/filter-options`
- Query: `targetType?: string`, `q?: string`
- Produces: `AdministrationAuditFilterOptions { actors, actions, targetTypes, targets }`
- Produces: `AdministrationAuditTargetOption { targetType, targetId, label }`

- [ ] **Step 1: Write failing backend tests**

Seed repeated and historical audit rows. Assert options are distinct, deterministically ordered, admin-only, and target results are filtered by `targetType` and bounded to 50. Assert no sanitized detail JSON is returned.

- [ ] **Step 2: Run focused backend tests and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter FullyQualifiedName~AdministrationAuditEndpointTests
```

Expected: FAIL because the filter options route does not exist.

- [ ] **Step 3: Implement bounded audit option queries**

Map `/filter-options` before the `/{id}`-style routes. Query distinct actors, actions, and target types. When `targetType` is supplied, query distinct matching target IDs, optionally filter by `q`, sort, and take 50. Build a safe label from target type and a shortened ID without returning details.

- [ ] **Step 4: Run focused backend tests and verify GREEN**

Run the command from Step 2 and require zero failures.

- [ ] **Step 5: Write failing frontend tests**

Require `filterOptions()` in the API client. Verify actor, action, target type, and target are selectors; changing target type clears the previous target and reloads target options; applying filters sends the selected exact values.

- [ ] **Step 6: Run focused frontend tests and verify RED**

Run:

```powershell
npm test -- --run src/api/administrationAudit.spec.ts src/views/audit/AdministrationAuditView.spec.ts
```

Expected: FAIL because option loading and selectors are absent.

- [ ] **Step 7: Implement linked administration audit selectors**

Load base options on mount. Use searchable clearable `ElSelect` controls for actor, action, and target type. Reload target options when target type or remote search changes, clear stale target IDs, and keep date filters unchanged.

- [ ] **Step 8: Run focused frontend tests and verify GREEN**

Run the command from Step 6 and require zero failures.

- [ ] **Step 9: Commit Task 4**

Stage only Task 4 files and commit:

```powershell
git commit -m "feat: select administration audit filters"
```

---

### Task 5: Full Regression and UI Contract Verification

**Files:**
- Modify: `tests/e2e/test-server.mjs`
- Modify: `tests/e2e/server-fixtures.mjs`
- Modify: `tests/e2e/admin-workflows.spec.ts`

**Interfaces:**
- Consumes all option endpoints and selectors from Tasks 1-4.
- Produces a verified guarantee that no editable internal ID input remains.

- [ ] **Step 1: Add or update E2E fixtures**

Serve deterministic group, robot, assignee, and administration audit option responses. Update workflows to choose readable labels rather than fill UUIDs.

- [ ] **Step 2: Run full frontend verification**

Run:

```powershell
npm test -- --run
npm run typecheck
npm run build
```

All commands must exit zero.

- [ ] **Step 3: Run relevant backend integration tests**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~ConversationAuditEndpointTests|FullyQualifiedName~HandoffReadEndpointTests|FullyQualifiedName~AdministrationAuditEndpointTests|FullyQualifiedName~GroupOperationEndpointTests"
```

Require zero failures.

- [ ] **Step 4: Scan the rendered source contract**

Run:

```powershell
rg -n -S '群 ID<input|机器人配置 ID.*<input|客服用户 ID|目标 ID<input' src/web/wechatrobot-admin/src -g '*.vue'
```

Expected: no matches. Confirm WorkTool credential inputs remain.

- [ ] **Step 5: Verify the local page**

Reload the running frontend and inspect `/audit` as `KnowledgeOperator`,
`/groups/operations` and `/administration-audit` as `Admin`, and `/handoffs` as
`HumanAgent`. Confirm selectors render readable labels and submit without
hand-entered UUIDs.

- [ ] **Step 6: Check repository hygiene**

Run:

```powershell
git diff --check
git status --short
```

Confirm only intended task changes plus the user’s pre-existing unrelated modifications remain.

- [ ] **Step 7: Commit final fixture or regression adjustments**

If Task 5 changed files, stage only those files and commit:

```powershell
git commit -m "test: cover internal id selectors"
```
