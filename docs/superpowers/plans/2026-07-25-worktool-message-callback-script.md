# WorkTool Message Callback Script Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a safe PowerShell workflow that logs into `https://wxrobot.aavisa.com`, selects an enabled robot, and asks the backend to configure the real WorkTool message callback without exposing robot credentials or callback secrets.

**Architecture:** The PowerShell script talks only to authenticated WechatRobot APIs. A new backend endpoint generates a one-time callback token, calls WorkTool's `/robot/robotInfo/update` message-callback API through a dedicated client method, and persists only the token hash after provider success.

**Tech Stack:** PowerShell 7, ASP.NET Core Minimal APIs, .NET 10, EF Core, xUnit, repository PowerShell acceptance tests.

## Global Constraints

- Production public base URL is exactly `https://wxrobot.aavisa.com`.
- WorkTool message callback configuration uses `openCallback=1`, `replyAll=1`, and `/robot/robotInfo/update`.
- Do not expose administrator passwords, Bearer tokens, WorkTool Robot IDs, callback route codes, or callback tokens in output, responses, logs, or committed fixtures.
- Production accepts only an HTTPS origin without user info, path, query, or fragment; tests may use strict loopback HTTP.
- Default automated tests must use fake or loopback providers and must not contact WorkTool.
- The script is preview-only unless `-Apply` is supplied and the operator confirms with the exact text `UPDATE`.

---

### Task 1: Add the WorkTool message-callback client contract

**Files:**
- Modify: `src/server/WechatRobot.Application/WorkTool/IWorkToolClient.cs`
- Modify: `src/server/WechatRobot.Infrastructure/WorkTool/WorkToolClient.cs`
- Modify: `tests/server/WechatRobot.ContractTests/WorkTool/GroupOperationsContractTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationEndpointTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Messaging/DurableRobotCoordinationTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/WorkTool/GroupOperationWorkerTests.cs`

**Interfaces:**
- Consumes: `IWorkToolCredentialResolver.ResolveRobotIdAsync(Guid, CancellationToken)`.
- Produces: `IWorkToolClient.ConfigureMessageCallbackAsync(Guid robotConfigId, Uri callbackUrl, bool replyAll, CancellationToken cancellationToken)`.

- [ ] **Step 1: Write the failing provider contract test**

Add a test that calls:

```csharp
var result = await sut.ConfigureMessageCallbackAsync(
    Guid.NewGuid(),
    new Uri("https://wxrobot.aavisa.com/api/worktool/callback/opaque?token=secret"),
    true,
    TestContext.Current.CancellationToken);
```

Assert the literal boundary contract:

```csharp
Assert.True(result.Succeeded);
Assert.Equal("/robot/robotInfo/update?robotId=robot-7", handler.RequestUri!.PathAndQuery);
Assert.Equal(
    JsonNode.Parse("""{"openCallback":1,"replyAll":1,"callbackUrl":"https://wxrobot.aavisa.com/api/worktool/callback/opaque?token=secret"}""")!.ToJsonString(),
    JsonNode.Parse(handler.Body)!.ToJsonString());
```

- [ ] **Step 2: Run the contract test and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore --filter "FullyQualifiedName~Configure_message_callback"
```

Expected: compilation fails because `ConfigureMessageCallbackAsync` does not exist.

- [ ] **Step 3: Add the interface and minimal client implementation**

Add this interface member:

```csharp
Task<WorkToolSendResult> ConfigureMessageCallbackAsync(
    Guid robotConfigId,
    Uri callbackUrl,
    bool replyAll,
    CancellationToken cancellationToken);
```

Implement it with the existing credential resolver and response parser:

```csharp
public async Task<WorkToolSendResult> ConfigureMessageCallbackAsync(
    Guid robotConfigId,
    Uri callbackUrl,
    bool replyAll,
    CancellationToken cancellationToken)
{
    var robotId = await credentials.ResolveRobotIdAsync(robotConfigId, cancellationToken);
    using var response = await httpClient.PostAsJsonAsync(
        $"robot/robotInfo/update?robotId={Uri.EscapeDataString(robotId)}",
        new { openCallback = 1, replyAll = replyAll ? 1 : 0, callbackUrl = callbackUrl.AbsoluteUri },
        cancellationToken);
    return await ParseResultAsync(response, cancellationToken);
}
```

Add matching no-op implementations to every test double so the solution compiles.

- [ ] **Step 4: Run the contract test and verify GREEN**

Run:

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore --filter "FullyQualifiedName~Configure_message_callback"
```

Expected: PASS.

- [ ] **Step 5: Commit the client contract**

```powershell
git add src/server/WechatRobot.Application/WorkTool/IWorkToolClient.cs src/server/WechatRobot.Infrastructure/WorkTool/WorkToolClient.cs tests/server/WechatRobot.ContractTests/WorkTool/GroupOperationsContractTests.cs tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationEndpointTests.cs tests/server/WechatRobot.IntegrationTests/Messaging/DurableRobotCoordinationTests.cs tests/server/WechatRobot.IntegrationTests/WorkTool/GroupOperationWorkerTests.cs
git commit -m "feat: add WorkTool message callback client"
```

### Task 2: Add the secure administrator configuration endpoint

**Files:**
- Modify: `src/server/WechatRobot.Api/WorkTool/WorkToolGroupOperationEndpoints.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/WorkTool/GroupOperationEndpointTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationEndpointTests.cs`

**Interfaces:**
- Consumes: `ConfigureMessageCallbackAsync(Guid, Uri, bool, CancellationToken)`.
- Produces: `POST /api/admin/worktool/robots/{id}/message-callback/configure`.
- Produces response: `{ "succeeded": true, "callbackRoute": "https://wxrobot.aavisa.com/api/worktool/callback/{route}?token=[REDACTED]", "message": "Message callback configured." }`.

- [ ] **Step 1: Write failing endpoint tests**

Seed an enabled robot with a literal existing token hash and callback route. Configure the recording client to capture only the callback URL for test assertions, then call:

```csharp
var response = await client.PostAsJsonAsync(
    $"/api/admin/worktool/robots/{robot.Id:D}/message-callback/configure",
    new { publicBaseUrl = "https://wxrobot.aavisa.com", replyAll = true },
    TestContext.Current.CancellationToken);
```

Assert:

```csharp
response.EnsureSuccessStatusCode();
Assert.Equal(1, recorder.MessageCallbackCalls);
Assert.Equal(robot.Id, recorder.LastMessageCallbackRobotId);
Assert.True(recorder.LastReplyAll);
Assert.StartsWith(
    $"https://wxrobot.aavisa.com/api/worktool/callback/{robot.CallbackRouteCode}?token=",
    recorder.LastMessageCallbackUrl!.AbsoluteUri,
    StringComparison.Ordinal);
Assert.NotEqual(oldHash, saved.CallbackSecretHash);
Assert.DoesNotContain(robot.CallbackRouteCode!, responseBody, StringComparison.Ordinal);
Assert.DoesNotContain("token=", responseBody, StringComparison.OrdinalIgnoreCase);
Assert.DoesNotContain(robot.WorkToolRobotId, responseBody, StringComparison.Ordinal);
```

Add separate tests proving a disabled robot returns 404 without a provider call, an invalid public origin returns 400, and a provider failure returns 502 while preserving the literal old hash.

- [ ] **Step 2: Run endpoint tests and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~Message_callback"
```

Expected: FAIL with 404 because the route is not registered.

- [ ] **Step 3: Implement minimal secure endpoint behavior**

Register:

```csharp
group.MapPost("/robots/{id:guid}/message-callback/configure", ConfigureRobotMessageCallbackAsync);
```

Validate the origin with a focused helper that requires:

```csharp
uri.IsAbsoluteUri
&& string.IsNullOrEmpty(uri.UserInfo)
&& uri.AbsolutePath == "/"
&& string.IsNullOrEmpty(uri.Query)
&& string.IsNullOrEmpty(uri.Fragment)
&& (uri.Scheme == Uri.UriSchemeHttps || testingLoopbackHttp)
```

Generate and use the token without returning it:

```csharp
var callbackToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
var callbackUrl = new Uri(
    baseUri,
    $"/api/worktool/callback/{Uri.EscapeDataString(robot.CallbackRouteCode)}?token={Uri.EscapeDataString(callbackToken)}");
var result = await client.ConfigureMessageCallbackAsync(robot.Id, callbackUrl, request.ReplyAll, cancellationToken);
if (!result.Succeeded)
    return Results.Problem("WorkTool message callback configuration failed.", statusCode: StatusCodes.Status502BadGateway);

robot.CallbackSecretHash = Convert.ToHexString(
    SHA256.HashData(Encoding.UTF8.GetBytes(callbackToken)));
robot.UpdatedAtUtc = DateTime.UtcNow;
await database.SaveChangesAsync(cancellationToken);
return Results.Ok(new MessageCallbackConfigurationResponse(
    true,
    $"{baseUri.Scheme}://{baseUri.Authority}/api/worktool/callback/{{route}}?token=[REDACTED]",
    "Message callback configured."));
```

Catch provider exceptions and return a generic 502. Do not include `FailureReason` in the response or logs.

- [ ] **Step 4: Run endpoint tests and verify GREEN**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~Message_callback"
```

Expected: PASS.

- [ ] **Step 5: Commit the endpoint**

```powershell
git add src/server/WechatRobot.Api/WorkTool/WorkToolGroupOperationEndpoints.cs tests/server/WechatRobot.IntegrationTests/WorkTool/GroupOperationEndpointTests.cs tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationEndpointTests.cs
git commit -m "feat: configure WorkTool message callbacks safely"
```

### Task 3: Replace the callback PowerShell workflow

**Files:**
- Modify: `scripts/update-worktool-callback.ps1`
- Modify: `tests/operations/task17-operations.Tests.ps1`
- Modify: `docs/runbooks/local-development.md`

**Interfaces:**
- Consumes: `POST /api/auth/login`, `GET /api/admin/worktool/robots`, and `POST /api/admin/worktool/robots/{id}/message-callback/configure`.
- Produces: an interactive, preview-first command for `https://wxrobot.aavisa.com`.

- [ ] **Step 1: Rewrite the callback acceptance test first**

Run the script against a loopback fake API that records request paths and returns complete production-shaped fixtures:

```json
{"accessToken":"fake-bearer","tokenType":"Bearer","expiresInSeconds":900,"user":{"id":"00000000-0000-0000-0000-000000000001","email":"admin@example.test","displayName":"Admin","roles":["Admin"]}}
```

```json
[{"id":"00000000-0000-0000-0000-000000000002","name":"Main robot","robotReference":"configured","isEnabled":true}]
```

```json
{"succeeded":true,"callbackRoute":"https://wxrobot.aavisa.com/api/worktool/callback/{route}?token=[REDACTED]","message":"Message callback configured."}
```

Assert observable behavior:

- Without `-Apply`, output says preview-only and the fake API receives zero requests.
- With `-Apply -Confirmation UPDATE` against loopback, the fake API receives login, robot list, and message-callback configuration in that order.
- The bind body equals `{"publicBaseUrl":"https://wxrobot.aavisa.com","replyAll":true}`.
- The Authorization header is `Bearer fake-bearer`.
- Output contains `Main robot` and contains none of `fake-password`, `fake-bearer`, Robot ID, route code, or token.
- A disabled-only list fails before the configuration request.

- [ ] **Step 2: Run the callback acceptance test and verify RED**

Run:

```powershell
pwsh -NoProfile -File tests/operations/task17-operations.Tests.ps1 -CallbackOnly
```

Expected: FAIL because the existing script requires the obsolete tunnel, callback token, Robot ID, and arbitrary WorkTool update URI parameters.

- [ ] **Step 3: Implement the minimal script**

Use these parameters:

```powershell
[CmdletBinding(SupportsShouldProcess)]
param(
    [uri]$ApiBaseUrl = "https://wxrobot.aavisa.com",
    [uri]$PublicBaseUrl = "https://wxrobot.aavisa.com",
    [switch]$Apply,
    [ValidateRange(1, 60)][int]$TimeoutSeconds = 15,
    [string]$Email,
    [securestring]$Password,
    [string]$Confirmation,
    [int]$RobotSelection = 0
)
```

Required behavior:

- Validate both URLs as HTTPS origins; permit strict loopback HTTP only when all non-interactive test inputs are used.
- Return before credentials or networking when `-Apply` is absent or `-WhatIf` is active.
- Prompt for missing email and secure password.
- Use `HttpClient` with a bounded timeout and JSON requests.
- Convert the secure password only for the login JSON construction, then zero the BSTR in `finally`.
- Set the Bearer header only after successful login.
- Select the single enabled robot automatically; for multiple enabled robots print numbered names and read a bounded integer.
- Require exact `UPDATE`; permit `-Confirmation` only for a loopback API.
- POST `{ publicBaseUrl = $PublicBaseUrl.AbsoluteUri.TrimEnd('/'); replyAll = $true }`.
- Print only safe names and the server-provided already-redacted callback route.
- Dispose all HTTP objects in `finally`.

- [ ] **Step 4: Run the callback acceptance test and verify GREEN**

Run:

```powershell
pwsh -NoProfile -File tests/operations/task17-operations.Tests.ps1 -CallbackOnly
```

Expected: `PASS callback fake-runtime acceptance`.

- [ ] **Step 5: Update the runbook**

Document preview and apply commands:

```powershell
.\scripts\update-worktool-callback.ps1
.\scripts\update-worktool-callback.ps1 -Apply
```

State that the script configures the real WorkTool message callback, defaults to
`https://wxrobot.aavisa.com`, prompts for administrator credentials, and never
prints WorkTool Robot IDs or callback secrets.

- [ ] **Step 6: Run focused and full verification**

Run:

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore
pwsh -NoProfile -File tests/operations/task17-operations.Tests.ps1 -CallbackOnly
git diff --check
```

Expected: all tests pass and `git diff --check` is clean.

- [ ] **Step 7: Commit the script and documentation**

```powershell
git add scripts/update-worktool-callback.ps1 tests/operations/task17-operations.Tests.ps1 docs/runbooks/local-development.md
git commit -m "feat: automate WorkTool message callback setup"
```
