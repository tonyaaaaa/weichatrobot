# Global Knowledge Tag Consolidation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Merge all ordinary tags named “全局知识” into the canonical system tag and ensure private-chat ingestion and tag administration can never create that duplicate again.

**Architecture:** Add a focused infrastructure repair service that runs after database initialization, resolves the canonical tag by `SystemKind`, migrates relational and JSON tag references, and removes duplicate rows idempotently. Centralize the system tag identity rules so private ingestion and tag administration use the same canonical lookup and reserved display-name behavior.

**Tech Stack:** ASP.NET Core 10, EF Core, MySQL 5.7, xUnit v3/Microsoft Testing Platform.

## Global Constraints

- Preserve unrelated uncommitted frontend changes.
- Do not connect to or mutate an unconfirmed production database.
- Keep MySQL 5.7 compatibility.
- The canonical global tag is identified by `SystemKind = "GlobalKnowledge"`; its display name is “全局知识”.
- Existing group, chunk, review, index-job, and private-ingest references must survive consolidation.
- The repair must be idempotent.
- Do not commit or push unless the user explicitly requests it.

---

### Task 1: Consolidate Existing Duplicate Tags

**Files:**
- Create: `src/server/WechatRobot.Infrastructure/Knowledge/GlobalKnowledgeTag.cs`
- Create: `src/server/WechatRobot.Infrastructure/Knowledge/GlobalKnowledgeTagRepairService.cs`
- Modify: `src/server/WechatRobot.Api/Program.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Knowledge/GlobalKnowledgeTagRepairServiceTests.cs`

**Interfaces:**
- Consumes: `WechatRobotDbContext` and the existing knowledge-tag reference entities.
- Produces: `GlobalKnowledgeTagRepairService.RepairAsync(CancellationToken)` and canonical tag constants/helpers.

- [ ] **Step 1: Write the failing consolidation test**

Create a canonical system tag and an ordinary duplicate named “全局知识”. Add references to both IDs through group bindings, chunk bindings, review JSON, index-job JSON, and private-ingest JSON. Assert that repair leaves one canonical tag and replaces/de-duplicates every reference.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -c Release -- --filter-class WechatRobot.IntegrationTests.Knowledge.GlobalKnowledgeTagRepairServiceTests
```

Expected: compilation or assertion failure because the repair service does not exist.

- [ ] **Step 3: Implement the idempotent repair**

Implement `RepairAsync` so it:

1. resolves or creates the canonical `SystemKind = "GlobalKnowledge"` row;
2. finds non-system rows whose trimmed display name normalizes to “全局知识”;
3. replaces duplicate IDs in group and chunk join tables without creating duplicate composite keys;
4. parses and rewrites review, index-job, and private-ingest JSON arrays with distinct canonical IDs;
5. removes duplicate tag rows;
6. writes one sanitized administration audit only when data changed;
7. succeeds without additional changes on a second call.

- [ ] **Step 4: Invoke repair after database initialization**

Register the repair service in API dependency injection and invoke it after `MigrateAsync`/`EnsureCreatedAsync`, before Identity seeding and endpoint startup.

- [ ] **Step 5: Run the focused test and verify GREEN**

Run the same focused integration-test command and confirm all consolidation assertions pass.

### Task 2: Fix Private-Chat Tag Resolution and Reserved-Name Administration

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/Agents/PrivateKnowledgeIngestProcessor.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeTagManager.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateKnowledgeIngestPipelineTests.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeTagManagerTests.cs`

**Interfaces:**
- Consumes: canonical global-tag helpers from Task 1.
- Produces: exact explicit tag resolution that maps “全局知识” to the system tag and administration conflict behavior for the reserved name.

- [ ] **Step 1: Write the failing private-ingest regression test**

Seed only the canonical system tag, return a proposal with explicit tag “全局知识”, process the private ingest job, and assert no ordinary duplicate is created and the staged/indexed tag ID is canonical.

- [ ] **Step 2: Write the failing administration regression test**

Seed the canonical system tag, attempt to create an ordinary tag named “全局知识”, and assert `NameConflict` points to the canonical row.

- [ ] **Step 3: Run both focused test classes and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -c Release -- --filter-class WechatRobot.IntegrationTests.PrivateChat.PrivateKnowledgeIngestPipelineTests WechatRobot.IntegrationTests.Knowledge.KnowledgeTagManagerTests
```

Expected: the private-ingest test finds two tags, and the administration test incorrectly succeeds.

- [ ] **Step 4: Implement shared canonical resolution**

Change private ingestion to:

- load the canonical tag by `SystemKind`;
- map explicit “全局知识” directly to that ID;
- exact-match/create other explicit tags;
- use the Agent-suggested enabled tag only when no explicit tags were supplied;
- fall back to the canonical tag when no enabled tag resolves.

Change tag administration to treat “全局知识” as reserved and return the canonical row as the conflict target.

- [ ] **Step 5: Run both focused test classes and verify GREEN**

Run the same focused integration-test command and confirm both regressions pass.

### Task 3: Verification

**Files:**
- Review: all task files and the pre-existing dirty-tree files separately.

**Interfaces:**
- Consumes: completed Tasks 1 and 2.
- Produces: fresh build/test evidence and a scoped diff.

- [ ] **Step 1: Run the complete backend integration suite**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj
```

- [ ] **Step 2: Run backend unit tests**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj
```

- [ ] **Step 3: Build the solution and check the diff**

```powershell
dotnet build WechatRobot.slnx
git diff --check
git status --short
```

- [ ] **Step 4: Report deployment boundary**

State clearly that source and tests are complete, but no production database was touched. The deployed API must restart once so the idempotent repair runs against its configured database.
