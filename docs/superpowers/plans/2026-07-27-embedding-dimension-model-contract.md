# Embedding Dimension Model Contract Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make vector dimension a tested Embedding-model contract and bind every index job to the exact model configuration that created it.

**Architecture:** Extend the model entity and public DTOs with an optional embedding dimension, validate it by configuration type, and include it in the connection-test fingerprint. Queue index jobs with model ID/version/dimension snapshots; Worker loads that exact model and rejects drift.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, EF Core/MySQL, xUnit, Vue 3, TypeScript, Element Plus.

## Global Constraints

- Chat configurations must persist a null dimension.
- Embedding configurations require a positive dimension.
- A dimension mismatch must fail connection testing without exposing provider response data.
- Index jobs must never switch model configuration after they are queued.

---

### Task 1: Model persistence and API contract

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/ModelConfigEntity.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Models/ModelConfigurationManager.cs`
- Modify: `src/server/WechatRobot.Application/Models/ModelConfigurationService.cs`
- Modify: `src/server/WechatRobot.Api/Models/ModelConfigurationEndpoints.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/20260727080000_AddEmbeddingDimensionAndIndexModelSnapshot.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationEndpointTests.cs`

**Interfaces:**
- Produces: `int? EmbeddingDimension` on model entity, commands, records and responses.
- Produces: validation error key `embeddingDimension`.

- [ ] Write API tests proving chat rejects a dimension, embedding requires a positive dimension, and responses round-trip 1024.
- [ ] Run the focused tests and verify they fail because the field is absent.
- [ ] Add the entity, DTO, command, validation, response and fingerprint changes.
- [ ] Generate the EF Core migration and verify the snapshot.
- [ ] Run the focused tests and verify they pass.

### Task 2: Connection-test dimension validation

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/Models/ModelConfigurationManager.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationEndpointTests.cs`

**Interfaces:**
- Consumes: `EmbeddingDimension`.
- Produces: failure summary `dimension_mismatch_expected_{expected}_actual_{actual}`.

- [ ] Write a failing test where an expected 1536 configuration receives one 1024-length vector.
- [ ] Run the test and verify current code incorrectly succeeds.
- [ ] Compare the returned vector length, persist the stable mismatch summary, and invalidate the tested fingerprint.
- [ ] Run mismatch, success and enable/default tests.

### Task 3: Index-job model snapshot

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/KnowledgeIndexJobEntity.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs`
- Modify: `src/server/WechatRobot.Application/Knowledge/IKnowledgeService.cs`
- Modify: `src/server/WechatRobot.Application/Knowledge/KnowledgeIndexService.cs`
- Modify: `src/server/WechatRobot.Api/Program.cs`
- Modify: `src/server/WechatRobot.Worker/Program.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Knowledge/QdrantKnowledgeTests.cs`
- Test: `tests/server/WechatRobot.UnitTests/Knowledge/KnowledgeIndexServiceTests.cs`

**Interfaces:**
- Produces on a job: `ModelConfigurationId`, `ModelConfigurationVersion`, `Dimension`.
- Changes `KnowledgeIndexOptions` to exclude `Dimension`.
- Changes `LoadEmbeddingConfigurationAsync` to accept the queued model ID and version.

- [ ] Write failing tests proving queue dimension comes from the selected Embedding model and Worker rejects configuration drift.
- [ ] Run focused tests and verify the fixed 1536 options cause failure.
- [ ] Snapshot model identity/version/dimension when queueing and load the exact configuration in Worker.
- [ ] Remove global dimension binding and derive collection names from job dimension plus configured distance.
- [ ] Run unit and integration index suites.

### Task 4: Frontend model field

**Files:**
- Modify: `src/web/wechatrobot-admin/src/api/models.ts`
- Modify: `src/web/wechatrobot-admin/src/views/models/ModelConfigurationDialog.vue`
- Modify: `src/web/wechatrobot-admin/src/views/models/ModelSettingsView.vue`
- Test: `src/web/wechatrobot-admin/src/views/models/ModelConfigurationDialog.spec.ts`
- Test: `src/web/wechatrobot-admin/src/views/models/ModelSettingsView.spec.ts`

**Interfaces:**
- Produces: `embeddingDimension?: number | null` in API models and drafts.

- [ ] Write failing tests for conditional field visibility, required validation and list display.
- [ ] Run focused Vitest tests and verify failure.
- [ ] Add the Element Plus numeric input and submit null for chat configurations.
- [ ] Display the configured dimension and actual mismatch summary in plain Chinese.
- [ ] Run focused and full frontend tests.

### Task 5: Local migration and recovery

**Files:**
- Modify local database through EF migration.
- Modify current model through the admin API.

- [ ] Stop API and Worker, build the solution, and start API with migrations enabled.
- [ ] Update the current Embedding configuration to 1024 and run connection testing.
- [ ] Restart Worker, submit a new index job rather than retrying the 1536 snapshot.
- [ ] Poll until the job is active or a terminal failure is recorded.
- [ ] Verify API, Worker, Qdrant and frontend health.
