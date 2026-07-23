# Windows local development

## Prerequisites

Install Docker Desktop, .NET 10 SDK plus `dotnet-ef`, and Node.js/npm. Copy `.env.example` to the ignored `.env` file and replace every `replace-with-...` value. Use local-only credentials. The master key must be a Base64-encoded 32-byte value and the JWT signing key must contain at least 32 characters.

## Start and stop

From the repository root:

```powershell
.\scripts\start-dev.ps1
.\scripts\start-dev.ps1
```

The second call is intentionally idempotent. A checkout-specific named mutex serializes concurrent start/stop operations. The script validates tools, configuration, and free API/Web ports; waits for required MySQL and Qdrant dependencies; starts optional OCR without blocking first-time model download; builds the server; applies EF migrations; and starts API, Worker, and Vite (`--strictPort`) in hidden processes. Startup succeeds only after API liveness and Vue return HTTP 200 and the Worker writes a fresh local readiness marker after persisting its database heartbeat. A failed check stops only this invocation's recorded PIDs and removes its manifest. The ignored `.dev/processes.json` binds the requested endpoints and runtime mode to exact repository-owned process identities; stdout/stderr are under ignored `.dev/logs`. Secrets are never written to command output or process metadata by the script.

Public liveness is `http://127.0.0.1:5268/health/live`. Detailed readiness is `http://127.0.0.1:5268/api/admin/health/ready` and requires an Admin bearer token. Detailed status covers MySQL, Qdrant, OCR, OSS configuration, and Worker heartbeat. OCR is optional and produces `degraded`; failed MySQL, Qdrant, OSS configuration, or stale Worker heartbeat produces `failed`.

Configure each `Cors:AllowedOrigins` entry as an exact normalized authority such as `https://admin.example.test` or `http://127.0.0.1:5173`. Do not include credentials, a trailing slash, path, query, fragment, or wildcard.

Stop only processes recorded by this checkout:

```powershell
.\scripts\stop-dev.ps1
.\scripts\stop-dev.ps1
```

The second stop is also idempotent. Compose dependencies and volumes are intentionally left running. Use `docker compose --env-file .env -p wechatrobot-dev down` separately when dependency shutdown is desired; do not add `-v` unless volume deletion is explicitly intended.

## Safe script validation

The following commands perform no process, Docker, network, callback, or WorkTool changes:

```powershell
.\scripts\start-dev.ps1 -WhatIf
.\scripts\start-dev.ps1 -WhatIf
.\scripts\stop-dev.ps1 -WhatIf
.\scripts\stop-dev.ps1 -WhatIf
.\scripts\update-worktool-callback.ps1 -TunnelUrl https://example.trycloudflare.com -CallbackToken fake-token -WorkToolRobotId fake-id -WorkToolUpdateUri https://127.0.0.1/never-called -WhatIf
.\tests\operations\task17-operations.Tests.ps1
```

The callback script uses `WorkToolRobotId` as the sole authoritative payload and callback-route identity. The preview shows only a `{robot-code}` placeholder, redacted token, and one-way route fingerprint. `TunnelUrl` must be an HTTPS origin with no path, query, fragment, or user info. The script is preview-only unless `-Apply` is present. Applying requires the operator to type `UPDATE`; the bounded HTTP call is accepted only for a 2xx response containing JSON `{"success":true}`. Tests and routine development must use loopback fake endpoints or `-WhatIf`; never use a real WorkTool target without explicit approval.
