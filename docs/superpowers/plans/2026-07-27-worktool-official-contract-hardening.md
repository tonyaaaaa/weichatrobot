# WorkTool Official Contract Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every existing WorkTool integration obey the verified official contract for online probing, callback acknowledgement, request serialization, and the shared 60 QPM egress limit.

**Architecture:** Keep `WorkToolClient` responsible for endpoint DTO mapping, move the cross-process egress limit into a delegating HTTP handler shared by every WorkTool request, and classify callbacks before persistence. Preserve the public nullable `online` field for compatibility while removing all invented response parsing.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, EF Core with MySQL 5.7, `IHttpClientFactory`, xUnit v3, Vue 3, TypeScript, Vitest.

## Global Constraints

- Use the verified WorkTool documents linked from `docs/superpowers/specs/2026-07-27-worktool-official-contract-hardening-design.md`.
- Do not parse `data.online`, `data.status`, or any other undocumented online-state field.
- Every actual WorkTool HTTP attempt, including an administrative retry, consumes one global permit.
- Requests sharing one configured egress scope must be spaced by at least one second at 60 QPM.
- A database or limiter failure must fail closed and must not send the WorkTool request.
- Valid unsupported callbacks return HTTP 200 with `{"code":0,"message":"ignored"}` and create no durable data.
- WorkTool-only JSON options omit `null`; empty `atList` is omitted; meaningful `false` values and official empty arrays remain.
- Preserve all unrelated dirty-worktree changes.

---

### Task 1: Remove Invented Online-State Parsing

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/WorkTool/WorkToolClient.cs`
- Modify: `tests/server/WechatRobot.ContractTests/WorkTool/RobotAndCallbackContractTests.cs`
- Modify: `src/web/wechatrobot-admin/src/views/settings/RobotSettingsView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/settings/RobotSettingsView.spec.ts`

**Interfaces:**
- Consumes: `IWorkToolClient.GetOnlineAsync(Guid, CancellationToken)`
- Produces: `WorkToolOnlineSnapshot(null, null)` for every HTTP 2xx response, without inspecting the body

- [ ] **Step 1: Write failing online response contract tests**

Add three cases to `RobotAndCallbackContractTests`:

```csharp
[Theory]
[InlineData("{}")]
[InlineData("")]
[InlineData("""{"unexpected":"successful-but-undocumented"}""")]
public async Task GetOnlineAsync_treats_every_http_2xx_body_as_unknown_without_failure(string body)
{
    using var handler = new CapturingHandler(body);

    var result = await Client(handler).GetOnlineAsync(
        Guid.NewGuid(),
        TestContext.Current.CancellationToken);

    Assert.Null(result.Online);
    Assert.Null(result.FailureCode);
}
```

Add a non-2xx assertion:

```csharp
[Fact]
public async Task GetOnlineAsync_preserves_safe_http_failure()
{
    using var handler = new CapturingHandler("upstream failed", HttpStatusCode.BadGateway);
    var result = await Client(handler).GetOnlineAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
    Assert.Null(result.Online);
    Assert.Equal("worktool_http_502", result.FailureCode);
}
```

- [ ] **Step 2: Run the focused tests and verify the undocumented body fails**

Run:

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore --filter "FullyQualifiedName~RobotAndCallbackContractTests.GetOnlineAsync" --verbosity minimal
```

Expected: the unexpected successful body returns `worktool_invalid_response`, proving the old parser is still active.

- [ ] **Step 3: Replace the online parser with status-only handling**

Implement the method body after the HTTP request as:

```csharp
if (!response.IsSuccessStatusCode)
{
    return new(null, HttpFailure(response));
}

return new(null, null);
```

Delete `OnlineData` and `ToBoolean()`.

- [ ] **Step 4: Make the frontend wording truthful**

Keep the nullable API property. Replace the three-state online tag with:

```vue
<ElTag type="info">在线状态：WorkTool 未提供可靠结果</ElTag>
```

Keep the separate `reachable` tag unchanged. Update the component test to assert the truthful text and to reject `离线`.

- [ ] **Step 5: Verify server and frontend tests**

Run:

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore --filter "FullyQualifiedName~RobotAndCallbackContractTests" --verbosity minimal
Set-Location src/web/wechatrobot-admin
npm test -- src/views/settings/RobotSettingsView.spec.ts
Set-Location ../../..
```

Expected: all selected tests pass.

- [ ] **Step 6: Commit the online-state correction**

```powershell
git add src/server/WechatRobot.Infrastructure/WorkTool/WorkToolClient.cs tests/server/WechatRobot.ContractTests/WorkTool/RobotAndCallbackContractTests.cs src/web/wechatrobot-admin/src/views/settings/RobotSettingsView.vue src/web/wechatrobot-admin/src/views/settings/RobotSettingsView.spec.ts
git commit -m "fix: stop inventing WorkTool online state"
```

### Task 2: Acknowledge Valid Unsupported Callbacks

**Files:**
- Modify: `src/server/WechatRobot.Application/WorkTool/WorkToolCallbackDto.cs`
- Modify: `src/server/WechatRobot.Api/WorkTool/WorkToolCallbackEndpoints.cs`
- Modify: `tests/server/WechatRobot.ContractTests/WorkTool/RecordedCallbackSamples.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/WorkTool/CallbackIngestionTests.cs`

**Interfaces:**
- Produces: `WorkToolCallbackClassification(Disposition, Reason)`
- Dispositions: `Process`, `Ignore`, `Reject`

- [ ] **Step 1: Replace the existing wrong integration expectations**

Replace `Non_group_or_non_text_callback_is_rejected_without_enqueuing` with:

```csharp
[Theory]
[InlineData("roomType", 2)]
[InlineData("roomType", 3)]
[InlineData("roomType", 4)]
[InlineData("textType", 2)]
[InlineData("textType", 3)]
[InlineData("textType", 9)]
public async Task Official_but_unsupported_callback_is_acknowledged_and_ignored(string field, int value)
{
    await using var factory = new CallbackApiFactory(_fixture.ConnectionString);
    var robot = await SeedRobotAsync(
        factory,
        $"callback-ignored-{field}-{value}",
        "callback-secret");
    var payload = ValidPayload($"message-ignored-{field}-{value}");
    payload[field] = value;
    using var client = factory.CreateClient();

    var response = await client.PostAsJsonAsync(
        $"/api/worktool/callback/{robot.CallbackRouteCode}?token=callback-secret",
        payload,
        TestContext.Current.CancellationToken);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal(
        "{\"code\":0,\"message\":\"ignored\"}",
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    await AssertNoInboundDataAsync(factory, robot.Id);
}
```

The exact response assertion is:

```csharp
Assert.Equal(HttpStatusCode.OK, response.StatusCode);
Assert.Equal(
    "{\"code\":0,\"message\":\"ignored\"}",
    await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
await AssertNoInboundDataAsync(factory, robot.Id);
```

Add a Base64 size failure case using `fileBase64 = new string('A', configuredLimit + 1)`.

- [ ] **Step 2: Run the callback integration test and verify RED**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~CallbackIngestionTests.Official_but_unsupported_callback" --verbosity minimal
```

Expected: HTTP 400 is returned instead of the required HTTP 200.

- [ ] **Step 3: Add explicit callback classification**

Add:

```csharp
public enum WorkToolCallbackDisposition
{
    Process,
    Ignore,
    Reject
}

public sealed record WorkToolCallbackClassification(
    WorkToolCallbackDisposition Disposition,
    string Reason);
```

Add `[JsonPropertyName("fileBase64")] public string? FileBase64 { get; init; }`.

Implement `Classify()` with this order:

```csharp
if (!AllFieldsWithinLimits()) return new(Reject, "callback-field-too-large");
if (RoomType is < 1 or > 4) return new(Reject, "unknown-room-type");
if (!OfficialTextTypes.Contains(TextType)) return new(Reject, "unknown-text-type");
if (RoomType != 1 || TextType != 1) return new(Ignore, "unsupported-message-kind");
if (string.IsNullOrWhiteSpace(GroupName) ||
    string.IsNullOrWhiteSpace(ReceivedName) ||
    string.IsNullOrWhiteSpace(Spoken))
    return new(Reject, "missing-required-group-text-field");
return new(Process, string.Empty);
```

Use the documented text types:

```csharp
private static readonly HashSet<int?> OfficialTextTypes =
    [0, 1, 2, 3, 5, 7, 8, 9, 13, 15];
```

Set `private const int MaxFileBase64Length = 8 * 1024 * 1024;` and enforce it before logging or persistence.

- [ ] **Step 4: Update the endpoint branch**

After authentication:

```csharp
var classification = callback.Classify();
if (classification.Disposition == WorkToolCallbackDisposition.Reject)
    return Results.BadRequest();
if (classification.Disposition == WorkToolCallbackDisposition.Ignore)
{
    logger.LogInformation(
        "WorkTool callback ignored with reason {Reason}, room type {RoomType}, text type {TextType}.",
        classification.Reason,
        callback.RoomType,
        callback.TextType);
    return Results.Ok(new { code = 0, message = "ignored" });
}
```

Do not pass an ignored DTO to `IngestAsync`.

- [ ] **Step 5: Update contract samples and rerun tests**

Add contract cases for documented room/text types and string `atMe`. Run:

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore --filter "FullyQualifiedName~RecordedCallbackSamples" --verbosity minimal
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~CallbackIngestionTests" --verbosity minimal
```

Expected: valid unsupported callbacks are ignored; malformed/auth/size failures retain 400/401.

- [ ] **Step 6: Commit callback classification**

```powershell
git add src/server/WechatRobot.Application/WorkTool/WorkToolCallbackDto.cs src/server/WechatRobot.Api/WorkTool/WorkToolCallbackEndpoints.cs tests/server/WechatRobot.ContractTests/WorkTool/RecordedCallbackSamples.cs tests/server/WechatRobot.IntegrationTests/WorkTool/CallbackIngestionTests.cs
git commit -m "fix: acknowledge unsupported WorkTool callbacks"
```

### Task 3: Omit Null Fields and Empty `atList`

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/WorkTool/WorkToolClient.cs`
- Modify: `tests/server/WechatRobot.ContractTests/WorkTool/SendRawMessageContractTests.cs`
- Modify: `tests/server/WechatRobot.ContractTests/WorkTool/GroupOperationsContractTests.cs`

**Interfaces:**
- Produces: WorkTool-only `JsonSerializerOptions` with `WhenWritingNull`

- [ ] **Step 1: Add exact failing JSON tests**

Add:

```csharp
[Fact]
public async Task SendTextAsync_omits_atList_when_no_targets_exist()
{
    // Send with AtList = [] and capture the complete JSON document.
    Assert.False(JsonNode.Parse(handler.Body)!["list"]![0]!.AsObject().ContainsKey("atList"));
}
```

For a rename command assert:

```csharp
var command = JsonNode.Parse(handler.Body)!["list"]![0]!.AsObject();
Assert.False(command.ContainsKey("newGroupAnnouncement"));
Assert.Equal(false, command["showMessageHistory"]!.GetValue<bool>());
Assert.NotNull(command["selectList"]);
Assert.NotNull(command["removeList"]);
```

- [ ] **Step 2: Run focused contract tests and verify RED**

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore --filter "FullyQualifiedName~SendRawMessageContractTests|FullyQualifiedName~GroupOperationsContractTests" --verbosity minimal
```

Expected: empty `atList` and null operation fields are present.

- [ ] **Step 3: Configure WorkTool-only serialization**

Change the static options to:

```csharp
private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};
```

Normalize `atList`:

```csharp
atList = request.AtList is { Count: > 0 } ? request.AtList : null
```

Replace `PostAsJsonAsync` with an explicit request whose content is created by `JsonContent.Create(body, mediaType: null, options: JsonOptions)` so command submissions and administrative requests use the same options.

- [ ] **Step 4: Rerun WorkTool contract tests**

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore --filter "FullyQualifiedName~WorkTool" --verbosity minimal
```

Expected: exact JSON assertions pass and boolean/empty-array semantics are preserved.

- [ ] **Step 5: Commit serialization correction**

```powershell
git add src/server/WechatRobot.Infrastructure/WorkTool/WorkToolClient.cs tests/server/WechatRobot.ContractTests/WorkTool/SendRawMessageContractTests.cs tests/server/WechatRobot.ContractTests/WorkTool/GroupOperationsContractTests.cs
git commit -m "fix: serialize WorkTool requests exactly"
```

### Task 4: Add the Shared Egress Permit Model and Configuration

**Files:**
- Create: `src/server/WechatRobot.Application/WorkTool/IWorkToolGlobalRateLimiter.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Entities/WorkToolRateLimitBucketEntity.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Configurations/WorkToolRateLimitBucketConfiguration.cs`
- Create: `src/server/WechatRobot.Infrastructure/WorkTool/WorkToolRateLimitOptions.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/WechatRobotDbContext.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Security/StartupConfigurationValidator.cs`
- Modify: `tests/server/WechatRobot.UnitTests/Security/StartupConfigurationValidatorTests.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/WorkTool/WorkToolRateLimitMigrationTests.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/WechatRobotDbContextModelSnapshot.cs`

**Interfaces:**
- Produces:

```csharp
public interface IWorkToolGlobalRateLimiter
{
    Task<WorkToolRateLimitLease> AcquireAsync(string operation, CancellationToken cancellationToken);
}

public sealed record WorkToolRateLimitLease(bool Acquired, string? FailureCode);
```

- [ ] **Step 1: Write failing startup and migration tests**

Assert:

```csharp
values["WorkTool:RateLimit:RequestsPerMinute"] = "61";
Assert.Throws<InvalidOperationException>(() => Validate(values));
```

Migration test assertions:

```csharp
Assert.True(await TableExistsAsync(database, "worktool_rate_limit_bucket"));
Assert.True(await ColumnExistsAsync(database, "worktool_rate_limit_bucket", "ScopeKey"));
Assert.True(await ColumnExistsAsync(database, "worktool_rate_limit_bucket", "NextPermitAtUtc"));
```

- [ ] **Step 2: Run tests and verify RED**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore --filter "FullyQualifiedName~StartupConfigurationValidatorTests" --verbosity minimal
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~WorkToolRateLimitMigrationTests" --verbosity minimal
```

Expected: missing options validation and missing table fail.

- [ ] **Step 3: Add options, entity, configuration, and DbSet**

Use:

```csharp
public sealed class WorkToolRateLimitOptions
{
    public string ScopeKey { get; set; } = "default-egress";
    public int RequestsPerMinute { get; set; } = 60;
    public int MaxWaitSeconds { get; set; } = 15;
}
```

Entity:

```csharp
public sealed class WorkToolRateLimitBucketEntity
{
    public string ScopeKey { get; set; } = string.Empty;
    public DateTime NextPermitAtUtc { get; set; }
    public int Version { get; set; }
}
```

Map to `worktool_rate_limit_bucket`, key `ScopeKey`, max length 128, concurrency token `Version`.

- [ ] **Step 4: Generate the migration**

```powershell
dotnet ef migrations add AddWorkToolGlobalRateLimit --project src/server/WechatRobot.Infrastructure --startup-project src/server/WechatRobot.Api
```

Inspect the generated migration and confirm MySQL 5.7-compatible SQL only.

- [ ] **Step 5: Bind and validate options**

Validate:

```csharp
if (string.IsNullOrWhiteSpace(options.ScopeKey) || options.ScopeKey.Length > 128)
    throw new InvalidOperationException("WorkTool:RateLimit:ScopeKey must contain 1-128 characters.");
if (options.RequestsPerMinute is < 1 or > 60)
    throw new InvalidOperationException("WorkTool:RateLimit:RequestsPerMinute must be between 1 and 60.");
if (options.MaxWaitSeconds is < 1 or > 60)
    throw new InvalidOperationException("WorkTool:RateLimit:MaxWaitSeconds must be between 1 and 60.");
```

- [ ] **Step 6: Rerun startup and migration tests**

Run the commands from Step 2. Expected: PASS.

- [ ] **Step 7: Commit the model and migration**

```powershell
git add src/server/WechatRobot.Application/WorkTool/IWorkToolGlobalRateLimiter.cs src/server/WechatRobot.Infrastructure/Persistence/Entities/WorkToolRateLimitBucketEntity.cs src/server/WechatRobot.Infrastructure/Persistence/Configurations/WorkToolRateLimitBucketConfiguration.cs src/server/WechatRobot.Infrastructure/WorkTool/WorkToolRateLimitOptions.cs src/server/WechatRobot.Infrastructure/Persistence/WechatRobotDbContext.cs src/server/WechatRobot.Infrastructure/Security/StartupConfigurationValidator.cs src/server/WechatRobot.Infrastructure/Persistence/Migrations tests/server/WechatRobot.UnitTests/Security/StartupConfigurationValidatorTests.cs tests/server/WechatRobot.IntegrationTests/WorkTool/WorkToolRateLimitMigrationTests.cs
git commit -m "feat: persist WorkTool global rate limit"
```

### Task 5: Implement MySQL-Coordinated Smooth Permits

**Files:**
- Create: `src/server/WechatRobot.Infrastructure/WorkTool/MySqlWorkToolGlobalRateLimiter.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/WorkTool/WorkToolGlobalRateLimiterTests.cs`

**Interfaces:**
- Consumes: `IDbContextFactory<WechatRobotDbContext>`, `IOptions<WorkToolRateLimitOptions>`
- Produces: database-reserved permit times spaced by `60 / RequestsPerMinute` seconds

- [ ] **Step 1: Write failing MySQL concurrency tests**

Create tests that:

```csharp
var leases = await Task.WhenAll(
    Enumerable.Range(0, 3).Select(_ => limiter.AcquireAsync("test", token)));

Assert.All(leases, lease => Assert.True(lease.Acquired));
Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(2));
```

Add a second-instance test using two service scopes against the same MySQL database. Add cancellation and `MaxWaitSeconds=1` failure cases.

- [ ] **Step 2: Run the limiter tests and verify RED**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~WorkToolGlobalRateLimiterTests" --verbosity minimal
```

Expected: the implementation type is missing.

- [ ] **Step 3: Implement permit reservation**

Within a serializable transaction:

```csharp
var databaseNow = await database.Database
    .SqlQueryRaw<DateTime>("SELECT UTC_TIMESTAMP(6) AS Value")
    .SingleAsync(cancellationToken);
var bucket = await database.WorkToolRateLimitBuckets
    .FromSqlInterpolated(
        $"SELECT * FROM worktool_rate_limit_bucket WHERE ScopeKey = {options.ScopeKey} FOR UPDATE")
    .SingleAsync(cancellationToken);
var permitAt = bucket.NextPermitAtUtc > databaseNow ? bucket.NextPermitAtUtc : databaseNow;
var wait = permitAt - databaseNow;
if (wait > TimeSpan.FromSeconds(options.MaxWaitSeconds))
{
    await transaction.RollbackAsync(cancellationToken);
    return new(false, "worktool_global_rate_limited");
}
bucket.NextPermitAtUtc = permitAt.AddSeconds(60d / options.RequestsPerMinute);
bucket.Version++;
await database.SaveChangesAsync(cancellationToken);
await transaction.CommitAsync(cancellationToken);
await Task.Delay(wait, cancellationToken);
return new(true, null);
```

Create the missing bucket row with an insert-and-retry path that tolerates a duplicate key from concurrent first use.

- [ ] **Step 4: Verify concurrency, cancellation, and fail-closed behavior**

Run the command from Step 2. Expected: PASS with no request spacing below the configured interval.

- [ ] **Step 5: Commit the limiter**

```powershell
git add src/server/WechatRobot.Infrastructure/WorkTool/MySqlWorkToolGlobalRateLimiter.cs tests/server/WechatRobot.IntegrationTests/WorkTool/WorkToolGlobalRateLimiterTests.cs
git commit -m "feat: coordinate WorkTool egress permits"
```

### Task 6: Put Every WorkTool HTTP Attempt Behind the Permit Handler

**Files:**
- Create: `src/server/WechatRobot.Infrastructure/WorkTool/WorkToolGlobalRateLimitHandler.cs`
- Modify: `src/server/WechatRobot.Api/Program.cs`
- Modify: `src/server/WechatRobot.Worker/Program.cs`
- Modify: `src/server/WechatRobot.Api/appsettings.json`
- Modify: `src/server/WechatRobot.Worker/appsettings.json`
- Modify: `.env.example`
- Modify: `deploy/windows/wechatrobot.env.example`
- Modify: `tests/server/WechatRobot.ContractTests/WorkTool/WorkToolHttpTransportContractTests.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/WorkTool/WorkToolRateLimitPipelineTests.cs`

**Interfaces:**
- Produces: `WorkToolGlobalRateLimitHandler : DelegatingHandler`

- [ ] **Step 1: Write a failing handler pipeline test**

Use a recording limiter and terminal handler:

```csharp
await client.GetAsync("/first", token);
await client.PostAsync("/second", JsonContent.Create(new { }), token);

Assert.Equal(2, limiter.AcquireCalls);
Assert.Equal(2, terminal.SendCalls);
```

Add a rejected lease case and assert terminal `SendCalls == 0`.

- [ ] **Step 2: Run tests and verify RED**

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore --filter "FullyQualifiedName~WorkToolHttpTransportContractTests" --verbosity minimal
```

Expected: the delegating handler is missing.

- [ ] **Step 3: Implement the handler**

```csharp
protected override async Task<HttpResponseMessage> SendAsync(
    HttpRequestMessage request,
    CancellationToken cancellationToken)
{
    var lease = await limiter.AcquireAsync(
        $"{request.Method.Method}:{request.RequestUri?.AbsolutePath}",
        cancellationToken);
    if (!lease.Acquired)
        throw new WorkToolRateLimitException(lease.FailureCode ?? "worktool_global_rate_limited");
    return await base.SendAsync(request, cancellationToken);
}
```

The exception message must not include query parameters.

- [ ] **Step 4: Register the same limiter and handler in API and Worker**

Bind `WorkToolRateLimitOptions`, register `IDbContextFactory`, limiter, and transient handler. Update both WorkTool clients:

```csharp
builder.Services
    .AddHttpClient<IWorkToolClient, WorkToolClient>(
        client => client.BaseAddress = new Uri(
            builder.Configuration["WorkTool:BaseUrl"]
            ?? "https://api.worktool.ymdyes.cn/"))
    .AddHttpMessageHandler<WorkToolGlobalRateLimitHandler>()
    .ConfigurePrimaryHttpMessageHandler(WorkToolHttpTransport.CreatePrimaryHandler);
```

Map `WorkToolRateLimitException` to the existing safe failure code at the WorkTool client boundary.

- [ ] **Step 5: Document identical API/Worker configuration**

Add:

```dotenv
WorkTool__RateLimit__ScopeKey=default-egress
WorkTool__RateLimit__RequestsPerMinute=60
WorkTool__RateLimit__MaxWaitSeconds=15
```

Explain that API and Worker using the same public egress IP must use the same `ScopeKey`.

- [ ] **Step 6: Verify that an administrative retry consumes two permits**

Extend the existing transport-failure-then-success test with a recording limiter and assert two `AcquireAsync` calls.

- [ ] **Step 7: Run focused and full server verification**

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore --verbosity minimal
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore --verbosity minimal
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~WorkTool" --verbosity minimal
dotnet build WechatRobot.slnx --no-restore
```

Expected: all pass with zero build warnings and errors.

- [ ] **Step 8: Commit the pipeline integration**

```powershell
git add src/server/WechatRobot.Infrastructure/WorkTool/WorkToolGlobalRateLimitHandler.cs src/server/WechatRobot.Api/Program.cs src/server/WechatRobot.Worker/Program.cs src/server/WechatRobot.Api/appsettings.json src/server/WechatRobot.Worker/appsettings.json .env.example deploy/windows/wechatrobot.env.example tests/server/WechatRobot.ContractTests/WorkTool/WorkToolHttpTransportContractTests.cs tests/server/WechatRobot.IntegrationTests/WorkTool/WorkToolRateLimitPipelineTests.cs
git commit -m "feat: enforce WorkTool global egress limit"
```

### Task 7: Final Contract and UI Verification

**Files:**
- Modify only if a verification failure requires an in-scope correction

**Interfaces:**
- Consumes: completed Tasks 1-6
- Produces: release evidence

- [ ] **Step 1: Search for forbidden invented parsing and old callback behavior**

```powershell
rg -n "OnlineData|data\\.online|data\\.status|unsupported-room-type.*BadRequest|unsupported-text-type.*BadRequest|atList = request\\.AtList \\?\\? \\[\\]" src tests
```

Expected: no production matches.

- [ ] **Step 2: Run all backend verification**

```powershell
dotnet build WechatRobot.slnx --no-restore
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore --verbosity minimal
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore --verbosity minimal
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore --verbosity minimal
```

- [ ] **Step 3: Run all frontend verification**

```powershell
Set-Location src/web/wechatrobot-admin
npm run typecheck
npm test
npm run build
Set-Location ../../..
```

- [ ] **Step 4: Verify migration against MySQL 5.7**

Apply migrations to the configured disposable integration database, then query:

```sql
SHOW CREATE TABLE worktool_rate_limit_bucket;
SELECT ScopeKey, NextPermitAtUtc, Version
FROM worktool_rate_limit_bucket;
```

Expected: compatible schema and one row per configured scope after first use.

- [ ] **Step 5: Record final worktree evidence**

```powershell
git status --short
git log -7 --oneline
```

Confirm only intended plan-A files were committed and unrelated changes remain preserved.
