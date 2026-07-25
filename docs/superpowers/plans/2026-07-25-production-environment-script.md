# Production Environment Script Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a validated `configure-production-environment.ps1` that writes all API and Worker production settings into Windows environment variables, then include it in the IIS release package.

**Architecture:** A single administrator-facing PowerShell script owns the editable production setting map, validates every required value before writing anything, and writes through the .NET environment API. A standalone PowerShell acceptance test exercises the script against the current-user environment and restores prior values, so verification never requires Administrator access or changes the machine environment.

**Tech Stack:** PowerShell 7/Windows PowerShell 5.1-compatible syntax, ASP.NET Core environment-variable configuration, IIS, Windows Scheduled Tasks.

## Global Constraints

- The application must not be changed to load `.env` files.
- Production defaults target Windows machine-level environment variables.
- The filled script contains plaintext secrets and must never be committed.
- Do not print secret values.
- Do not automatically restart IIS or the Worker.
- `WECHATROBOT_MASTER_KEY_BASE64` must decode to exactly 32 bytes and remain permanently stable.
- `Oss__PublicBaseUrl` is optional; when present it must be an absolute HTTPS URL.
- Qdrant may use an HTTP base URL on a private network.
- The public CORS origin is exactly `https://wxrobot.aavisa.com`.
- Set both `ASPNETCORE_ENVIRONMENT=Production` for the API and
  `DOTNET_ENVIRONMENT=Production` for the Worker.

---

## File Structure

- `deploy/windows/configure-production-environment.ps1`: editable production template, validation, and environment writes.
- `tests/operations/production-environment.Tests.ps1`: isolated script acceptance tests with environment restoration.
- `docs/runbooks/iis-test-deployment-wxrobot.aavisa.com.md`: usage, restart order, and secret-handling instructions.
- `.gitignore`: ignore the server-filled `.local.ps1` copy.

### Task 1: Environment Script Contract and Implementation

**Files:**

- Create: `tests/operations/production-environment.Tests.ps1`
- Create: `deploy/windows/configure-production-environment.ps1`
- Modify: `.gitignore`

**Interfaces:**

- Consumes: ASP.NET Core environment variable names already read by `WechatRobot.Api` and `WechatRobot.Worker`.
- Produces: `configure-production-environment.ps1 -Target Machine|User`, with `Machine` as the default target.

- [ ] **Step 1: Write the failing acceptance test**

Create a test that:

1. Parses the script with `[Management.Automation.Language.Parser]::ParseFile`.
2. Creates a temporary copy and replaces every `REPLACE_*` example value with safe test values.
3. Invokes the temporary script with `-Target User`.
4. Asserts the complete mapping below.
5. Restores or removes every affected user environment variable in `finally`.
6. Runs negative copies for an unchanged placeholder, a short master key, a non-HTTPS OSS public URL, and an invalid Qdrant URL.
7. Confirms captured output contains variable names but none of the fake passwords or API keys.

The expected map is:

```powershell
$expected = [ordered]@{
    ASPNETCORE_ENVIRONMENT = "Production"
    DOTNET_ENVIRONMENT = "Production"
    ConnectionStrings__WechatRobot = "Server=10.0.0.10;Port=3306;Database=wechatrobot;User=wechatrobot;Password=fake-mysql-password;CharSet=utf8mb4;"
    WECHATROBOT_MASTER_KEY_BASE64 = [Convert]::ToBase64String([byte[]](1..32))
    Jwt__Issuer = "WechatRobot"
    Jwt__Audience = "WechatRobot.Admin"
    Jwt__SigningKey = "fake-jwt-signing-key-with-at-least-32-characters"
    Cors__AllowedOrigins__0 = "https://wxrobot.aavisa.com"
    Qdrant__BaseUrl = "http://10.0.0.20:6333/"
    Qdrant__ApiKey = "fake-qdrant-key"
    Oss__AccessKeyId = "fake-oss-access-key-id"
    Oss__AccessKeySecret = "fake-oss-access-key-secret"
    Oss__Bucket = "fake-bucket"
    Oss__Endpoint = "oss-cn-shenzhen"
    Oss__PublicBaseUrl = "https://files.example.test"
    Oss__PublicReadRiskAccepted = "true"
    ALIBABA_CLOUD_OCR_ACCESS_KEY_ID = "fake-ocr-access-key-id"
    ALIBABA_CLOUD_OCR_ACCESS_KEY_SECRET = "fake-ocr-access-key-secret"
    BootstrapAdmin__Email = "admin@example.test"
    BootstrapAdmin__Password = "Fake-admin-password-123!"
    BootstrapAdmin__DisplayName = "Test Admin"
    Database__ApplyMigrationsOnStartup = "true"
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run:

```powershell
pwsh -NoProfile -File tests/operations/production-environment.Tests.ps1
```

Expected: failure because `deploy/windows/configure-production-environment.ps1` does not exist.

- [ ] **Step 3: Implement the production script**

Use an editable ordered map with detailed Chinese comments and `REPLACE_*`
sentinels:

```powershell
[CmdletBinding()]
param(
    [ValidateSet("Machine", "User")]
    [string]$Target = "Machine"
)

$settings = [ordered]@{
    "ASPNETCORE_ENVIRONMENT" = "Production"
    "DOTNET_ENVIRONMENT" = "Production"
    "ConnectionStrings__WechatRobot" = "REPLACE_MYSQL_CONNECTION_STRING"
    "WECHATROBOT_MASTER_KEY_BASE64" = "REPLACE_32_BYTE_BASE64_MASTER_KEY"
    "Jwt__Issuer" = "WechatRobot"
    "Jwt__Audience" = "WechatRobot.Admin"
    "Jwt__SigningKey" = "REPLACE_JWT_SIGNING_KEY"
    "Cors__AllowedOrigins__0" = "https://wxrobot.aavisa.com"
    "Qdrant__BaseUrl" = "REPLACE_QDRANT_BASE_URL"
    "Qdrant__ApiKey" = "REPLACE_QDRANT_API_KEY"
    "Oss__AccessKeyId" = "REPLACE_OSS_ACCESS_KEY_ID"
    "Oss__AccessKeySecret" = "REPLACE_OSS_ACCESS_KEY_SECRET"
    "Oss__Bucket" = "REPLACE_OSS_BUCKET"
    "Oss__Endpoint" = "oss-cn-shenzhen"
    "Oss__PublicBaseUrl" = ""
    "Oss__PublicReadRiskAccepted" = "true"
    "ALIBABA_CLOUD_OCR_ACCESS_KEY_ID" = "REPLACE_OCR_ACCESS_KEY_ID"
    "ALIBABA_CLOUD_OCR_ACCESS_KEY_SECRET" = "REPLACE_OCR_ACCESS_KEY_SECRET"
    "BootstrapAdmin__Email" = "REPLACE_BOOTSTRAP_ADMIN_EMAIL"
    "BootstrapAdmin__Password" = "REPLACE_BOOTSTRAP_ADMIN_PASSWORD"
    "BootstrapAdmin__DisplayName" = "系统管理员"
    "Database__ApplyMigrationsOnStartup" = "true"
}
```

Implement focused validators:

```powershell
function Assert-NoPlaceholder([Collections.IDictionary]$Values)
function Assert-MasterKey([string]$Value)
function Assert-AbsoluteBaseUrl([string]$Name, [string]$Value, [bool]$RequireHttps)
function Assert-MySqlConnectionString([string]$Value)
function Assert-Administrator()
```

Validation must finish before this write loop begins:

```powershell
foreach ($entry in $settings.GetEnumerator()) {
    [Environment]::SetEnvironmentVariable(
        [string]$entry.Key,
        [string]$entry.Value,
        [EnvironmentVariableTarget]$Target)
    Write-Host "Configured $($entry.Key)"
}
```

For `Machine`, require an elevated Windows administrator token. For `User`,
skip elevation so the acceptance test can run safely. Output only setting names,
then print:

```powershell
Write-Host "Restart IIS: iisreset"
Write-Host 'Restart Worker: Stop-ScheduledTask -TaskName "WechatRobot-Worker"; Start-ScheduledTask -TaskName "WechatRobot-Worker"'
```

Add this ignore rule:

```gitignore
deploy/windows/*.local.ps1
```

- [ ] **Step 4: Run the acceptance test**

Run:

```powershell
pwsh -NoProfile -File tests/operations/production-environment.Tests.ps1
```

Expected: `PASS production environment configuration`.

- [ ] **Step 5: Run repository whitespace and secret scans**

Run:

```powershell
git diff --check
rg -n "fake-mysql-password|fake-qdrant-key|fake-oss-access-key-secret" deploy
```

Expected: `git diff --check` has no errors and the secret scan has no matches.

- [ ] **Step 6: Commit the script and test**

```powershell
git add .gitignore deploy/windows/configure-production-environment.ps1 tests/operations/production-environment.Tests.ps1
git commit -m "feat: add production environment configuration script"
```

### Task 2: Deployment Documentation and Release Package

**Files:**

- Modify: `docs/runbooks/iis-test-deployment-wxrobot.aavisa.com.md`
- Regenerate ignored artifact: `artifacts/WechatRobot-IIS-wxrobot.aavisa.com-20260725.zip`

**Interfaces:**

- Consumes: `deploy/windows/configure-production-environment.ps1` from Task 1.
- Produces: a release ZIP whose `deployment/` directory contains the script and updated runbook.

- [ ] **Step 1: Update the deployment runbook**

Add an environment setup section with these exact commands:

```powershell
Copy-Item .\configure-production-environment.ps1 .\configure-production-environment.local.ps1
notepad .\configure-production-environment.local.ps1
Set-ExecutionPolicy -Scope Process Bypass
.\configure-production-environment.local.ps1
iisreset
```

Document that:

- The `.local.ps1` file contains plaintext secrets.
- The file must stay in an administrator-only server directory.
- The Qdrant URL must be the Linux server's private address, not
  `127.0.0.1`, unless Qdrant is installed on the Windows server.
- `Oss__PublicBaseUrl` is blank for the standard Alibaba OSS bucket endpoint,
  or an HTTPS custom/CDN domain such as `https://files.aavisa.com`.
- The first API start must complete migrations before the Worker starts.
- After the first successful bootstrap, change migration startup to `false`
  and remove the bootstrap password from the machine environment.

- [ ] **Step 2: Parse and verify documentation examples**

Run:

```powershell
$null = [Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path "deploy/windows/configure-production-environment.ps1"),
    [ref]$null,
    [ref]$null)
rg -n "Qdrant__ApiKey|ConnectionStrings__WechatRobot|Oss__PublicBaseUrl" `
    deploy/windows/configure-production-environment.ps1 `
    docs/runbooks/iis-test-deployment-wxrobot.aavisa.com.md
```

Expected: parser returns without errors and all three settings occur in both
the script and runbook.

- [ ] **Step 3: Regenerate the release ZIP**

Copy the final files into the ignored release directory:

```powershell
$release = "artifacts/iis-wxrobot-20260725"
Copy-Item deploy/windows/configure-production-environment.ps1 `
    "$release/deployment/configure-production-environment.ps1" -Force
Copy-Item docs/runbooks/iis-test-deployment-wxrobot.aavisa.com.md `
    "$release/deployment/iis-test-deployment-wxrobot.aavisa.com.md" -Force
Compress-Archive -Path "$release/*" `
    -DestinationPath "artifacts/WechatRobot-IIS-wxrobot.aavisa.com-20260725.zip" `
    -CompressionLevel Optimal -Force
```

- [ ] **Step 4: Verify the release archive**

Run:

```powershell
$zip = [IO.Compression.ZipFile]::OpenRead(
    (Resolve-Path "artifacts/WechatRobot-IIS-wxrobot.aavisa.com-20260725.zip"))
try {
    $names = $zip.Entries.FullName
    if ("deployment/configure-production-environment.ps1" -notin $names) {
        throw "Environment script missing from release ZIP."
    }
} finally {
    $zip.Dispose()
}
```

Expected: no exception.

- [ ] **Step 5: Commit the runbook**

```powershell
git add docs/runbooks/iis-test-deployment-wxrobot.aavisa.com.md
git commit -m "docs: explain production environment setup"
```

- [ ] **Step 6: Final verification**

Run:

```powershell
pwsh -NoProfile -File tests/operations/production-environment.Tests.ps1
git diff --check
git status --short
Get-FileHash artifacts/WechatRobot-IIS-wxrobot.aavisa.com-20260725.zip -Algorithm SHA256
```

Expected: the acceptance test passes, the worktree is clean, and a SHA256 hash
is printed for the rebuilt release ZIP.
