# Chunk Preview Workflow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Support atomic merging of two or more contiguous previews and make approved/indexed versions visibly read-only.

**Architecture:** Extend the editor and merge request to accept an ordered ID set, with one repository mutation and revision increment. Load exact version status in the Vue page and derive all mutation availability from that status.

**Tech Stack:** .NET 10, EF Core, xUnit, Vue 3, TypeScript, Vitest.

## Global Constraints

- Multi-merge requires at least two contiguous previews.
- A failed validation must produce no partial merge.
- User-visible segment numbering starts at one.
- Only uploaded and preview versions are mutable.

---

### Task 1: Atomic backend multi-merge

**Files:**
- Modify: `src/server/WechatRobot.Application/Knowledge/Chunking/ChunkingService.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Knowledge/ChunkPreviewRepository.cs`
- Modify: `src/server/WechatRobot.Api/Knowledge/ChunkPreviewEndpoints.cs`
- Test: `tests/server/WechatRobot.UnitTests/Knowledge/Chunking/ChunkingServiceTests.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Knowledge/ChunkPreviewPersistenceTests.cs`

- [ ] Write failing tests for three contiguous items, non-contiguous rejection, metadata rejection and overlap removal.
- [ ] Run focused tests and verify the two-ID API cannot satisfy them.
- [ ] Implement `Merge(previews, previewIds)` and a `PreviewIds` request contract.
- [ ] Run focused tests and verify one revision increment and no partial writes.

### Task 2: Frontend selection and read-only state

**Files:**
- Modify: `src/web/wechatrobot-admin/src/api/knowledge.ts`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/DocumentDetailView.vue`
- Test: `src/web/wechatrobot-admin/src/views/knowledge/DocumentDetailView.spec.ts`
- Test: `src/web/wechatrobot-admin/src/views/task16-operational.spec.ts`

- [ ] Write failing tests for three-item merge payload, one-based labels and an indexing version with disabled mutations.
- [ ] Run focused tests and verify current two-item and always-editable behavior fails.
- [ ] Fetch current version status, compute contiguous selection, and send `previewIds`.
- [ ] Render the read-only explanation and disable every preview mutation control.
- [ ] Run focused and full frontend tests.
