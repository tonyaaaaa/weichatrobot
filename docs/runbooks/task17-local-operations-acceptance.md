# Task 17 local operations acceptance

## Safety boundary

This acceptance used only repository-owned fake API, Worker, Vue, and loopback HTTP callback processes. It did not call WorkTool, Cloudflare, MySQL, Qdrant, OCR, OSS, or any other external endpoint. The callback tunnel value was a string used to construct a payload for a loopback fake server; no request was sent to that hostname.

## Automated fake-runtime matrix

Command:

```powershell
.\tests\operations\task17-operations.Tests.ps1
```

Result on 2026-07-23 (Asia/Shanghai): PASS.

- Callback preview hid the authoritative robot ID and token, rejected a tunnel URL containing a path/query, sent the authoritative ID only inside the loopback request, accepted HTTP 200 plus `success: true`, and rejected HTTP 200 plus `success: false`.
- `start-dev.ps1` followed by a second start reused one three-process manifest; `stop-dev.ps1` followed by a second stop was idempotent.
- The manifest recorded its schema version, repository marker, API URL, Web port, runtime mode, and exact process identities. A second start with a different Web port was rejected without replacing the manifest or disturbing the healthy original stack.
- Two concurrent starts were serialized by the checkout-specific named mutex and converged on one API/Worker/Web process set.
- An occupied API port failed before child launch and left no manifest.
- A fake Web startup failure stopped the API and Worker started by that invocation, closed both ports, and removed the manifest.
- A forged manifest pointing at an unrelated live process with the same PID, creation time, and executable path but the wrong command marker was refused; the unrelated process remained alive.

The harness printed:

```text
PASS callback fake-runtime acceptance
PASS start x2 / stop x2
PASS concurrent start serialization
PASS port conflict preflight
PASS startup failure cleanup
PASS stale/reused PID ownership refusal
PASS all Task17 local operation acceptance tests
```

## Recorded readiness sample

Command:

```powershell
.\scripts\start-dev.ps1 -FakeRuntime -ApiUrl http://127.0.0.1:38461 -WebPort 38462 -StartupTimeoutSeconds 10
Invoke-WebRequest http://127.0.0.1:38461/health/live
Invoke-WebRequest http://127.0.0.1:38462/
.\scripts\stop-dev.ps1
```

Evidence:

- Start: `2026-07-23T13:39:31.1714663+08:00`
- Finish: `2026-07-23T13:39:33.2328156+08:00`
- API PID/port: `31500` / `38461`; liveness HTTP `200`
- Worker PID: `10868`; persisted-readiness marker timestamp `2026-07-23T05:39:32.3213437+00:00`
- Web PID/port: `35352` / `38462`; root HTTP `200`
- Stop result: all three exact manifest-owned process identities stopped; `.dev/processes.json` removed; Docker dependencies untouched.

PIDs above are historical evidence from a completed fake run and are no longer expected to exist.

## Fresh server verification

Command:

```powershell
dotnet clean WechatRobot.slnx --nologo
dotnet build WechatRobot.slnx -warnaserror --nologo
dotnet test WechatRobot.slnx --no-build -- --timeout 10m
```

Result on 2026-07-23 (Asia/Shanghai):

- Clean: succeeded.
- Build: succeeded with `0` warnings and `0` errors.
- Tests: `312` passed, `0` failed, `0` skipped; final test duration `3m 02s`.

The suite includes recursive log-redaction coverage for nested/escaped JSON and OSS signed URLs, plus loopback object-storage readiness checks against the runtime `LoopbackObjectStorage:BaseUrl` setting. A real-MySQL integration test runs the MySQL and persisted Worker-heartbeat probes together and proves that their EF Core context IDs are independent. Startup validation tests also reject CORS values containing user information, a trailing slash, a path, query, or fragment instead of an exact normalized authority origin.
