# Model Configuration Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build complete ID-based administration for OpenAI-compatible chat and embedding configurations, including rename, connection gating, one default per type, optional API keys, deletion protection, auditing, and an Element Plus create/edit flow.

**Architecture:** Keep `ModelConfigEntity.Id` as the stable identity and move management rules into a focused infrastructure service used by thin Minimal API endpoints. Enforce normalized names and one default per type in MySQL, persist connection-test provenance, and expose ID-based APIs while retaining the existing name-based routes for compatibility. The Vue page uses typed API functions, summary cards, and one reusable dialog.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, EF Core 10 with MySQL 8.4, ASP.NET Core Identity, Vue 3, TypeScript, Element Plus, Vitest, Playwright.

## Global Constraints

- Preserve all unrelated uncommitted work, especially the current administration entities, configurations, DbContext registrations, and `20260723103629_AddAdministrationSurfaces` migration.
- `ModelConfigEntity.Id` is permanent; rename never changes IDs or business references.
- Names are trimmed, 1–128 characters, and unique case-insensitively.
- `ConfigurationType` is exactly `chat` or `embedding`.
- Each configuration type has at most one default.
- A configuration can be saved while untested, but it cannot be enabled or made default until its current fingerprint has a successful test.
- API keys are optional, encrypted at rest, never returned, and cleared only through an explicit endpoint.
- Default or referenced configurations cannot be deleted.
- Audit records never include plaintext API keys, encrypted API keys, request authorization headers, or provider response bodies.
- Retain the existing name-based routes during this implementation; removing them is a separate compatibility change.
- Use TDD for every behavior: observe the focused test fail, implement the minimum behavior, and rerun the focused test before moving on.
- Do not make real OpenAI, WorkTool, OSS, OCR, or WeChat calls in automated tests.

---

## File Map

### Server contracts and provider clients

- Modify `src/server/WechatRobot.Application/Models/IChatCompletionClient.cs` to make the protected API key optional.
- Modify `src/server/WechatRobot.Application/Models/ModelConfigurationService.cs` to preserve/clear keys and calculate configuration fingerprints.
- Modify `src/server/WechatRobot.Infrastructure/Models/OpenAiCompatibleChatClient.cs` to omit `Authorization` when no key exists.
- Modify `src/server/WechatRobot.Infrastructure/Models/OpenAiCompatibleEmbeddingClient.cs` for the same optional-key behavior.
- Modify `tests/server/WechatRobot.UnitTests/Models/OpenAiCompatibleModelClientTests.cs` with no-key request tests.
- Modify `tests/server/WechatRobot.UnitTests/Models/ModelConfigurationServiceTests.cs` with fingerprint and key-version tests.

### Persistence

- Modify `src/server/WechatRobot.Infrastructure/Persistence/Entities/ModelConfigEntity.cs` with normalized name, connection state, fingerprint, API-key version, and concurrency version.
- Modify `src/server/WechatRobot.Infrastructure/Persistence/Entities/RetrievalAuditEntity.cs` with a structured nullable model-configuration reference.
- Modify `src/server/WechatRobot.Infrastructure/Persistence/Configurations/MessagingConfigurations.cs` with unique indexes, generated default-type key, connection field limits, concurrency, and retrieval-audit reference mapping.
- Create the next EF migration under `src/server/WechatRobot.Infrastructure/Persistence/Migrations/` after `20260723103629_AddAdministrationSurfaces`.
- Modify `src/server/WechatRobot.Infrastructure/Persistence/Migrations/WechatRobotDbContextModelSnapshot.cs`.
- Modify `tests/server/WechatRobot.IntegrationTests/Persistence/MigrationTests.cs` with migration and legacy-row assertions.
- Create `tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationMySqlConstraintTests.cs` for real MySQL uniqueness constraints.

### Management service and HTTP API

- Create `src/server/WechatRobot.Infrastructure/Models/ModelConfigurationManager.cs` for validation, create/update, default switching, test-state transitions, key clearing, deletion protection, and audit writes.
- Modify `src/server/WechatRobot.Api/Models/ModelConfigurationEndpoints.cs` to expose ID routes and retain compatibility routes.
- Modify `src/server/WechatRobot.Api/Program.cs` to register the manager.
- Modify `tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationEndpointTests.cs` with ID-based endpoint coverage.
- Modify `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs` to persist the structured chat model reference in retrieval audits.
- Modify conversation persistence tests that construct `RetrievalAuditEntity`.

### Vue administration

- Modify `src/web/wechatrobot-admin/src/api/models.ts` with create, ID update, delete, test, clear-key, enable, and default operations.
- Create `src/web/wechatrobot-admin/src/api/models.spec.ts`.
- Create `src/web/wechatrobot-admin/src/views/models/ModelConfigurationDialog.vue`.
- Create `src/web/wechatrobot-admin/src/views/models/ModelConfigurationDialog.spec.ts`.
- Modify `src/web/wechatrobot-admin/src/views/models/ModelSettingsView.vue`.
- Create `src/web/wechatrobot-admin/src/views/models/ModelSettingsView.spec.ts`.
- Modify `tests/e2e/admin-workflows.spec.ts`.
- Modify `tests/e2e/test-server.mjs` so the deterministic E2E backend implements the new ID routes and connection states.

---

### Task 1: Support OpenAI-Compatible Providers Without API Keys

**Files:**

- Modify: `src/server/WechatRobot.Application/Models/IChatCompletionClient.cs`
- Modify: `src/server/WechatRobot.Application/Models/ModelConfigurationService.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Models/OpenAiCompatibleChatClient.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Models/OpenAiCompatibleEmbeddingClient.cs`
- Modify: `tests/server/WechatRobot.UnitTests/Models/OpenAiCompatibleModelClientTests.cs`
- Modify: `tests/server/WechatRobot.UnitTests/Models/ModelConfigurationServiceTests.cs`

**Interfaces:**

- Produces: `ModelProviderConfiguration(string BaseUrl, string Model, string? EncryptedApiKey, TimeSpan Timeout, int MaxRetries)`
- Produces: `string ComputeFingerprint(ModelConfigurationRecord record, string configurationType, int apiKeyVersion)`
- Produces: `string? ClearApiKey(string? existingEncryptedApiKey)`
- Consumes: existing `ISecretProtector`

- [ ] **Step 1: Add failing no-key and fingerprint tests**

Add tests with these assertions:

```csharp
[Fact]
public async Task Chat_request_omits_authorization_when_api_key_is_null()
{
    var handler = new RecordingHttpHandler(HttpStatusCode.OK,
        """{"choices":[{"message":{"content":"ok"}}]}""");
    var client = new OpenAiCompatibleChatClient(new HttpClient(handler), new ThrowingProtector());

    await client.CompleteAsync(
        new("https://local.test", "local-chat", null, TimeSpan.FromSeconds(5), 0),
        new([new("user", "ping")]),
        TestContext.Current.CancellationToken);

    Assert.Null(handler.Request!.Headers.Authorization);
}

[Fact]
public void Fingerprint_changes_when_api_key_version_changes()
{
    var record = new ModelConfigurationRecord(
        Guid.NewGuid(), "Local", "OpenAI compatible", "https://local.test",
        "model-a", null, 30, 0, false, false);

    Assert.NotEqual(
        service.ComputeFingerprint(record, "chat", 1),
        service.ComputeFingerprint(record, "chat", 2));
}
```

Use `ThrowingProtector` to prove the client does not attempt unprotect when the key is absent. Add the equivalent embedding test.

- [ ] **Step 2: Run the focused tests and verify failure**

Run:

```powershell
dotnet test --project tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-build -- --filter-class "*OpenAiCompatibleModelClientTests|*ModelConfigurationServiceTests"
```

Expected: FAIL because `EncryptedApiKey` is non-nullable, the clients always set `Authorization`, and `ComputeFingerprint` does not exist.

- [ ] **Step 3: Make the provider configuration key optional**

Change the record to:

```csharp
public sealed record ModelProviderConfiguration(
    string BaseUrl,
    string Model,
    string? EncryptedApiKey,
    TimeSpan Timeout,
    int MaxRetries);
```

In both provider clients, replace unconditional authorization assignment with:

```csharp
if (!string.IsNullOrWhiteSpace(configuration.EncryptedApiKey))
{
    request.Headers.Authorization = new AuthenticationHeaderValue(
        "Bearer",
        secretProtector.Unprotect(configuration.EncryptedApiKey));
}
```

Change `ToProviderConfiguration` so `null` remains valid rather than throwing.

- [ ] **Step 4: Add deterministic fingerprint and explicit clear behavior**

Implement:

```csharp
public string ComputeFingerprint(
    ModelConfigurationRecord record,
    string configurationType,
    int apiKeyVersion)
{
    var canonical = string.Join('\n',
        configurationType.Trim().ToUpperInvariant(),
        record.Provider.Trim().ToUpperInvariant(),
        record.BaseUrl.TrimEnd('/').ToUpperInvariant(),
        record.Model.Trim(),
        apiKeyVersion.ToString(CultureInfo.InvariantCulture));
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}

public string? ClearApiKey(string? existingEncryptedApiKey) => null;
```

The fingerprint must not decrypt or include the API key.

- [ ] **Step 5: Run focused and model-client regression tests**

Run:

```powershell
dotnet test --project tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj -- --filter-class "*OpenAiCompatibleModelClientTests|*ModelConfigurationServiceTests"
```

Expected: all selected tests PASS; recording handlers show no authorization header for null keys and unchanged bearer behavior for saved keys.

- [ ] **Step 6: Commit**

```powershell
git add src/server/WechatRobot.Application/Models/IChatCompletionClient.cs src/server/WechatRobot.Application/Models/ModelConfigurationService.cs src/server/WechatRobot.Infrastructure/Models/OpenAiCompatibleChatClient.cs src/server/WechatRobot.Infrastructure/Models/OpenAiCompatibleEmbeddingClient.cs tests/server/WechatRobot.UnitTests/Models/OpenAiCompatibleModelClientTests.cs tests/server/WechatRobot.UnitTests/Models/ModelConfigurationServiceTests.cs
git commit -m "feat: support keyless OpenAI-compatible models"
```

### Task 2: Persist Stable Names, Connection Provenance, and Default Constraints

**Files:**

- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/ModelConfigEntity.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/RetrievalAuditEntity.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Configurations/MessagingConfigurations.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/20260724090000_HardenModelConfigurationManagement.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/20260724090000_HardenModelConfigurationManagement.Designer.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/WechatRobotDbContextModelSnapshot.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Persistence/MigrationTests.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationMySqlConstraintTests.cs`

**Interfaces:**

- Produces: `ModelConnectionStatus` constants `Untested`, `Succeeded`, `Failed`
- Produces: persistent properties `NormalizedName`, `DefaultConfigurationType`, `ConnectionStatus`, `LastTestedAtUtc`, `LastTestFailureSummary`, `TestedConfigurationFingerprint`, `ApiKeyVersion`, `Version`
- Produces: nullable `RetrievalAuditEntity.ModelConfigurationId`
- Consumes: `20260723103629_AddAdministrationSurfaces` as the migration predecessor

- [ ] **Step 1: Add failing migration and MySQL constraint tests**

Add a legacy-row migration assertion:

```csharp
Assert.Equal("LEGACY CHAT", migrated.NormalizedName);
Assert.Equal(ModelConnectionStatus.Untested, migrated.ConnectionStatus);
Assert.Null(migrated.TestedConfigurationFingerprint);
Assert.Equal(0, migrated.ApiKeyVersion);
```

Add real-MySQL tests proving:

```csharp
await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(database,
    Config("Primary", "PRIMARY", "chat"),
    Config("primary", "PRIMARY", "embedding")));

await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(database,
    DefaultConfig("chat", "one"),
    DefaultConfig("chat", "two")));

static ModelConfigEntity Config(string name, string normalizedName, string type) => new()
{
    Name = name,
    NormalizedName = normalizedName,
    Provider = "fake",
    ConfigurationType = type,
    BaseUrl = "https://fake.test",
    Model = "fake"
};

static ModelConfigEntity DefaultConfig(string type, string name)
{
    var entity = Config(name, name.ToUpperInvariant(), type);
    entity.IsDefault = true;
    return entity;
}

static async Task SaveAsync(
    WechatRobotDbContext database,
    params ModelConfigEntity[] entities)
{
    database.ModelConfigs.AddRange(entities);
    await database.SaveChangesAsync(TestContext.Current.CancellationToken);
}
```

The first test intentionally proves names are globally unique across both types. The second proves one default per type.

- [ ] **Step 2: Run tests and verify failure**

Run:

```powershell
dotnet test --project tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*MigrationTests|*ModelConfigurationMySqlConstraintTests"
```

Expected: FAIL because the new columns and constraints do not exist.

- [ ] **Step 3: Extend entities and EF configuration**

Add:

```csharp
public static class ModelConnectionStatus
{
    public const string Untested = "Untested";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}
```

Add these properties to `ModelConfigEntity`:

```csharp
public string NormalizedName { get; set; } = string.Empty;
public string? DefaultConfigurationType { get; private set; }
public string ConnectionStatus { get; set; } = ModelConnectionStatus.Untested;
public DateTime? LastTestedAtUtc { get; set; }
public string? LastTestFailureSummary { get; set; }
public string? TestedConfigurationFingerprint { get; set; }
public int ApiKeyVersion { get; set; }
public int Version { get; set; }
```

Configure `DefaultConfigurationType` as a stored generated column:

```csharp
builder.Property(entity => entity.DefaultConfigurationType)
    .HasMaxLength(32)
    .HasComputedColumnSql(
        "CASE WHEN `IsDefault` = 1 THEN `ConfigurationType` ELSE NULL END",
        stored: true);
builder.HasIndex(entity => entity.NormalizedName).IsUnique();
builder.HasIndex(entity => entity.DefaultConfigurationType).IsUnique();
builder.Property(entity => entity.Version).IsConcurrencyToken();
```

Add a nullable retrieval-audit FK with `DeleteBehavior.Restrict`.

- [ ] **Step 4: Generate and inspect the migration**

Run:

```powershell
dotnet ef migrations add HardenModelConfigurationManagement --project src/server/WechatRobot.Infrastructure --startup-project src/server/WechatRobot.Api
```

Rename the generated migration and its designer to the fixed
`20260724090000_HardenModelConfigurationManagement` ID, update the designer's
`[Migration]` attribute to that exact ID, and edit the migration so it:

1. Adds nullable columns first.
2. Backfills `NormalizedName = UPPER(TRIM(Name))`.
3. Runs a temporary stored-procedure preflight that raises SQLSTATE `45000`
   with message `Duplicate normalized model configuration names exist.` when
   `GROUP BY UPPER(TRIM(Name)) HAVING COUNT(*) > 1` returns a row.
4. Runs the same preflight with message
   `Multiple default model configurations exist for one type.` when
   `WHERE IsDefault = 1 GROUP BY ConfigurationType HAVING COUNT(*) > 1`
   returns a row.
5. Makes `NormalizedName` required.
6. Creates the unique normalized-name and generated default-type indexes.
7. Backfills `RetrievalAuditEntity.ModelConfigurationId` from `InputSummaryJson.ModelConfigurationId` only when it is a valid existing GUID.

- [ ] **Step 5: Apply the migration to the fixture and rerun tests**

Run:

```powershell
dotnet test --project tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*MigrationTests|*ModelConfigurationMySqlConstraintTests"
```

Expected: all selected tests PASS on MySQL 8.4.

- [ ] **Step 6: Commit**

```powershell
git add src/server/WechatRobot.Infrastructure/Persistence/Entities/ModelConfigEntity.cs src/server/WechatRobot.Infrastructure/Persistence/Entities/RetrievalAuditEntity.cs src/server/WechatRobot.Infrastructure/Persistence/Configurations/MessagingConfigurations.cs src/server/WechatRobot.Infrastructure/Persistence/Migrations tests/server/WechatRobot.IntegrationTests/Persistence/MigrationTests.cs tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationMySqlConstraintTests.cs
git commit -m "feat: harden model configuration persistence"
```

### Task 3: Add ID-Based CRUD and Rename Semantics

**Files:**

- Create: `src/server/WechatRobot.Infrastructure/Models/ModelConfigurationManager.cs`
- Modify: `src/server/WechatRobot.Api/Models/ModelConfigurationEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Program.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationEndpointTests.cs`

**Interfaces:**

- Produces: `CreateModelConfigurationCommand`
- Produces: `UpdateModelConfigurationCommand`
- Produces: `ModelConfigurationManagementResult`
- Produces: `CreateAsync`, `UpdateAsync`, and `ListAsync`
- Consumes: Task 1 fingerprint/key behavior and Task 2 persistence fields

- [ ] **Step 1: Add failing create, rename, validation, and concurrency tests**

Add endpoint tests for:

```csharp
var create = await client.PostAsJsonAsync("/api/admin/model-configurations", new
{
    name = "  Local Chat  ",
    provider = "OpenAI compatible",
    configurationType = "chat",
    baseUrl = "http://127.0.0.1:11434",
    model = "qwen",
    apiKey = (string?)null,
    timeoutSeconds = 30,
    maxRetries = 0
}, token);
create.StatusCode.Should().Be(HttpStatusCode.Created);

using var created = JsonDocument.Parse(
    await create.Content.ReadAsStringAsync(token));
var id = created.RootElement.GetProperty("id").GetGuid();
var version = created.RootElement.GetProperty("version").GetInt32();
var update = await client.PutAsJsonAsync(
    $"/api/admin/model-configurations/{id}",
    new
    {
        name = "Renamed Chat",
        provider = "OpenAI compatible",
        configurationType = "chat",
        baseUrl = "http://127.0.0.1:11434",
        model = "qwen",
        apiKey = (string?)null,
        timeoutSeconds = 30,
        maxRetries = 0,
        version
    },
    token);
update.EnsureSuccessStatusCode();
using var updated = JsonDocument.Parse(
    await update.Content.ReadAsStringAsync(token));
Assert.Equal(id, updated.RootElement.GetProperty("id").GetGuid());
```

Also assert:

- blank and 129-character names return `400`;
- `CHAT` as a type returns normalized `chat`;
- conflicting normalized name returns `409` with code `model_name_conflict`;
- stale version returns `409` with code `model_concurrency_conflict`;
- create ignores any client attempt to set `IsEnabled` or `IsDefault`.

- [ ] **Step 2: Run the focused endpoint tests and verify failure**

Run:

```powershell
dotnet test --project tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*ModelConfigurationEndpointTests"
```

Expected: FAIL because POST and ID-based PUT routes do not exist.

- [ ] **Step 3: Implement the management service contracts**

Create focused records:

```csharp
public sealed record CreateModelConfigurationCommand(
    string Name, string Provider, string ConfigurationType, string BaseUrl,
    string Model, string? ApiKey, int TimeoutSeconds, int MaxRetries);

public sealed record UpdateModelConfigurationCommand(
    string Name, string Provider, string ConfigurationType, string BaseUrl,
    string Model, string? ApiKey, int TimeoutSeconds, int MaxRetries, int Version);
```

`CreateAsync` must trim values, calculate `NormalizedName`, force disabled/non-default/untested, protect an optional key, and return a conflict result for unique-name violations.

`UpdateAsync` must:

- find by ID;
- compare `Version`;
- preserve the key when submitted key is blank;
- increment `ApiKeyVersion` only when a nonblank replacement is submitted;
- invalidate test state when type, provider, Base URL, model, or key version changes;
- preserve ID and created time;
- increment `Version`.

- [ ] **Step 4: Map thin ID endpoints and safe response contracts**

Map:

```csharp
group.MapPost("", CreateAsync);
group.MapPut("{id:guid}", UpdateByIdAsync);
```

Return `201 Created` with `/api/admin/model-configurations/{id}` for create. Add these response properties:

```csharp
Guid Id,
string Name,
string Provider,
string ConfigurationType,
string BaseUrl,
string Model,
int TimeoutSeconds,
int MaxRetries,
bool IsEnabled,
bool IsDefault,
string ConnectionStatus,
DateTime? LastTestedAtUtc,
string? LastTestFailureSummary,
bool HasApiKey,
string? LastFour,
int Version
```

Never serialize `EncryptedApiKey`, fingerprint, or API-key version.

- [ ] **Step 5: Retain and test the old name route**

Keep `PUT /{name}` and route it through the manager. The compatibility route may create a missing record, but it must normalize names and produce the same safe response.

Update route assertions so both compatibility and ID routes require the Admin policy.

- [ ] **Step 6: Run tests and commit**

Run:

```powershell
dotnet test --project tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*ModelConfigurationEndpointTests"
```

Expected: all selected tests PASS.

Commit:

```powershell
git add src/server/WechatRobot.Infrastructure/Models/ModelConfigurationManager.cs src/server/WechatRobot.Api/Models/ModelConfigurationEndpoints.cs src/server/WechatRobot.Api/Program.cs tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationEndpointTests.cs
git commit -m "feat: add ID-based model configuration CRUD"
```

### Task 4: Gate Enablement and Defaults on Current Connection Tests

**Files:**

- Modify: `src/server/WechatRobot.Infrastructure/Models/ModelConfigurationManager.cs`
- Modify: `src/server/WechatRobot.Api/Models/ModelConfigurationEndpoints.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationEndpointTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationMySqlConstraintTests.cs`

**Interfaces:**

- Produces: `TestConnectionAsync(Guid id, string actor, CancellationToken token)`
- Produces: `SetEnabledAsync(Guid id, bool enabled, int version, string actor, CancellationToken token)`
- Produces: `SetDefaultAsync(Guid id, bool isDefault, int version, string actor, CancellationToken token)`
- Consumes: `IChatCompletionClient`, `IEmbeddingClient`, fingerprint fields, and unique generated default constraint

- [ ] **Step 1: Add failing state-transition tests**

Cover:

```csharp
Assert.Equal(HttpStatusCode.Conflict,
    (await client.PostAsJsonAsync($"/api/admin/model-configurations/{id}/enabled",
        new { enabled = true, version }, token)).StatusCode);

var tested = await client.PostAsync(
    $"/api/admin/model-configurations/{id}/test-connection", null, token);
tested.EnsureSuccessStatusCode();

var enabled = await client.PostAsJsonAsync(
    $"/api/admin/model-configurations/{id}/enabled",
    new { enabled = true, version = testedVersion }, token);
enabled.EnsureSuccessStatusCode();
```

Also test:

- failed provider call persists `Failed` and a sanitized summary;
- successful test persists `Succeeded`, time, and current fingerprint;
- changing model or key resets state to `Untested`;
- setting a new chat default clears the old chat default but not embedding default;
- clearing a default leaves the type with no default and keeps the configuration enabled;
- two concurrent default attempts end with exactly one default;
- default cannot be disabled without first selecting another default.

- [ ] **Step 2: Run focused tests and verify failure**

Run:

```powershell
dotnet test --project tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*ModelConfigurationEndpointTests|*ModelConfigurationMySqlConstraintTests"
```

Expected: FAIL because connection state is not persisted and enable/default transition routes do not exist.

- [ ] **Step 3: Implement connection testing and state invalidation**

The success path must execute:

```csharp
entity.ConnectionStatus = ModelConnectionStatus.Succeeded;
entity.LastTestedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
entity.LastTestFailureSummary = null;
entity.TestedConfigurationFingerprint =
    modelConfigurationService.ComputeFingerprint(
        ToRecord(entity),
        entity.ConfigurationType,
        entity.ApiKeyVersion);
entity.Version++;
```

The failure path sets `Failed`, stores one of the stable summaries `timeout`, `http_error`, or `invalid_response`, clears the tested fingerprint, increments the version, and never stores the exception message or provider body.

- [ ] **Step 4: Implement transactional enable and default switching**

Before enable/default, compare the persisted fingerprint with a freshly computed fingerprint. Return conflict code `model_test_required` when it differs or status is not `Succeeded`.

For setting a default:

1. Begin a database transaction.
2. Clear `IsDefault` on the current default of the same type.
3. Set target enabled and default.
4. Save and commit.
5. Translate the generated-column unique-index violation to `model_default_conflict`.

For clearing a default, set only the target's `IsDefault` to false in a
transaction. Clearing a default does not disable it and does not require
another configuration to exist.

- [ ] **Step 5: Map transition endpoints**

Map:

```csharp
group.MapPost("{id:guid}/test-connection", TestConnectionByIdAsync);
group.MapPost("{id:guid}/enabled", SetEnabledAsync);
group.MapPost("{id:guid}/default", SetDefaultAsync);
```

The default request body is `{ isDefault: boolean, version: number }`.
Retain `POST /{name}/test-connection` as a compatibility adapter.

- [ ] **Step 6: Run tests and commit**

Run:

```powershell
dotnet test --project tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*ModelConfigurationEndpointTests|*ModelConfigurationMySqlConstraintTests"
```

Expected: all selected tests PASS.

Commit:

```powershell
git add src/server/WechatRobot.Infrastructure/Models/ModelConfigurationManager.cs src/server/WechatRobot.Api/Models/ModelConfigurationEndpoints.cs tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationEndpointTests.cs tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationMySqlConstraintTests.cs
git commit -m "feat: gate model activation on connection tests"
```

### Task 5: Add Explicit Key Clearing, Deletion Protection, and Administration Audit

**Files:**

- Modify: `src/server/WechatRobot.Infrastructure/Models/ModelConfigurationManager.cs`
- Modify: `src/server/WechatRobot.Api/Models/ModelConfigurationEndpoints.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationEndpointTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Conversations/ConversationAuditQueryTests.cs`

**Interfaces:**

- Produces: `ClearApiKeyAsync(Guid id, int version, string actor, CancellationToken token)`
- Produces: `DeleteAsync(Guid id, int version, string actor, CancellationToken token)`
- Produces: `ModelConfigurationReferenceSummary(int RetrievalAuditCount)`
- Consumes: existing `AdministrationAuditEntity` and `WechatRobotDbContext.AdministrationAudits`

- [ ] **Step 1: Add failing key-clear, deletion, reference, and audit tests**

Assert:

- `DELETE /{id}/api-key` clears ciphertext, increments API-key version, and resets test state;
- clearing an already absent key succeeds idempotently without exposing a key;
- deleting a default returns `409 model_default_delete_blocked`;
- deleting a configuration referenced by retrieval audit returns `409 model_reference_delete_blocked` with `retrievalAuditCount`;
- deleting an unreferenced non-default returns `204`;
- create, rename, test, enable, default, clear-key, and delete add audit rows;
- serialized audit details contain neither submitted plaintext, encrypted key, nor last-four metadata.

Use:

```csharp
Assert.DoesNotContain("provider-secret", audit.SanitizedDetailJson, StringComparison.Ordinal);
Assert.DoesNotContain(storedCiphertext, audit.SanitizedDetailJson, StringComparison.Ordinal);
```

- [ ] **Step 2: Run the focused tests and verify failure**

Run:

```powershell
dotnet test --project tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*ModelConfigurationEndpointTests|*ConversationAuditQueryTests"
```

Expected: FAIL because clear/delete endpoints, structured references, and model administration audits are absent.

- [ ] **Step 3: Persist the chat model reference**

When creating `RetrievalAuditEntity`, assign:

```csharp
ModelConfigurationId = request.ModelConfigurationId == Guid.Empty
    ? null
    : request.ModelConfigurationId
```

Preserve the existing sanitized JSON for compatibility, but use the structured column for reference checks.

- [ ] **Step 4: Implement clear and delete rules**

`ClearApiKeyAsync` must set ciphertext to null, increment `ApiKeyVersion`, invalidate test state, increment `Version`, and write action `model_api_key_cleared`.

`DeleteAsync` must:

1. reject default configurations;
2. count `RetrievalAudits` by `ModelConfigurationId`;
3. return a structured conflict when count is nonzero;
4. delete and write audit action `model_configuration_deleted` in one transaction.

- [ ] **Step 5: Add sanitized audit writes to every management action**

Use a single helper:

```csharp
private void Audit(string actor, string action, ModelConfigEntity entity, object detail)
{
    database.AdministrationAudits.Add(new AdministrationAuditEntity
    {
        Actor = string.IsNullOrWhiteSpace(actor) ? "unknown" : actor,
        Action = action,
        TargetType = "model_configuration",
        TargetId = entity.Id.ToString(),
        SanitizedDetailJson = JsonSerializer.Serialize(detail),
        CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime
    });
}
```

Only pass allow-listed fields such as name, type, enabled/default state, connection status, and changed field names.

- [ ] **Step 6: Map endpoints, rerun tests, and commit**

Map:

```csharp
group.MapDelete("{id:guid}/api-key", ClearApiKeyAsync);
group.MapDelete("{id:guid}", DeleteAsync);
```

Run:

```powershell
dotnet test --project tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class "*ModelConfigurationEndpointTests|*ConversationAuditQueryTests"
```

Expected: all selected tests PASS.

Commit:

```powershell
git add src/server/WechatRobot.Infrastructure/Models/ModelConfigurationManager.cs src/server/WechatRobot.Api/Models/ModelConfigurationEndpoints.cs src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationEndpointTests.cs tests/server/WechatRobot.IntegrationTests/Conversations/ConversationAuditQueryTests.cs
git commit -m "feat: protect and audit model configuration deletion"
```

### Task 6: Add the Typed Vue API and Reusable Model Dialog

**Files:**

- Modify: `src/web/wechatrobot-admin/src/api/models.ts`
- Create: `src/web/wechatrobot-admin/src/api/models.spec.ts`
- Create: `src/web/wechatrobot-admin/src/views/models/ModelConfigurationDialog.vue`
- Create: `src/web/wechatrobot-admin/src/views/models/ModelConfigurationDialog.spec.ts`

**Interfaces:**

- Produces: `ModelConfigurationDraft`
- Produces: `ModelConfigurationApiError`
- Produces: `ModelApi.create`, `update`, `testConnection`, `setEnabled`, `setDefault`, `clearApiKey`, `delete`
- Produces: dialog emits `save` and `clear-api-key`
- Consumes: Task 3–5 response contracts

- [ ] **Step 1: Add failing API URL and payload tests**

Mock `apiClient` and assert:

```typescript
await modelApi.update('11111111-1111-1111-1111-111111111111', {
  name: 'Renamed',
  provider: 'OpenAI compatible',
  configurationType: 'chat',
  baseUrl: 'http://127.0.0.1:11434',
  model: 'qwen',
  apiKey: undefined,
  timeoutSeconds: 30,
  maxRetries: 0,
  version: 2
});

expect(apiClient.put).toHaveBeenCalledWith(
  '/api/admin/model-configurations/11111111-1111-1111-1111-111111111111',
  expect.objectContaining({ name: 'Renamed', version: 2 })
);
```

Cover every new ID route and assert no key is added to query strings or logs.
`setDefault(id, isDefault, version)` must send both `isDefault` and `version`
in the JSON body so the UI can explicitly clear a default before deletion.

- [ ] **Step 2: Add failing dialog validation and emit tests**

Mount the dialog and assert:

- blank name blocks save and displays “请输入配置名称”;
- invalid URL blocks save;
- type options are only chat and embedding;
- API Key is blank when editing and help text says blank preserves;
- valid input emits one `save` with the complete draft;
- existing-key edit displays a separate “清除密钥” button and confirmation event.

- [ ] **Step 3: Run focused Vitest files and verify failure**

Run:

```powershell
npm --prefix src/web/wechatrobot-admin test -- src/api/models.spec.ts src/views/models/ModelConfigurationDialog.spec.ts
```

Expected: FAIL because ID methods and the dialog do not exist.

- [ ] **Step 4: Implement typed API contracts**

Define:

```typescript
export type ModelConfigurationType = 'chat' | 'embedding';
export type ModelConnectionStatus = 'Untested' | 'Succeeded' | 'Failed';

export interface ModelConfigurationDraft {
  name: string;
  provider: string;
  configurationType: ModelConfigurationType;
  baseUrl: string;
  model: string;
  apiKey?: string;
  timeoutSeconds: number;
  maxRetries: number;
  version?: number;
}
```

Extend `ModelConfiguration` with connection state and version. Implement exact ID paths from the design.

- [ ] **Step 5: Implement the Element Plus dialog**

Use `ElDialog`, `ElForm`, `ElFormItem`, `ElInput`, `ElSelect`, `ElOption`, `ElInputNumber`, `ElButton`, and `ElAlert`.

Initialize new drafts with:

```typescript
{
  name: '',
  provider: 'OpenAI 兼容',
  configurationType: 'chat',
  baseUrl: '',
  model: '',
  apiKey: '',
  timeoutSeconds: 30,
  maxRetries: 0
}
```

Keep dialog state isolated from the list object by cloning props on open.

- [ ] **Step 6: Run tests, typecheck, and commit**

Run:

```powershell
npm --prefix src/web/wechatrobot-admin test -- src/api/models.spec.ts src/views/models/ModelConfigurationDialog.spec.ts
npm --prefix src/web/wechatrobot-admin run typecheck
```

Expected: selected tests PASS and typecheck exits 0.

Commit:

```powershell
git add src/web/wechatrobot-admin/src/api/models.ts src/web/wechatrobot-admin/src/api/models.spec.ts src/web/wechatrobot-admin/src/views/models/ModelConfigurationDialog.vue src/web/wechatrobot-admin/src/views/models/ModelConfigurationDialog.spec.ts
git commit -m "feat: add model configuration editor dialog"
```

### Task 7: Replace the Empty Placeholder with Cards and Complete E2E Acceptance

**Files:**

- Modify: `src/web/wechatrobot-admin/src/views/models/ModelSettingsView.vue`
- Create: `src/web/wechatrobot-admin/src/views/models/ModelSettingsView.spec.ts`
- Modify: `tests/e2e/admin-workflows.spec.ts`
- Modify: `tests/e2e/test-server.mjs`

**Interfaces:**

- Consumes: Task 6 dialog and typed API
- Produces: grouped chat/embedding cards and complete admin workflow

- [ ] **Step 1: Add failing empty-state and card-action component tests**

Assert:

```typescript
expect(wrapper.get('[data-testid="create-model"]').text()).toContain('新增模型配置');
await wrapper.get('[data-testid="create-model"]').trigger('click');
expect(wrapper.get('[role="dialog"]').exists()).toBe(true);
```

Cover:

- empty list still shows the create button;
- cards are grouped under “对话模型” and “向量模型”;
- card displays enabled/default/test status and masked key metadata;
- untested card disables enable/default actions;
- successful test refreshes card state;
- rename replaces the same card by ID rather than appending;
- delete confirmation renders server conflict reason;
- clear-key confirmation calls the explicit endpoint.

- [ ] **Step 2: Run the focused view test and verify failure**

Run:

```powershell
npm --prefix src/web/wechatrobot-admin test -- src/views/models/ModelSettingsView.spec.ts
```

Expected: FAIL because the page still renders the placeholder and inline edit cards.

- [ ] **Step 3: Implement grouped summary cards**

Replace inline forms with:

- page header actions;
- two grouped sections;
- connection status tags using `info`, `success`, and `danger`;
- summary rows for URL, model, key state, timeout, and retries;
- action buttons with stable `data-testid` values based on immutable ID;
- the reusable dialog for create/edit.

Keep errors visible without discarding the current list. Map stable server conflict codes to the exact Chinese messages confirmed in the design.

- [ ] **Step 4: Update the deterministic E2E server**

Seed model records with IDs, versions, and connection states. Implement deterministic routes for:

- create;
- update by ID;
- successful connection test;
- enable;
- set default;
- clear key;
- protected and successful delete.

No E2E route may call an external model provider.

- [ ] **Step 5: Replace the old model E2E slice**

Implement this flow:

1. Click “新增模型配置”.
2. Create a disabled chat configuration.
3. Assert status “待测试”.
4. Test connection and assert “测试成功”.
5. Enable and set default.
6. Rename it.
7. Assert the renamed card retains the same `data-testid` containing the immutable ID.
8. Reload and verify enabled/default state remains.

- [ ] **Step 6: Run frontend and E2E validation**

Run:

```powershell
npm --prefix src/web/wechatrobot-admin test
npm --prefix src/web/wechatrobot-admin run typecheck
npm --prefix src/web/wechatrobot-admin run build
npm --prefix tests/e2e test -- admin-workflows.spec.ts
```

Expected: all Vitest and Playwright tests PASS; typecheck and build exit 0.

- [ ] **Step 7: Commit**

```powershell
git add src/web/wechatrobot-admin/src/views/models/ModelSettingsView.vue src/web/wechatrobot-admin/src/views/models/ModelSettingsView.spec.ts tests/e2e/admin-workflows.spec.ts tests/e2e/test-server.mjs
git commit -m "feat: complete model configuration administration"
```

### Task 8: Full Regression and Runtime Acceptance

**Files:**

- Modify only files required by failures directly caused by Tasks 1–7.
- Do not include unrelated administration-surface or user changes in corrective commits.

**Interfaces:**

- Consumes: all previous task outputs
- Produces: verified build, migration, API, Vue, Worker, and E2E evidence

- [ ] **Step 1: Run formatting and compile checks**

Run:

```powershell
dotnet build WechatRobot.slnx --nologo
npm --prefix src/web/wechatrobot-admin run typecheck
npm --prefix src/web/wechatrobot-admin run build
```

Expected: all commands exit 0 with no compiler errors.

- [ ] **Step 2: Run all server tests**

Run:

```powershell
dotnet test --solution WechatRobot.slnx --no-build
```

Expected: all non-environmental tests PASS; only explicitly gated real-provider tests may be skipped.

- [ ] **Step 3: Run all frontend and E2E tests**

Run:

```powershell
npm --prefix src/web/wechatrobot-admin test
npm --prefix tests/e2e test
```

Expected: all Vitest and Playwright tests PASS.

- [ ] **Step 4: Apply migration to the local development database**

Run:

```powershell
dotnet ef database update --project src/server/WechatRobot.Infrastructure --startup-project src/server/WechatRobot.Api --no-build
```

Expected: `HardenModelConfigurationManagement` applies successfully and no duplicate-name/default preflight signal is raised.

- [ ] **Step 5: Restart and verify the real local stack**

Run:

```powershell
./scripts/stop-dev.ps1
./scripts/start-dev.ps1 -SkipDependencies -StartupTimeoutSeconds 120
```

Verify:

```powershell
Invoke-WebRequest http://127.0.0.1:5268/health/live -UseBasicParsing
Invoke-WebRequest http://127.0.0.1:5173/ -UseBasicParsing
```

Expected: both return HTTP 200 and `.dev/worker.ready` is newer than 10 seconds.

- [ ] **Step 6: Perform local browser acceptance**

Using the existing local admin account:

1. Open `/models`.
2. Create a keyless disabled configuration.
3. Confirm it cannot be enabled before testing.
4. Use a deterministic local fake provider to pass the connection test.
5. Enable, set default, rename, reload, and verify the same ID.
6. Confirm no key or provider response body appears in browser-visible data or administration audit JSON.

- [ ] **Step 7: Commit only necessary verification fixes**

If verification required source changes, stage only the directly related files and commit:

```powershell
git commit -m "test: verify model configuration management"
```

If no source changes were required, do not create an empty commit.
