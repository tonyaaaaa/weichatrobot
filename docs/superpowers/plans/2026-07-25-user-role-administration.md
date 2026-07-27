# User and Role Administration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the user-and-role placeholder with an administrator-only, audited account-management workflow covering pagination, account creation, enable/disable, and assignment of the three existing system roles.

**Architecture:** Keep ASP.NET Core Identity as the only account and role source of truth. Add an explicit `ApplicationUser.IsEnabled` flag rather than overloading temporary lockout state. Put mutations in a scoped service using `UserManager` and `RoleManager`, expose thin Minimal API endpoints, and write sanitized records to the existing `administration_audit` table. The frontend uses a typed API client and never receives or displays password hashes, security stamps, invitation tokens, or other Identity internals.

**Tech Stack:** .NET 10, ASP.NET Core Identity, Minimal APIs, EF Core/MySQL, xUnit integration tests, Vue 3, TypeScript, Element Plus, Vitest.

## Global Constraints

- Only an authenticated `Admin` may list or mutate users and roles.
- This phase does not add enterprise-WeChat member fields, automatic member synchronization, or a handoff assignee selector.
- Only `Admin`, `KnowledgeOperator`, and `HumanAgent` are assignable.
- Email addresses are normalized through Identity; duplicate accounts are rejected by Identity.
- New users receive an administrator-entered temporary password that must satisfy the existing password policy. The password is write-only and never returned or audited.
- Disabled users cannot log in. Existing authenticated principals for a disabled user are rejected when Identity-backed validation runs.
- At least one enabled administrator must remain after any disable or role-removal operation.
- An administrator cannot remove the last enabled administrator role through a concurrent race; the service rechecks within the mutation transaction boundary supported by the store and returns a conflict when the invariant would be broken.
- Administration audit detail contains only safe field names, role names, enabled state, display name, and email; it never contains passwords, password hashes, security stamps, reset tokens, or bearer tokens.
- Pagination uses the repository-wide normalized page/page-size behavior.

---

### Task 1: Identity Enable State and Management Service

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/Identity/ApplicationUser.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Configurations/ApplicationUserConfiguration.cs`
- Create: `src/server/WechatRobot.Infrastructure/Identity/UserAdministrationService.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/*AddApplicationUserEnabledState.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/WechatRobotDbContextModelSnapshot.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Identity/UserAdministrationServiceTests.cs`

- [ ] **Step 1: Write failing service tests**

Cover paginated listing, password-policy failures, successful creation with existing roles, disable/enable, role add/remove, unknown-role rejection, and last-enabled-administrator protection.

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test tests\server\WechatRobot.IntegrationTests\WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class WechatRobot.IntegrationTests.Identity.UserAdministrationServiceTests
```

Expected: FAIL because `IsEnabled` and the service do not exist.

- [ ] **Step 3: Implement the minimum service**

Use `UserManager<ApplicationUser>` and `RoleManager<IdentityRole<Guid>>` for all Identity mutations. Persist safe `AdministrationAuditEntity` rows in the same scoped database context. Return explicit not-found, validation, and invariant-conflict outcomes without leaking Identity internals.

- [ ] **Step 4: Generate the migration and verify GREEN**

Run the Step 2 command.

Expected: PASS.

### Task 2: Administrator API and Disabled-User Authentication

**Files:**
- Create: `src/server/WechatRobot.Api/Users/UserAdministrationEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Auth/AuthEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Program.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Identity/UserAdministrationEndpointTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Auth/AuthEndpointTests.cs`

**API contract:**

```text
GET   /api/admin/users?page=&pageSize=&q=&state=
POST  /api/admin/users
PUT   /api/admin/users/{id}/enabled
PUT   /api/admin/users/{id}/roles
GET   /api/admin/users/roles
```

- [ ] **Step 1: Write failing endpoint tests**

Assert anonymous `401`, non-admin `403`, safe paginated payloads, role catalogue output, create validation, enable mutation, role mutation, and `409` for last-administrator violations. Assert response and audit JSON never contain the temporary password.

- [ ] **Step 2: Run focused tests and verify RED**

```powershell
dotnet test tests\server\WechatRobot.IntegrationTests\WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class WechatRobot.IntegrationTests.Identity.UserAdministrationEndpointTests --filter-class WechatRobot.IntegrationTests.Auth.AuthEndpointTests
```

- [ ] **Step 3: Implement endpoints and authentication checks**

Map the group under `/api/admin/users`, require the `Admin` policy, use ProblemDetails-compatible status codes, and reject disabled users during login/current-user resolution. Do not add enterprise-member fields.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the Step 2 command.

Expected: PASS.

### Task 3: Typed Frontend API and User/Role Page

**Files:**
- Create: `src/web/wechatrobot-admin/src/api/users.ts`
- Create: `src/web/wechatrobot-admin/src/api/users.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/users/UserRolesView.vue`
- Create: `src/web/wechatrobot-admin/src/views/users/UserRolesView.spec.ts`

- [ ] **Step 1: Write failing client and page tests**

Cover exact API paths and payloads, pagination/filter reset, safe account creation, role checkboxes, enable/disable confirmation, last-admin conflict messaging, and absence of the old placeholder text.

- [ ] **Step 2: Run and verify RED**

```powershell
Set-Location src\web\wechatrobot-admin
npm test -- --run src/api/users.spec.ts src/views/users/UserRolesView.spec.ts
```

- [ ] **Step 3: Implement the typed client and page**

Use write-only password inputs with autocomplete disabled, per-row busy state, explicit confirmations, and readable API validation/invariant errors.

- [ ] **Step 4: Run and verify GREEN**

Run the Step 2 command.

Expected: PASS.

### Task 4: Phase Acceptance

**Files:**
- Modify: `docs/superpowers/plans/2026-07-24-frontend-backend-alignment-roadmap.md`
- Modify: `docs/superpowers/plans/2026-07-25-user-role-administration.md`

- [ ] **Step 1: Run focused server and frontend tests**
- [ ] **Step 2: Run frontend typecheck, full tests, and production build**
- [ ] **Step 3: Run solution build and bounded full server tests**
- [ ] **Step 4: Search API/frontend/audit output for password and Identity-secret exposure**
- [ ] **Step 5: Record exact evidence and mark Phase 6 complete only if its acceptance gate passes**

## Completion Evidence — 2026-07-25

- Identity service and administrator endpoint acceptance set: 17 passed, 0 failed.
- Dedicated user-administration service and endpoint tests: 8 passed, 0 failed.
- Frontend full suite: 103 passed across 29 files, 0 failed.
- Frontend typecheck: passed.
- Frontend production build: passed.
- Full solution build: passed with 0 warnings and 0 errors.
- `git diff --check`: passed; only existing line-ending conversion warnings were reported.
- Secret review found the temporary password only in write-only request/input paths. It is absent from response DTOs and sanitized administration audits.
- Enterprise-WeChat member synchronization and the handoff assignee selector remain deferred and were not added to this phase.
