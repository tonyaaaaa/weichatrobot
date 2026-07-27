# Shared Production .env Design

## Objective

Make `WechatRobot.Api` and `WechatRobot.Worker` load the same production `.env`
file directly whenever either process starts. No PowerShell import step is
required.

## File Location and Resolution

The deployment location is:

```text
C:\wxrobot\config\.env
```

The loader uses that exact fixed path by default. `WECHATROBOT_ENV_FILE` may
specify a different absolute file path. An explicitly configured path that does
not exist is a startup error; an absent default file is allowed so development
and tests can continue using ordinary environment variables.

## Loading and Precedence

The loader runs before `WebApplication.CreateBuilder` or
`Host.CreateApplicationBuilder`. It adds `.env` values only when the process
does not already contain that variable. Therefore the precedence is:

```text
process/machine environment > .env > appsettings JSON defaults
```

API and Worker use the same union of settings. Each process ignores settings it
does not consume. Both `ASPNETCORE_ENVIRONMENT=Production` and
`DOTNET_ENVIRONMENT=Production` are included because the API and generic Worker
host use different environment names.

## Parser Contract

- UTF-8 text, with or without BOM.
- Blank lines and lines whose first non-space character is `#` are ignored.
- Each setting is `NAME=VALUE`; split at the first `=`.
- Names must match `[A-Za-z_][A-Za-z0-9_]*`.
- Single-quoted and double-quoted values are supported.
- Unquoted values retain `#`, `;`, and additional `=` characters.
- Duplicate names, malformed lines, or unmatched quotes fail startup.
- No shell execution, interpolation, variable expansion, or secret logging.

## Security and Deployment

Commit only `deploy/windows/wechatrobot.env.example`, with detailed Chinese
comments and non-secret placeholders. The real `.env` remains ignored by Git
and is copied to `C:\wxrobot\config\.env`. On Windows, administrators and
the API/Worker identities receive read access; ordinary users do not.

The example covers MySQL, the permanent master key, JWT, CORS, Qdrant, OSS,
OCR, bootstrap administrator, and migration startup. `Oss__PublicBaseUrl` may
be empty; when present it is an HTTPS OSS custom domain or CDN base URL.

## Verification

- Unit tests cover parsing, quoting, precedence, duplicate rejection, malformed
  input, explicit missing paths, and the fixed default deployment path.
- Contract tests confirm both API and Worker call the loader before constructing
  their hosts.
- API and Worker publish outputs include no real `.env`.
- The release ZIP contains `config/.env.example` and the updated deployment
  runbook.
