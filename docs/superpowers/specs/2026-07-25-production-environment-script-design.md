# Production Environment Script Design

## Objective

Provide `deploy/windows/configure-production-environment.ps1` for configuring
the Windows IIS test server used by `wxrobot.aavisa.com`.

The script writes the values required by both `WechatRobot.Api` and
`WechatRobot.Worker` into Windows machine-level environment variables. ASP.NET
Core and the Worker then receive those values whenever their processes start.
The application will not be changed to load `.env` files.

## Configuration Covered

The script contains a clearly marked editable section with detailed Chinese
comments for:

- ASP.NET Core environment and the `wechatrobot` MySQL connection string.
- The permanent 32-byte Base64 master encryption key.
- JWT issuer, audience, and signing key.
- The exact frontend CORS origin `https://wxrobot.aavisa.com`.
- Qdrant base URL and API key.
- Alibaba Cloud OSS endpoint, bucket, credentials, optional public base URL,
  and the explicit public-read risk switch.
- Alibaba Cloud OCR credentials.
- Initial bootstrap administrator details.
- The first-start database migration switch.

`Oss__PublicBaseUrl` may be empty. When empty, the current storage
implementation builds the public URL from the bucket and OSS endpoint. When
specified, it must be an absolute HTTPS base URL for an OSS custom domain or
CDN domain.

## Behavior and Safety

- Require an elevated Administrator PowerShell session.
- Validate required values before changing the machine environment.
- Reject unchanged example placeholders.
- Validate URI fields and require HTTPS for `Oss__PublicBaseUrl` when present.
- Validate that `WECHATROBOT_MASTER_KEY_BASE64` decodes to exactly 32 bytes.
- Write variables with
  `[Environment]::SetEnvironmentVariable(..., "Machine")`; do not use `setx`.
- Be idempotent: running the script again updates the same variables.
- Never print secret values to the terminal.
- Do not automatically restart IIS or the Worker because that interrupts
  running processes. Print the exact restart commands after successful setup.
- Explain that the filled script contains plaintext secrets, must remain only
  on the server in an access-controlled directory, and must never be committed.

## Deployment Packaging

The release ZIP includes the script under `deployment/`. The repository also
ignores the server-local filled copy if it is created with a `.local.ps1`
suffix. The deployment runbook links to the script and documents the required
restart order:

1. Run the environment script as Administrator.
2. Restart IIS so the API receives the new machine environment.
3. Complete the first API migration successfully.
4. Start or restart the Worker scheduled task.

## Verification

- Parse the script with the PowerShell parser without executing it.
- Add a safe current-user test mode so automated verification can prove the
  variable mapping without modifying machine-level state.
- Verify placeholder rejection, Base64 master-key validation, URI validation,
  and secret redaction.
- Rebuild the release ZIP and verify that the script and updated runbook are
  present.
