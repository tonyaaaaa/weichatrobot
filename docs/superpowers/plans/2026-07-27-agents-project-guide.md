# WechatRobot Project Agent Guide Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expand the repository-level `AGENTS.md` into an accurate project
introduction and enforceable operating guide for future agents.

**Architecture:** Keep one concise repository-level guide as the first-layer
execution contract. Put stable project context and high-risk rules directly in
`AGENTS.md`, while linking detailed runbooks and design documents instead of
duplicating them.

**Tech Stack:** Markdown, PowerShell, Git.

## Global Constraints

- Only modify `AGENTS.md` for the implementation.
- Preserve all unrelated working-tree changes.
- Treat `.local` as the source of truth for local runtime configuration.
- Never include values from `.local`, credentials, tokens, connection strings,
  robot identifiers, or callback secrets.
- Use rules verified from the current repository, not assumptions.
- Do not commit, push, deploy, reset, or clean unless the user explicitly asks.

---

### Task 1: Expand the repository Agent guide

**Files:**

- Modify: `AGENTS.md`
- Reference:
  `docs/superpowers/specs/2026-07-27-agents-project-guide-design.md`

**Interfaces:**

- Consumes: Current repository structure, local runtime convention, testing
  projects, runbooks, and existing architecture boundaries.
- Produces: A repository-wide instruction file automatically discoverable by
  Codex-compatible agents.

- [ ] **Step 1: Preserve and expand the current local-startup rules**

  Keep the existing `.local` requirements and organize the final document
  under these top-level sections:

  ```markdown
  # WechatRobot Agent Instructions

  ## Project overview
  ## Technology stack
  ## Repository map
  ## Architecture boundaries
  ## Local startup
  ## Configuration and security
  ## Working tree safety
  ## Backend development
  ## Frontend development
  ## WorkTool integration
  ## Data and migrations
  ## Testing and verification
  ## Documentation
  ## Delivery checklist
  ```

- [ ] **Step 2: Add the verified project overview**

  State that WechatRobot is an AI knowledge-base group-chat platform for
  WeCom/WorkTool scenarios. Describe message ingestion, retrieval, model
  invocation, automated replies, human handoff, knowledge review, and
  administration without claiming unsupported platform capabilities.

- [ ] **Step 3: Add the repository and architecture map**

  Document the responsibilities of Domain, Application, Infrastructure, API,
  Worker, Vue admin, server tests, end-to-end tests, scripts, runbooks, specs,
  and plans. Require dependencies to point inward and keep slow/retryable work
  in the Worker instead of callback request handling.

- [ ] **Step 4: Add safety and external-integration rules**

  Include explicit rules for:

  - checking `git status` and `git worktree list`;
  - preserving unrelated changes;
  - keeping `.local` ignored and secret;
  - preventing credentials and identifiers from entering logs or responses;
  - verifying WorkTool HTTP status and business codes;
  - not treating WorkTool display names as stable identity;
  - checking official capability and real samples before contract changes;
  - avoiding fake frontend CRUD when no backend contract exists.

- [ ] **Step 5: Add development and data rules**

  Require existing dependency-injection, async, cancellation, typed-client,
  timeout, retry, rate-limit, and redaction patterns on the backend. Require
  existing API modules, TypeScript types, Vue components, and Element Plus
  interaction patterns on the frontend. Require EF entities, mappings,
  migrations, and snapshots to stay aligned.

- [ ] **Step 6: Add proportional verification rules**

  Document the verification matrix:

  ```text
  Backend unit: tests/server/WechatRobot.UnitTests
  WorkTool contracts: tests/server/WechatRobot.ContractTests
  Database/API integration: tests/server/WechatRobot.IntegrationTests
  Frontend: npm run typecheck, npm test -- --run, npm run build
  End-to-end: tests/e2e npm test
  Runtime: API liveness, authenticated readiness, Worker heartbeat, web HTTP 200
  Diff hygiene: git diff --check
  ```

  State that a bug fix needs a regression test reproducing the original
  symptom when practical, and that stale binaries or old logs are not
  completion evidence.

- [ ] **Step 7: Add stable documentation links**

  Link only to `docs/runbooks/`, `docs/superpowers/specs/`, and
  `docs/superpowers/plans/`. Do not link `.local`, transient logs, or
  one-off output.

- [ ] **Step 8: Validate the Markdown and repository boundary**

  Run:

  ```powershell
  $path = (Resolve-Path "AGENTS.md").Path
  $text = [IO.File]::ReadAllText($path)
  $lines = [IO.File]::ReadAllLines($path)
  if (@($lines | Where-Object { $_ -match '[ \t]+$' }).Count) {
      throw "AGENTS.md contains trailing whitespace."
  }
  if (-not $text.EndsWith("`n")) {
      throw "AGENTS.md has no final newline."
  }
  if (@($lines | Where-Object { $_ -match '^# ' }).Count -ne 1) {
      throw "AGENTS.md must contain exactly one H1."
  }
  git check-ignore .local/.env
  git diff --check -- AGENTS.md
  ```

  Expected: one H1, no trailing whitespace, a final newline, `.local/.env`
  ignored, and no diff errors.

- [ ] **Step 9: Review scope and report**

  Run:

  ```powershell
  git status --short
  git diff -- AGENTS.md
  ```

  Confirm that the implementation changed only `AGENTS.md`. Report the
  document sections, validation results, and the fact that no commit was made
  unless the user separately requested one.
