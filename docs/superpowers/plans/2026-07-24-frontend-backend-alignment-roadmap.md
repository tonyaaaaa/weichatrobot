# Frontend and Backend Alignment Roadmap

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Correct the WorkTool integration contract first, then close every approved non-handoff frontend/backend gap as independently testable vertical slices.

**Architecture:** WorkTool remains an external connector behind typed application contracts; knowledge, settings, dashboard, identity, queues, and audits remain explicit local capabilities. Each phase must finish its database, backend, frontend, tests, documentation, and acceptance gate before a dependent phase begins.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, Entity Framework Core with MySQL, xUnit v3 with Microsoft Testing Platform, Vue 3, TypeScript, Element Plus, Vitest, Vite.

## Global Constraints

- Work in `H:\Codex\WechatRobot\.worktrees\codex-wechatrobot-mvp` on `codex/wechatrobot-mvp`.
- Treat WorkTool official documentation as the only authority for WorkTool paths, fields, response codes, and external capability claims.
- Do not guess undocumented WorkTool fields for group remarks or group templates.
- An accepted WorkTool command is not an executed command.
- Never expose plaintext WorkTool robot IDs, callback secrets, model keys, OSS keys, OCR keys, authorization headers, or raw secret-bearing URLs.
- Preserve unrelated user changes and inspect `git status --short` before every task.
- Do not stop the running API or Worker unless the user explicitly requests it.
  A stale `--no-build` run is diagnostic only and cannot satisfy an acceptance gate
  for newly changed code.
- Normal automated tests must not call real WorkTool, Enterprise WeChat, OCR, OSS, Qdrant, or model providers.
- Real WorkTool mutation tests require explicit environment switches, target confirmation, and a dedicated test group.
- Use Microsoft Testing Platform filters after `--`; do not use the unsupported traditional `dotnet test --filter` form.
- Human handoff, Enterprise WeChat member sync, `EnterpriseMember`, `GroupMember`, agent selectors, proactive handoff, and handoff pause-policy UI are deferred.

---

## Phase Dependency Map

```text
P0 WorkTool contract correction
  ├─> P1 knowledge tags
  │     └─> P1 knowledge documents
  ├─> P1 robot administration
  └─> P1 dashboard and operational summary

P1 system settings
  └─> P1 dashboard and operational summary

P1 knowledge tags + P1 knowledge documents
  └─> P2 remaining knowledge operations

P0 WorkTool contract correction
  └─> P2 group concurrency and audit filters

P2 user and role administration is independent of handoff mapping
```

## Roadmap State Rules

- `NotStarted`: no implementation task has begun.
- `Planned`: a detailed phase plan exists and passed plan self-review.
- `InProgress`: exactly one phase task is active.
- `Verifying`: implementation is complete and the phase acceptance gate is running.
- `Completed`: all phase acceptance commands passed and the phase was reviewed.
- `Blocked`: an external credential, running-system lock, or user decision prevents safe progress.

Only one phase may be `InProgress` at a time. Update the table whenever a phase changes state.

| Phase | State | Detailed plan | Depends on |
|---|---|---|---|
| P0 WorkTool contract correction | Planned | `docs/superpowers/plans/2026-07-24-worktool-contract-correction.md` | None |
| P1 Knowledge tag closure | NotStarted | Create after P0 acceptance | P0 |
| P1 Knowledge document management | NotStarted | Create after tag acceptance | Tags |
| P1 Typed system settings | NotStarted | Create after document acceptance | None |
| P1 Dashboard and operational summary | NotStarted | Create after settings acceptance | P0, settings |
| P1 Full robot administration UI | NotStarted | Create after dashboard acceptance | P0 |
| P2 User and role administration | NotStarted | Create after robot acceptance | None |
| P2 Remaining knowledge, group concurrency, and audits | NotStarted | Create after user-role acceptance | Tags, documents, P0 |
| Deferred human-handoff member mapping | Deferred | Separate future design | Enterprise WeChat credentials |

## Phase 0: WorkTool Contract Correction

**Deliverable:** WorkTool robot probes, message callback configuration, command-result callbacks, command receipts, execution states, group-name/remark semantics, scripts, frontend status copy, and real acceptance all match documented WorkTool behavior.

**Acceptance gate:**

```powershell
dotnet test tests\server\WechatRobot.ContractTests\WechatRobot.ContractTests.csproj --no-restore -- --filter-namespace 'WechatRobot.ContractTests.WorkTool' --minimum-expected-tests 1
dotnet test tests\server\WechatRobot.UnitTests\WechatRobot.UnitTests.csproj --no-restore
dotnet test tests\server\WechatRobot.IntegrationTests\WechatRobot.IntegrationTests.csproj --no-restore -- --filter-namespace 'WechatRobot.IntegrationTests.WorkTool' --minimum-expected-tests 1
```

If API or Worker binaries are locked, obtain authorization before stopping them;
do not mark this gate complete using stale binaries. Then run:

```powershell
dotnet build WechatRobot.sln --no-restore
Set-Location src\web\wechatrobot-admin
npm run typecheck
npm test -- --run
npm run build
```

## Phase 1: Knowledge Tag Closure

**Deliverable:** List, create, edit, enable, disable, and conditionally delete tags; replace every UUID text field with a tag selector; add optimistic concurrency and administration audits.

**Required invariants:**

- `NormalizedName` remains unique.
- Referenced tags cannot be physically deleted.
- Disabled tags remain visible in historical records.
- Only enabled tags can be newly bound or indexed.
- Every mutation writes a sanitized `AdministrationAuditEntity`.

**Acceptance gate:** tag unit tests, tag endpoint integration tests, group configuration regression tests, frontend tag-view tests, full server build, frontend typecheck/test/build.

## Phase 2: Knowledge Document Management

**Deliverable:** Paginated document list, document detail, version history, upload/parse/OCR/chunk/index states, failed-upload retry, disable, physical-delete request, and navigation back into existing chunk review.

**Required invariants:**

- No API exposes staged file bytes, provider credentials, or signed secret query strings.
- Retry is available only for retryable states.
- Physical delete remains administrator-only and audited.
- Public OSS risk copy remains visible.

**Acceptance gate:** repository query tests, endpoint pagination and authorization tests, lifecycle mutation tests, document-list/detail frontend tests, full build and frontend verification.

## Phase 3: Typed System Settings

**Deliverable:** A typed settings registry, read/update API, optimistic concurrency, version history, rollback, runtime consumers, and a settings page containing only settings that actually affect runtime.

**Required invariants:**

- No arbitrary JSON editor.
- Every key declares type, defaults, validation, runtime consumer, and restart behavior.
- Secret values never enter `system_setting`.
- Rollback creates a new version; history is append-only.

**Acceptance gate:** registry validation tests, persistence/concurrency tests, runtime-consumer tests proving saved values are read, frontend edit/conflict/rollback tests, full build and frontend verification.

## Phase 4: Dashboard and Operational Summary

**Deliverable:** One aggregate admin API and dashboard cards for robots, callback state, knowledge counts, review counts, jobs, send commands, dead letters, and readiness. Handoff statistics remain excluded.

**Required invariants:**

- Database counts and component probes remain independently visible.
- A failed health probe does not erase other dashboard data.
- Required readiness failure preserves HTTP 503 on the readiness endpoint.
- Every card displays its check timestamp or data timestamp.

**Acceptance gate:** aggregate-query tests, partial probe-failure tests, authorization tests, dashboard loading/degraded/empty tests, full build and frontend verification.

## Phase 5: Full Robot Administration

**Deliverable:** Create, edit, enable, disable, rate-limit, rotate robot credential, probe connection, configure message callback, configure command-result callback, query callback state, and display safe metadata.

**Required invariants:**

- Robot IDs and callback secrets are never returned.
- “Configured”, “reachable”, and “online” are separate states.
- Message callback and command-result callback are separate controls.
- Re-enabling a robot does not imply that WorkTool is online.

**Acceptance gate:** credential redaction tests, create/update tests, callback configuration tests, robot page tests, full build and frontend verification.

## Phase 6: User and Role Administration

**Deliverable:** Paginated users, create/invite, enable/disable, add/remove existing roles, last-administrator protection, and administration audit.

**Required invariants:**

- No Enterprise WeChat member fields.
- No handoff agent selector.
- The final enabled administrator cannot be disabled or stripped of the administrator role.
- Password or invitation secrets are never included in audit detail.

**Acceptance gate:** Identity service tests, endpoint authorization and last-admin tests, frontend user-role tests, full build and frontend verification.

## Phase 7: Remaining Knowledge, Group Concurrency, and Audits

**Deliverable:** Delete chunk preview, configure Smart/Separator/Regex/QA chunking, document action buttons, preserve group `ConfigurationVersion`, add conversation group/UTC filters, fetch group-operation audit scope, and expose unified administration audit queries.

**Required invariants:**

- Every group configuration update sends `ExpectedConfigurationVersion`.
- Chunk policy DTOs use discriminated, validated fields.
- UTC filters are inclusive/exclusive as documented and tested at boundary timestamps.
- Handoff pause-policy editing remains deferred.

**Acceptance gate:** chunk policy tests, concurrency conflict tests, audit filter tests, frontend action and conflict tests, full build and frontend verification.

## Per-Phase Completion Procedure

- [ ] Run `git status --short --branch` and record unrelated changes.
- [ ] Write or update the phase design only if implementation discoveries alter an approved contract.
- [ ] Create the next detailed implementation plan with exact files, interfaces, tests, commands, and expected results.
- [ ] Self-review the plan for specification coverage, placeholders, and type consistency.
- [ ] Execute tasks using TDD and one reviewable commit per task.
- [ ] Run the phase acceptance gate.
- [ ] Run `git diff --check`.
- [ ] Review the final diff for secrets, unrelated files, and false capability claims.
- [ ] Mark the phase `Completed` in this roadmap.
- [ ] Generate the next phase plan from the now-current codebase.

## Final Program Acceptance

The program is complete only when every non-deferred phase is `Completed`, the worktree contains no unintended changes, the full solution and frontend production build pass, and the UI has no page that claims a capability which its backend or external connector does not provide.
