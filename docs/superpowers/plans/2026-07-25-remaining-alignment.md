# Remaining Knowledge, Concurrency, and Audit Alignment Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish every non-deferred frontend/backend alignment item after user administration: advanced chunk preview operations, mandatory group-configuration concurrency, operational audit filters/scope, and a unified administration-audit query surface.

**Architecture:** Reuse the existing chunking engine, document actions, group configuration version, conversation query, WorkTool audit-scope endpoint, and `administration_audit` table. Add only the missing contracts and UI controls. Chunk-policy requests become explicit discriminated DTOs at the API boundary and are mapped to the existing application policy. Group writes require a version on every request. Audit queries are read-only, paginated, authorized, UTC-bounded, and return sanitized detail only.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, EF Core/MySQL, Vue 3, TypeScript, Element Plus, xUnit, Vitest.

## Confirmed Existing Capabilities

- The backend already implements preview deletion.
- Document upload retry, disable, asynchronous physical deletion, and their management UI already exist and are tested.
- The chunking engine already implements Smart, Separator, Regex, and QA behavior.
- The backend conversation audit query already applies `fromUtc <= createdAtUtc < toUtc`.
- The WorkTool backend already exposes `/api/admin/worktool/group-operations/audit-scope`.
- Group responses already contain `configurationVersion`; only the frontend and mandatory write contract are incomplete.

## Global Constraints

- Do not add handoff pause-policy editing, enterprise-member synchronization, or assignee mapping.
- Do not invent WorkTool operations or audit guarantees; display the backend-provided scope text.
- Every group-configuration write requires `expectedConfigurationVersion`.
- A `409 group-configuration-conflict` reloads the latest server configuration and informs the operator.
- Chunk policy uses a TypeScript discriminated union and an API discriminated DTO. Irrelevant policy fields are rejected rather than silently ignored.
- Smart/Separator/Regex limits remain enforced by the existing chunking engine; QA entry count and content remain bounded.
- Conversation time filters are UTC instants with an inclusive start and exclusive end.
- Unified administration audit output returns only `SanitizedDetailJson`; it never joins or returns credentials, hashes, tokens, request authorization, or raw provider payloads.

---

### Task 1: Advanced Chunk Policies and Preview Deletion

**Files:**
- Modify: `src/server/WechatRobot.Api/Knowledge/ChunkPreviewEndpoints.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Knowledge/ChunkPreviewEndpointTests.cs`
- Modify: `src/web/wechatrobot-admin/src/api/knowledge.ts`
- Modify: `src/web/wechatrobot-admin/src/api/knowledgeDocuments.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/DocumentDetailView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentManagementView.spec.ts`
- Create or modify: `src/web/wechatrobot-admin/src/views/knowledge/DocumentDetailView.spec.ts`

- [x] Write failing endpoint tests for all four policy kinds, invalid discriminators/fields, and deletion.
- [x] Replace direct application-record binding with explicit policy request DTO mapping.
- [x] Write failing typed-client tests for policy payloads and encoded delete routes.
- [x] Add policy controls and a confirmed delete button to the version-detail page.
- [x] Run focused server/frontend tests and verify GREEN.

### Task 2: Mandatory Group Configuration Concurrency

**Files:**
- Modify: `src/server/WechatRobot.Api/Groups/GroupEndpoints.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Groups/GroupConfigurationTests.cs`
- Modify: `src/web/wechatrobot-admin/src/api/groups.ts`
- Create: `src/web/wechatrobot-admin/src/api/groups.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts`

- [x] Write failing backend tests proving a missing/stale version is rejected and a current version increments.
- [x] Require `ExpectedConfigurationVersion` on every update.
- [x] Write failing client/view tests proving the loaded version is sent on normal save and clear-context save.
- [x] Reload current server state on conflict and preserve an explicit conflict notice.
- [x] Run focused tests and verify GREEN.

### Task 3: Conversation Filters and WorkTool Audit Scope

**Files:**
- Modify: `src/web/wechatrobot-admin/src/api/audit.ts`
- Create: `src/web/wechatrobot-admin/src/api/audit.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/audit/ConversationAuditView.vue`
- Create or modify: `src/web/wechatrobot-admin/src/views/audit/ConversationAuditView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/api/worktool.ts`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupOperationsView.vue`
- Modify: corresponding WorkTool frontend tests

- [x] Write failing client tests for `groupId`, `fromUtc`, `toUtc`, pagination, and audit-scope.
- [x] Add group UUID and local datetime controls that serialize to UTC ISO instants.
- [x] Document and render the inclusive-start/exclusive-end boundary.
- [x] Replace hard-coded WorkTool scope copy with the backend response.
- [x] Run existing server boundary tests plus focused frontend tests.

### Task 4: Unified Administration Audit Query and Page

**Files:**
- Create: `src/server/WechatRobot.Api/Audit/AdministrationAuditEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Program.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Operations/AdministrationAuditEndpointTests.cs`
- Create: `src/web/wechatrobot-admin/src/api/administrationAudit.ts`
- Create: `src/web/wechatrobot-admin/src/api/administrationAudit.spec.ts`
- Create: `src/web/wechatrobot-admin/src/views/audit/AdministrationAuditView.vue`
- Create: `src/web/wechatrobot-admin/src/views/audit/AdministrationAuditView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/router/index.ts`
- Modify: `src/web/wechatrobot-admin/src/router/index.spec.ts`

- [x] Write failing authorization/filter/boundary/redaction endpoint tests.
- [x] Implement admin-only pagination with action, target type/id, actor, and UTC filters.
- [x] Return parsed sanitized detail while applying defensive redaction again at the boundary.
- [x] Add an Admin-only “管理审计” route/page with filters and sanitized detail rendering.
- [x] Run focused server/frontend tests and verify GREEN.

### Task 5: Final Program Acceptance

- [x] Run all Phase 7 focused tests.
- [x] Run server unit, contract, and bounded integration suites.
- [x] Run frontend full tests, typecheck, and production build.
- [x] Run full solution build and `git diff --check`.
- [x] Review routes/UI for false WorkTool or enterprise-member capability claims.
- [x] Record exact evidence and mark Phase 7 and the non-deferred roadmap complete.

## Completion Evidence — 2026-07-25

- Phase 7 focused server suites: 23 passed, 0 failed.
- Server unit tests: 218 passed, 0 failed.
- Server contract tests: 62 passed, 0 failed.
- Full server integration suite: 285 passed, 3 explicit external-credential acceptance tests skipped, 0 failed.
- Frontend tests: 114 passed across 36 files, 0 failed.
- Frontend typecheck and production build: passed.
- Full solution build: 0 warnings, 0 errors.
- Final `git diff --check`: passed.
- Capability review retained the explicit deferral of Enterprise WeChat member synchronization, assignee mapping, and handoff pause-policy editing. WorkTool send-command tests now distinguish HTTP acceptance from correlated execution completion.

During the full integration gate, two stale test assumptions were corrected without weakening product behavior: robot enable tests now obtain the required successful-probe confirmation and do not resend credentials during ordinary state changes; rule-only fixtures remove their unconsumed durable jobs; and fixed-reply acceptance remains `accepted` until a correlated WorkTool result callback completes it.
