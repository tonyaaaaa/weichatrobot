# Local Configuration Readiness Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make OSS readiness accept the same optional `PublicBaseUrl` behavior as OSS storage, then run API and Worker with the approved external local configuration directory.

**Architecture:** Keep secrets in `F:\aa\desktop\.env` and non-secret provider settings in `F:\aa\desktop\appsettings.json`. Change only the OSS configuration probe so an omitted public base URL uses the storage layer's generated HTTPS URL, while an explicitly configured URL must still be HTTPS.

**Tech Stack:** .NET 10, ASP.NET Core Minimal API, EF Core, xUnit v3, PowerShell.

## Global Constraints

- Do not print, copy, or commit AccessKey, Secret, JWT signing key, database password, or bootstrap password.
- Preserve the existing Vite proxy fix and its test.
- `Oss:PublicReadRiskAccepted` remains required and must be `true`.
- API and Worker must load both `F:\aa\desktop\.env` and `F:\aa\desktop\appsettings.json`.

---

### Task 1: Align OSS Readiness with Storage URL Generation

**Files:**
- Modify: `tests/server/WechatRobot.IntegrationTests/Operations/HealthTests.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Health/ComponentHealth.cs`

**Interfaces:**
- Consumes: `OssConfigurationHealthProbe.CheckAsync(CancellationToken)`
- Produces: readiness behavior where blank `Oss:PublicBaseUrl` is healthy when all other required OSS settings are valid.

- [x] **Step 1: Write the failing test**

Extend `Oss_probe_requires_https_public_url_and_explicit_public_read_acceptance` to clear `Oss:PublicBaseUrl` and assert `ComponentHealthState.Healthy`, then retain the existing assertion that an explicit HTTP URL is failed.

- [x] **Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test tests\server\WechatRobot.IntegrationTests\WechatRobot.IntegrationTests.csproj --no-restore -- --filter-method '*Oss_probe_requires_https_public_url_and_explicit_public_read_acceptance'
```

Expected: FAIL because blank `Oss:PublicBaseUrl` is currently included in the required non-empty values.

- [x] **Step 3: Write minimal implementation**

Require only `Oss:AccessKeyId`, `Oss:AccessKeySecret`, `Oss:Bucket`, and `Oss:Endpoint`. Treat `Oss:PublicBaseUrl` as valid when blank, or when it parses as an absolute HTTPS URI.

- [x] **Step 4: Run focused and full health tests**

Run the focused test, followed by the complete integration test class. Expected: PASS.

### Task 2: Restart with External Local Configuration

**Files:**
- Read only: `F:\aa\desktop\.env`
- Read only: `F:\aa\desktop\appsettings.json`

**Interfaces:**
- Consumes: `WECHATROBOT_ENV_FILE=F:\aa\desktop\.env`
- Produces: API on `127.0.0.1:5268`, Worker heartbeat, frontend on `127.0.0.1:5173`.

- [x] **Step 1: Build the server**

Run `dotnet build WechatRobot.slnx --no-restore`.

- [x] **Step 2: Stop only marker-owned API and Worker processes**

Stop processes containing `wechatrobot-master-api` and `wechatrobot-master-worker`. Leave the Vite process running.

- [x] **Step 3: Start API and Worker**

Set `WECHATROBOT_ENV_FILE` to the project-local `.env` copy and use `H:\Codex\WechatRobot\.local` as each process working directory so the copied `appsettings.json` is loaded. Attempt startup migration once; when the already-applied migration path is blocked by a transient RDS handshake timeout, retain the copied `.env` value `Database__ApplyMigrationsOnStartup=false` for normal runtime startup.

- [x] **Step 4: Verify readiness**

Authenticate without printing credentials or tokens, call `/api/admin/health/ready`, and require MySQL, Qdrant, OCR, OSS configuration, and Worker heartbeat to report healthy.
