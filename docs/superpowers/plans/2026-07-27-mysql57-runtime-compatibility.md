# MySQL 5.7 Runtime Compatibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep production on MySQL 5.7.44 while preserving identical application invariants and a repeatable MySQL 5.7/8.x integration-test matrix.

**Architecture:** Enforce provider-independent state invariants at the EF Core save boundary and retain existing database constraints as defense in depth. Remove MySQL-8-only SQL generated or written by tests, then select the Testcontainers image through one environment variable with explicit UTF-8 settings.

**Tech Stack:** .NET 10, ASP.NET Core, EF Core 10, MySql.EntityFrameworkCore, MySQL 5.7.44 and 8.4.10, Testcontainers, xUnit v3.

## Global Constraints

- Do not upgrade the production MySQL server.
- Do not rewrite applied EF Core migrations.
- Preserve all unrelated dirty working-tree changes.
- Keep existing API request validation and database constraints.
- Do not commit, push, deploy, or migrate a real database in this task.

---

### Task 1: Provider-independent persistence invariants

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/WechatRobotDbContext.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Persistence/PersistenceInvariantTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Messaging/FixedReplyPipelineTests.cs`

**Interfaces:**
- Consumes: EF Core tracked `Added` and `Modified` entries.
- Produces: synchronous and asynchronous saves that throw `InvalidOperationException` before SQL for invalid constrained values.

- [x] Write four integration tests, one for each constrained property family.
- [x] Run those tests and confirm MySQL 5.7 exposes the missing application-side validation.
- [x] Add one `ValidatePersistenceInvariants` path used by both `SaveChanges` overload families.
- [x] Run the focused tests on MySQL 5.7 and confirm they pass.

### Task 2: MySQL 5.7-compatible test SQL

**Files:**
- Modify: `tests/server/WechatRobot.IntegrationTests/Messaging/DurableRobotCoordinationTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationMySqlConstraintTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Conversations/InboundGroupRulePipelineTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/WorkTool/WorkToolGlobalRateLimiterTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/WorkTool/CallbackIngestionTests.cs`

**Interfaces:**
- Consumes: fixed local table names and test-generated identifiers.
- Produces: cleanup and failure injection SQL accepted by MySQL 5.7 and 8.x.

- [x] Replace six `ExecuteDeleteAsync` test calls with compatible bounded cleanup.
- [x] Replace the temporary `CHECK` constraint with a uniquely named `BEFORE INSERT` trigger using `SIGNAL SQLSTATE '45000'`.
- [x] Run each affected test class against MySQL 5.7.

### Task 3: Dual-version integration fixture

**Files:**
- Modify: `tests/server/WechatRobot.IntegrationTests/Infrastructure/MySqlFixture.cs`
- Modify: `docs/runbooks/local-development.md` if it exists; otherwise create `docs/runbooks/mysql-integration-test-matrix.md`.

**Interfaces:**
- Consumes: optional `WECHATROBOT_TEST_MYSQL_IMAGE`.
- Produces: one process-wide container using `mysql:8.4.10` by default or the explicitly selected image.

- [x] Add image selection with whitespace-safe fallback to `mysql:8.4.10`.
- [x] Add `--character-set-server=utf8mb4` and `--collation-server=utf8mb4_bin`.
- [x] Document separate-process commands for MySQL 5.7.44 and 8.4.10.

### Task 4: Verification

**Files:**
- Verify only.

**Interfaces:**
- Consumes: the completed implementation.
- Produces: fresh evidence for both supported test images.

- [x] Run `git diff --check`.
- [x] Run the MySQL migration compatibility contract tests.
- [x] Run focused affected integration tests with `mysql:5.7.44`.
- [x] Run the same tests with `mysql:8.4.10`.
- [x] Scan product source for `ExecuteDeleteAsync` and known MySQL-8-only SQL constructs.
- [x] Review the final diff and separate current-task changes from pre-existing changes.
