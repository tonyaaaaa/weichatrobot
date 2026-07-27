# Shared Production .env Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the API and Worker load one shared production `.env` directly at process startup and package a documented example for IIS deployment.

**Architecture:** A dependency-free loader in Infrastructure resolves the shared file, parses a deliberately small `.env` grammar, and writes missing values into the process environment before either .NET host is built. Existing machine/process variables win over file values.

**Tech Stack:** .NET 10, ASP.NET Core Minimal API, .NET Generic Host, xUnit, IIS.

## Global Constraints

- Default deployment path is `C:\wxrobot\config\.env`.
- `WECHATROBOT_ENV_FILE` can override the path.
- Environment variables override `.env`; `.env` overrides JSON defaults.
- No secret values are logged or committed.
- API and Worker must load the file before their host builders are created.
- The parser does not execute shell syntax or expand variables.

---

### Task 1: Shared Loader

**Files:**

- Create: `tests/server/WechatRobot.UnitTests/Configuration/DotEnvFileLoaderTests.cs`
- Create: `src/server/WechatRobot.Infrastructure/Configuration/DotEnvFileLoader.cs`

- [x] Write tests for basic parsing, `=` and `#` in values, quoted values,
      environment precedence, duplicate rejection, malformed lines, explicit
      missing paths, and `DefaultPath == C:\wxrobot\config\.env`.
- [x] Run the focused tests and confirm they fail because the loader is absent.
- [x] Implement `DotEnvFileLoader.Load(string? defaultPath = null)`.
- [x] Re-run the focused tests and confirm they pass.

### Task 2: Host Wiring and Example

**Files:**

- Modify: `src/server/WechatRobot.Api/Program.cs`
- Modify: `src/server/WechatRobot.Worker/Program.cs`
- Create: `tests/server/WechatRobot.ContractTests/Configuration/DotEnvHostWiringTests.cs`
- Create: `deploy/windows/wechatrobot.env.example`

- [x] Write a source contract test proving both programs call
      `DotEnvFileLoader.Load()` before host creation.
- [x] Run it and confirm failure.
- [x] Wire both hosts and add the fully commented example containing the union
      of API and Worker settings.
- [x] Run unit and contract tests.

### Task 3: Deployment Package

**Files:**

- Modify: `docs/runbooks/iis-test-deployment-wxrobot.aavisa.com.md`
- Regenerate: `artifacts/WechatRobot-IIS-wxrobot.aavisa.com-20260725.zip`

- [x] Document creation, ACLs, precedence, restart order, Qdrant private URL,
      and OSS `PublicBaseUrl`.
- [x] Publish API and Worker Release outputs.
- [x] Add `config/.env.example` and the updated runbook to the release tree.
- [x] Rebuild the ZIP and verify required entries.
- [x] Run final tests, builds, secret scan, whitespace check, and Git status.
