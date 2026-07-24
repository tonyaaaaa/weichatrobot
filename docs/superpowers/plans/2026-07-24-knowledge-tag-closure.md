# Knowledge Tag Closure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a complete knowledge-tag management API and page, enforce safe lifecycle and concurrency rules, and replace every remaining tag-UUID text field with a real tag selector.

**Architecture:** `KnowledgeTagManager` owns normalization, pagination, optimistic concurrency, reference checks, and sanitized administration audits. Minimal API endpoints expose paged management records plus a compact enabled-options query. The Vue client uses one reusable `KnowledgeTagSelector` in document indexing and knowledge review, while the existing group selector remains group-aware so it can display disabled historical bindings.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, Entity Framework Core/MySQL, xUnit v3 with Microsoft Testing Platform, Vue 3, TypeScript, Element Plus, Vitest, Vite.

## Global Constraints

- Work in `H:\Codex\WechatRobot\.worktrees\codex-wechatrobot-mvp` on `codex/wechatrobot-mvp`.
- Human handoff, Enterprise WeChat member sync, agent selectors, proactive handoff, and handoff pause-policy UI remain deferred.
- `KnowledgeTagEntity.NormalizedName` remains unique and is always `Name.Trim().ToUpperInvariant()`.
- Create and update names are 1–128 characters after trimming.
- Every update, enable/disable, and delete request carries `ExpectedVersion`.
- A concurrency mismatch returns HTTP 409 with `error: "knowledge-tag-concurrency-conflict"` and the current tag record.
- Referenced tags cannot be physically deleted; they can be disabled.
- Reference checks include `GroupProfileTagEntity`, `KnowledgeChunkTagEntity`, `KnowledgeReviewEntity.TagIdsJson`, and `KnowledgeIndexJobEntity.PendingTagIdsJson`.
- Disabled tags remain visible in management and historical group bindings, but cannot be newly bound, indexed, or selected.
- Only an Admin may physically delete a tag, and the frontend requires a second confirmation.
- Admin and KnowledgeOperator may list, create, edit, enable, and disable tags.
- Every mutation writes one sanitized `AdministrationAuditEntity`; audit JSON contains tag metadata and versions, never document text, model input, message content, credentials, or authorization headers.
- Existing group configuration remains authoritative for disabled bound-tag display; do not replace it with the generic options endpoint.
- Do not stop the running API or Worker. Use isolated build outputs if default binaries are locked.

---

## File and Interface Map

### Backend

- Create `src/server/WechatRobot.Application/Knowledge/KnowledgeTagContracts.cs`
  - Shared records and mutation result types.
- Create `src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeTagManager.cs`
  - Pagination, CRUD, normalization, reference detection, concurrency, and audit writes.
- Create `src/server/WechatRobot.Api/Knowledge/KnowledgeTagEndpoints.cs`
  - Authorization, HTTP contracts, validation/error mapping.
- Modify `src/server/WechatRobot.Api/Program.cs`
  - Register `KnowledgeTagManager` and map endpoints.
- Create `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeTagEndpointTests.cs`
  - In-memory HTTP behavior, authorization, concurrency, auditing, and reference blocking.
- Create `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeTagManagerTests.cs`
  - Direct manager query, mutation, concurrency, reference, and audit behavior.
- Create `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeTagMySqlTests.cs`
  - Real MySQL unique-name race and delete-reference enforcement.
- Modify `tests/server/WechatRobot.IntegrationTests/Groups/GroupConfigurationTests.cs`
  - Preserve disabled historical binding behavior and reject new disabled bindings.

### Frontend

- Create `src/web/wechatrobot-admin/src/api/knowledgeTags.ts`
  - Typed management and selector API.
  - Map UI filters to the backend query names `query`, `isEnabled`, and `isGlobalPublic`.
- Create `src/web/wechatrobot-admin/src/api/knowledgeTags.spec.ts`
  - Lock query-name mapping and versioned mutation routes.
- Create `src/web/wechatrobot-admin/src/components/knowledge/KnowledgeTagSelector.vue`
  - Reusable enabled-tag multi-select.
- Create `src/web/wechatrobot-admin/src/components/knowledge/KnowledgeTagSelector.spec.ts`
  - Loading, selection, disabled historical display, empty and failure states.
- Modify `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeTagsView.vue`
  - Paged CRUD management UI.
- Create `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeTagsView.spec.ts`
  - Management interactions, concurrency refresh, delete confirmation, role behavior.
- Modify `src/web/wechatrobot-admin/src/views/knowledge/DocumentDetailView.vue`
  - Replace manual UUID text with `KnowledgeTagSelector`.
- Modify `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeReviewView.vue`
  - Replace manual UUID text with `KnowledgeTagSelector`.
- Modify `src/web/wechatrobot-admin/src/views/task16-operational.spec.ts`
  - Update document/review expectations from text parsing to selected IDs.
- Modify `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts`
  - Lock the existing disabled-bound/enabled-new selector behavior.
- Delete `src/web/wechatrobot-admin/src/utils/knowledgeTagIds.ts`
  - No production page may parse manually entered tag UUIDs after this phase.

---

### Task 1: Define the tag contracts and paged read model

**Files:**

- Create: `src/server/WechatRobot.Application/Knowledge/KnowledgeTagContracts.cs`
- Create: `src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeTagManager.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeTagManagerTests.cs`

**Interfaces:**

- Consumes: `WechatRobotDbContext.KnowledgeTags`.
- Produces:

```csharp
public sealed record KnowledgeTagRecord(
    Guid Id,
    string Name,
    bool IsEnabled,
    bool IsGlobalPublic,
    int Version,
    DateTime CreatedAtUtc);

public sealed record KnowledgeTagPage(
    IReadOnlyList<KnowledgeTagRecord> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record KnowledgeTagOption(Guid Id, string Name, bool IsGlobalPublic);

public sealed record KnowledgeTagDraft(string Name, bool IsGlobalPublic);
public sealed record KnowledgeTagUpdate(string Name, bool IsGlobalPublic, int ExpectedVersion);
public sealed record KnowledgeTagStateUpdate(bool IsEnabled, int ExpectedVersion);

public enum KnowledgeTagMutationStatus
{
    Succeeded,
    InvalidInput,
    NotFound,
    NameConflict,
    ConcurrencyConflict,
    Referenced
}

public sealed record KnowledgeTagMutationResult(
    KnowledgeTagMutationStatus Status,
    KnowledgeTagRecord? Tag = null,
    KnowledgeTagReferenceSummary? References = null,
    string? Error = null);

public sealed record KnowledgeTagReferenceSummary(
    int Groups,
    int Chunks,
    int Reviews,
    int IndexJobs)
{
    public bool IsReferenced => Groups + Chunks + Reviews + IndexJobs > 0;
}
```

- [ ] **Step 1: Write failing paged-query manager tests**

```csharp
[Fact]
public async Task List_filters_by_text_and_state_with_stable_name_then_id_order()
{
    await using var database = NewDatabase();
    database.KnowledgeTags.AddRange(
        new KnowledgeTagEntity { Name = "产品", NormalizedName = "产品", IsEnabled = true },
        new KnowledgeTagEntity { Name = "售后", NormalizedName = "售后", IsEnabled = false },
        new KnowledgeTagEntity { Name = "公开知识", NormalizedName = "公开知识", IsEnabled = true, IsGlobalPublic = true });
    await database.SaveChangesAsync(TestContext.Current.CancellationToken);
    var manager = new KnowledgeTagManager(database);

    var page = await manager.ListAsync(
        "知",
        isEnabled: true,
        isGlobalPublic: null,
        page: 1,
        pageSize: 20,
        TestContext.Current.CancellationToken);

    var tag = Assert.Single(page.Items);
    Assert.Equal("公开知识", tag.Name);
    Assert.True(tag.IsEnabled);
    Assert.True(tag.IsGlobalPublic);
    Assert.Equal(1, page.Total);
}

[Fact]
public async Task Options_returns_only_enabled_tags()
{
    await using var database = NewDatabase();
    database.KnowledgeTags.AddRange(
        new KnowledgeTagEntity { Name = "Enabled", NormalizedName = "ENABLED", IsEnabled = true },
        new KnowledgeTagEntity { Name = "Disabled", NormalizedName = "DISABLED", IsEnabled = false });
    await database.SaveChangesAsync(TestContext.Current.CancellationToken);

    var options = await new KnowledgeTagManager(database).ListOptionsAsync(
        TestContext.Current.CancellationToken);

    Assert.Equal(["Enabled"], options.Select(item => item.Name));
}

private static WechatRobotDbContext NewDatabase()
{
    var options = new DbContextOptionsBuilder<WechatRobotDbContext>()
        .UseInMemoryDatabase($"knowledge-tags-{Guid.NewGuid():N}")
        .Options;
    return new WechatRobotDbContext(options);
}
```

- [ ] **Step 2: Run tests and verify the manager is missing**

Run:

```powershell
dotnet test tests\server\WechatRobot.IntegrationTests\WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*KnowledgeTagManagerTests' --minimum-expected-tests 1
```

Expected: build FAIL because `KnowledgeTagManager` and the Task 1 contracts do not exist.

- [ ] **Step 3: Implement manager queries**

Create `KnowledgeTagManager` with these exact query rules:

```csharp
public async Task<KnowledgeTagPage> ListAsync(
    string? query,
    bool? isEnabled,
    bool? isGlobalPublic,
    int page,
    int pageSize,
    CancellationToken cancellationToken)
{
    page = Math.Max(page, 1);
    pageSize = Math.Clamp(pageSize, 1, 100);
    var tags = database.KnowledgeTags.AsNoTracking();
    if (!string.IsNullOrWhiteSpace(query))
    {
        var normalized = NormalizeName(query);
        tags = tags.Where(tag =>
            tag.NormalizedName.Contains(normalized) ||
            tag.Name.Contains(query.Trim()));
    }
    if (isEnabled is not null)
        tags = tags.Where(tag => tag.IsEnabled == isEnabled);
    if (isGlobalPublic is not null)
        tags = tags.Where(tag => tag.IsGlobalPublic == isGlobalPublic);

    var total = await tags.CountAsync(cancellationToken);
    var items = await tags
        .OrderBy(tag => tag.Name)
        .ThenBy(tag => tag.Id)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .Select(tag => ToRecord(tag))
        .ToArrayAsync(cancellationToken);
    return new(items, total, page, pageSize);
}

public Task<KnowledgeTagOption[]> ListOptionsAsync(CancellationToken cancellationToken) =>
    database.KnowledgeTags.AsNoTracking()
        .Where(tag => tag.IsEnabled)
        .OrderBy(tag => tag.Name)
        .ThenBy(tag => tag.Id)
        .Select(tag => new KnowledgeTagOption(tag.Id, tag.Name, tag.IsGlobalPublic))
        .ToArrayAsync(cancellationToken);

public static string NormalizeName(string name) => name.Trim().ToUpperInvariant();
```

- [ ] **Step 4: Add query boundary tests**

Cover:

- `page <= 0` becomes 1.
- `pageSize <= 0` becomes 1.
- `pageSize > 100` becomes 100.
- `state=all` maps to null.
- `global=all` maps to null.
- options never contain disabled tags.

Run the Task 1 test command again.

Expected: all `KnowledgeTagManagerTests` pass.

- [ ] **Step 5: Commit contracts and query manager**

```powershell
git add src/server/WechatRobot.Application/Knowledge/KnowledgeTagContracts.cs src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeTagManager.cs tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeTagManagerTests.cs
git commit -m "feat: add knowledge tag query contracts"
```

---

### Task 2: Implement audited create, update, and enable/disable

**Files:**

- Modify: `src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeTagManager.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeTagManagerTests.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeTagMySqlTests.cs`

**Interfaces:**

- Consumes: contracts from Task 1 and `AdministrationAuditEntity`.
- Produces:

```csharp
Task<KnowledgeTagMutationResult> CreateAsync(
    string actor,
    KnowledgeTagDraft draft,
    CancellationToken cancellationToken);

Task<KnowledgeTagMutationResult> UpdateAsync(
    Guid id,
    string actor,
    KnowledgeTagUpdate update,
    CancellationToken cancellationToken);

Task<KnowledgeTagMutationResult> SetEnabledAsync(
    Guid id,
    string actor,
    KnowledgeTagStateUpdate update,
    CancellationToken cancellationToken);
```

- [ ] **Step 1: Write failing mutation tests**

```csharp
[Fact]
public async Task Create_normalizes_name_and_writes_sanitized_audit()
{
    await using var database = NewDatabase();
    var manager = new KnowledgeTagManager(database);

    var result = await manager.CreateAsync(
        "knowledge-operator",
        new("  Product  ", false),
        TestContext.Current.CancellationToken);

    Assert.Equal(KnowledgeTagMutationStatus.Succeeded, result.Status);
    var tag = await database.KnowledgeTags.SingleAsync(
        item => item.NormalizedName == "PRODUCT",
        TestContext.Current.CancellationToken);
    var audit = await database.AdministrationAudits.SingleAsync(
        item => item.TargetId == tag.Id.ToString("D"),
        TestContext.Current.CancellationToken);
    Assert.Equal("knowledge-tag.create", audit.Action);
    Assert.Equal("knowledge-tag", audit.TargetType);
    Assert.DoesNotContain("authorization", audit.SanitizedDetailJson, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public async Task Stale_update_returns_current_version_without_overwriting()
{
    await using var database = NewDatabase();
    var tag = new KnowledgeTagEntity
    {
        Name = "Product",
        NormalizedName = "PRODUCT",
        Version = 3
    };
    database.KnowledgeTags.Add(tag);
    await database.SaveChangesAsync(TestContext.Current.CancellationToken);

    var result = await new KnowledgeTagManager(database).UpdateAsync(
        tag.Id,
        "knowledge-operator",
        new("Changed", true, ExpectedVersion: 2),
        TestContext.Current.CancellationToken);

    Assert.Equal(KnowledgeTagMutationStatus.ConcurrencyConflict, result.Status);
    Assert.Equal(3, result.Tag!.Version);
    Assert.Equal("Product", result.Tag.Name);
}
```

- [ ] **Step 2: Run tests and verify mutations fail**

Run the Task 1 command.

Expected: FAIL because create/update/state methods and routes do not exist.

- [ ] **Step 3: Implement normalized uniqueness and concurrency**

Use an explicit current-row check before mutation and still catch the MySQL unique-index race:

```csharp
var entity = await database.KnowledgeTags.SingleOrDefaultAsync(
    tag => tag.Id == id,
    cancellationToken);
if (entity is null) return new(KnowledgeTagMutationStatus.NotFound);
if (entity.Version != update.ExpectedVersion)
    return new(KnowledgeTagMutationStatus.ConcurrencyConflict, ToRecord(entity));

var normalized = NormalizeName(update.Name);
if (await database.KnowledgeTags.AnyAsync(
        tag => tag.Id != id && tag.NormalizedName == normalized,
        cancellationToken))
    return new(KnowledgeTagMutationStatus.NameConflict, ToRecord(entity));

var before = ToRecord(entity);
entity.Name = update.Name.Trim();
entity.NormalizedName = normalized;
entity.IsGlobalPublic = update.IsGlobalPublic;
entity.Version++;
database.AdministrationAudits.Add(NewAudit(
    actor,
    "knowledge-tag.update",
    entity.Id,
    new
    {
        before = new { before.Name, before.IsEnabled, before.IsGlobalPublic, before.Version },
        after = new { entity.Name, entity.IsEnabled, entity.IsGlobalPublic, entity.Version }
    }));
await database.SaveChangesAsync(cancellationToken);
return new(KnowledgeTagMutationStatus.Succeeded, ToRecord(entity));
```

For create and rename, wrap `SaveChangesAsync` and convert only a confirmed normalized-name collision:

```csharp
try
{
    await database.SaveChangesAsync(cancellationToken);
}
catch (DbUpdateException)
{
    database.ChangeTracker.Clear();
    var conflict = await database.KnowledgeTags.AsNoTracking()
        .SingleOrDefaultAsync(
            tag => tag.NormalizedName == normalized,
            cancellationToken);
    if (conflict is null) throw;
    return new(
        KnowledgeTagMutationStatus.NameConflict,
        ToRecord(conflict));
}
```

For `SetEnabledAsync`, update only `IsEnabled`, increment `Version`, and write action
`knowledge-tag.enable` or `knowledge-tag.disable`. A no-op state request still requires the
current version and returns the unchanged record without adding a duplicate audit.

Validate inside the manager before any query or audit:

```csharp
private static string? ValidateName(string name)
{
    var trimmed = name?.Trim() ?? string.Empty;
    return trimmed.Length is >= 1 and <= 128
        ? null
        : "knowledge-tag-name-invalid";
}
```

Invalid input returns `KnowledgeTagMutationStatus.InvalidInput` with that stable error and
does not write a tag or audit.

- [ ] **Step 4: Add MySQL race coverage**

`KnowledgeTagMySqlTests` must:

- create two names that normalize to the same value in concurrent contexts;
- assert exactly one succeeds;
- assert the other returns `NameConflict`;
- assert the unique index remains present;
- assert no partial administration audit is saved for the loser.

Run:

```powershell
dotnet test tests\server\WechatRobot.IntegrationTests\WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*KnowledgeTagManagerTests' '*KnowledgeTagMySqlTests' --minimum-expected-tests 1
```

Expected: PASS; MySQL tests may skip only when the repository's existing MySQL-test prerequisite is unavailable.

- [ ] **Step 5: Commit audited mutations**

```powershell
git add src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeTagManager.cs tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeTagManagerTests.cs tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeTagMySqlTests.cs
git commit -m "feat: manage knowledge tag lifecycle"
```

---

### Task 3: Enforce referenced-delete rules and expose HTTP endpoints

**Files:**

- Modify: `src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeTagManager.cs`
- Create: `src/server/WechatRobot.Api/Knowledge/KnowledgeTagEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Program.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeTagEndpointTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Groups/GroupConfigurationTests.cs`

**Interfaces:**

- Produces these routes:

```text
GET    /api/knowledge/tags
GET    /api/knowledge/tags/options
POST   /api/knowledge/tags
PUT    /api/knowledge/tags/{id}
PATCH  /api/knowledge/tags/{id}/enabled
DELETE /api/knowledge/tags/{id}?expectedVersion={version}
```

- GET/POST/PUT/PATCH require `SystemRoles.KnowledgeOperator`.
- DELETE requires `SystemRoles.Admin`.

- [ ] **Step 1: Write failing reference and authorization tests**

Add one case for each reference source:

```csharp
[Theory]
[InlineData("group")]
[InlineData("chunk")]
[InlineData("review")]
[InlineData("index-job")]
public async Task Referenced_tag_cannot_be_physically_deleted(string referenceKind)
{
    var tag = await SeedReferencedTagAsync(referenceKind);
    using var client = _factory.CreateClient();
    using var response = await client.DeleteAsync(
        $"/api/knowledge/tags/{tag.Id:D}?expectedVersion={tag.Version}",
        TestContext.Current.CancellationToken);

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(
        TestContext.Current.CancellationToken));
    Assert.Equal("knowledge-tag-referenced", body.RootElement.GetProperty("error").GetString());
    Assert.True(body.RootElement.GetProperty("references")
        .EnumerateObject().Any(property => property.Value.GetInt32() > 0));
}
```

Also assert:

- KnowledgeOperator can list/create/update/set state.
- KnowledgeOperator receives 403 for DELETE.
- an authenticated principal without a stable name/name-identifier receives 401 before mutation;
- Admin can delete an unreferenced current-version tag.
- stale DELETE returns 409 and keeps the row.
- successful delete writes `knowledge-tag.delete` with the deleted name and final version.

- [ ] **Step 2: Implement JSON reference detection**

Use relational counts plus strict JSON parsing:

```csharp
private async Task<KnowledgeTagReferenceSummary> ReferencesAsync(
    Guid id,
    CancellationToken cancellationToken)
{
    var groups = await database.GroupProfileTags.CountAsync(
        item => item.KnowledgeTagId == id,
        cancellationToken);
    var chunks = await database.KnowledgeChunkTags.CountAsync(
        item => item.KnowledgeTagId == id,
        cancellationToken);
    var reviewJson = await database.KnowledgeReviews.AsNoTracking()
        .Select(item => item.TagIdsJson)
        .ToArrayAsync(cancellationToken);
    var indexJson = await database.KnowledgeIndexJobs.AsNoTracking()
        .Select(item => item.PendingTagIdsJson)
        .ToArrayAsync(cancellationToken);
    return new(
        groups,
        chunks,
        reviewJson.Count(json => ContainsTag(json, id)),
        indexJson.Count(json => ContainsTag(json, id)));
}

private static bool ContainsTag(string json, Guid id)
{
    try
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Array &&
               document.RootElement.EnumerateArray().Any(item =>
                   item.ValueKind == JsonValueKind.String &&
                   Guid.TryParse(item.GetString(), out var value) &&
                   value == id);
    }
    catch (JsonException)
    {
        return false;
    }
}
```

Do not use substring matching; it creates false positives and is not a valid reference check.

- [ ] **Step 3: Implement endpoint mapping**

Use a single route group for KnowledgeOperator and override DELETE with Admin:

```csharp
public static IEndpointRouteBuilder MapKnowledgeTagEndpoints(this IEndpointRouteBuilder endpoints)
{
    var group = endpoints.MapGroup("/api/knowledge/tags")
        .RequireAuthorization(SystemRoles.KnowledgeOperator);
    group.MapGet("", ListAsync);
    group.MapGet("/options", OptionsAsync);
    group.MapPost("", CreateAsync);
    group.MapPut("/{id:guid}", UpdateAsync);
    group.MapPatch("/{id:guid}/enabled", SetEnabledAsync);
    group.MapDelete("/{id:guid}", DeleteAsync)
        .RequireAuthorization(SystemRoles.Admin);
    return endpoints;
}
```

Mutation mapping must be stable:

```csharp
private static IResult ToResult(KnowledgeTagMutationResult result) =>
    result.Status switch
    {
        KnowledgeTagMutationStatus.Succeeded => Results.Ok(result.Tag),
        KnowledgeTagMutationStatus.InvalidInput => Results.ValidationProblem(
            new Dictionary<string, string[]>
            {
                ["name"] = [result.Error ?? "knowledge-tag-name-invalid"]
            }),
        KnowledgeTagMutationStatus.NotFound => Results.NotFound(),
        KnowledgeTagMutationStatus.NameConflict => Results.Conflict(new
        {
            error = "knowledge-tag-name-conflict",
            current = result.Tag
        }),
        KnowledgeTagMutationStatus.ConcurrencyConflict => Results.Conflict(new
        {
            error = "knowledge-tag-concurrency-conflict",
            current = result.Tag
        }),
        KnowledgeTagMutationStatus.Referenced => Results.Conflict(new
        {
            error = "knowledge-tag-referenced",
            current = result.Tag,
            references = result.References
        }),
        _ => Results.Problem(statusCode: 500)
    };
```

Register and map in `Program.cs`:

```csharp
builder.Services.AddScoped<KnowledgeTagManager>();
// ...
app.MapKnowledgeTagEndpoints();
```

- [ ] **Step 4: Lock group disabled-tag behavior**

Extend `GroupConfigurationTests` to prove:

- an already bound disabled tag is returned with `isBound=true`;
- it can be removed;
- after removal, an update attempting to add it again returns validation failure;
- an enabled tag can still be bound;
- a global-public tag appears in `allowedTagIds` without binding.

- [ ] **Step 5: Run backend tag and group tests**

```powershell
dotnet test tests\server\WechatRobot.IntegrationTests\WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*KnowledgeTagEndpointTests' '*KnowledgeTagMySqlTests' '*GroupConfigurationTests' --minimum-expected-tests 1
```

Expected: all selected tests pass.

- [ ] **Step 6: Commit API closure**

```powershell
git add src/server/WechatRobot.Api/Knowledge/KnowledgeTagEndpoints.cs src/server/WechatRobot.Api/Program.cs src/server/WechatRobot.Infrastructure/Knowledge/KnowledgeTagManager.cs tests/server/WechatRobot.IntegrationTests/Knowledge/KnowledgeTagEndpointTests.cs tests/server/WechatRobot.IntegrationTests/Groups/GroupConfigurationTests.cs
git commit -m "feat: expose knowledge tag management api"
```

---

### Task 4: Add the typed frontend client and reusable selector

**Files:**

- Create: `src/web/wechatrobot-admin/src/api/knowledgeTags.ts`
- Create: `src/web/wechatrobot-admin/src/components/knowledge/KnowledgeTagSelector.vue`
- Create: `src/web/wechatrobot-admin/src/components/knowledge/KnowledgeTagSelector.spec.ts`

**Interfaces:**

```ts
export interface KnowledgeTag {
  id: string;
  name: string;
  isEnabled: boolean;
  isGlobalPublic: boolean;
  version: number;
  createdAtUtc: string;
}

export interface KnowledgeTagOption {
  id: string;
  name: string;
  isGlobalPublic: boolean;
}

export interface KnowledgeTagPage {
  items: KnowledgeTag[];
  total: number;
  page: number;
  pageSize: number;
}

export interface KnowledgeTagApi {
  list(params: {
    q?: string;
    state?: 'all' | 'enabled' | 'disabled';
    global?: 'all' | 'global' | 'scoped';
    page: number;
    pageSize: number;
  }): Promise<KnowledgeTagPage>;
  options(): Promise<KnowledgeTagOption[]>;
  create(request: { name: string; isGlobalPublic: boolean }): Promise<KnowledgeTag>;
  update(id: string, request: {
    name: string;
    isGlobalPublic: boolean;
    expectedVersion: number;
  }): Promise<KnowledgeTag>;
  setEnabled(id: string, request: {
    isEnabled: boolean;
    expectedVersion: number;
  }): Promise<KnowledgeTag>;
  delete(id: string, expectedVersion: number): Promise<void>;
}
```

Selector props and model:

```ts
const props = withDefaults(defineProps<{
  modelValue: string[];
  api?: Pick<KnowledgeTagApi, 'options'>;
  disabled?: boolean;
  required?: boolean;
}>(), {
  api: () => knowledgeTagApi,
  disabled: false,
  required: false
});

const emit = defineEmits<{
  'update:modelValue': [value: string[]];
}>();
```

- [ ] **Step 1: Write failing selector tests**

```ts
it('loads enabled options and emits selected ids', async () => {
  const api = {
    options: vi.fn().mockResolvedValue([
      { id: 'tag-1', name: '产品', isGlobalPublic: false },
      { id: 'tag-2', name: '公开', isGlobalPublic: true }
    ])
  };
  const wrapper = mount(KnowledgeTagSelector, {
    props: { api, modelValue: [] }
  });
  await flushPromises();

  await wrapper.get('[data-testid="knowledge-tag-tag-1"]').setValue(true);
  expect(wrapper.emitted('update:modelValue')?.at(-1)?.[0]).toEqual(['tag-1']);
  expect(wrapper.text()).toContain('公开（全局公开）');
});
```

Also test loading, empty, API failure, and required validation copy.

- [ ] **Step 2: Implement API client**

```ts
export const knowledgeTagApi: KnowledgeTagApi = {
  async list(params) {
    return (await apiClient.get<KnowledgeTagPage>('/api/knowledge/tags', {
      params: {
        query: params.q?.trim() || undefined,
        isEnabled: params.state === 'all' || params.state === undefined
          ? undefined
          : params.state === 'enabled',
        isGlobalPublic: params.global === 'all' || params.global === undefined
          ? undefined
          : params.global === 'global',
        page: params.page,
        pageSize: params.pageSize
      }
    })).data;
  },
  async options() {
    return (await apiClient.get<KnowledgeTagOption[]>('/api/knowledge/tags/options')).data;
  },
  async create(request) {
    return (await apiClient.post<KnowledgeTag>('/api/knowledge/tags', request)).data;
  },
  async update(id, request) {
    return (await apiClient.put<KnowledgeTag>(`/api/knowledge/tags/${id}`, request)).data;
  },
  async setEnabled(id, request) {
    return (await apiClient.patch<KnowledgeTag>(`/api/knowledge/tags/${id}/enabled`, request)).data;
  },
  async delete(id, expectedVersion) {
    await apiClient.delete(`/api/knowledge/tags/${id}`, { params: { expectedVersion } });
  }
};
```

- [ ] **Step 3: Implement selector states**

The component must:

- load options on mount;
- show “当前没有可用标签” when empty;
- show “标签加载失败，请刷新后重试。” on failure;
- render global-public copy;
- emit distinct IDs in option order;
- never render or request manual UUID input.

- [ ] **Step 4: Run selector tests and typecheck**

```powershell
Set-Location src\web\wechatrobot-admin
npm test -- --run src/components/knowledge/KnowledgeTagSelector.spec.ts
npm run typecheck
```

Expected: tests and typecheck pass.

- [ ] **Step 5: Commit frontend tag primitives**

```powershell
git add src/web/wechatrobot-admin/src/api/knowledgeTags.ts src/web/wechatrobot-admin/src/components/knowledge/KnowledgeTagSelector.vue src/web/wechatrobot-admin/src/components/knowledge/KnowledgeTagSelector.spec.ts
git commit -m "feat: add knowledge tag selector"
```

---

### Task 5: Replace the placeholder with the management page

**Files:**

- Modify: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeTagsView.vue`
- Create: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeTagsView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/element-plus-operational.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/task16-operational.spec.ts`

**Interfaces:**

- Consumes `KnowledgeTagApi` from Task 4.
- Uses `useAuthStore().user?.roles.includes('Admin')` to decide whether the physical-delete button is visible.

- [ ] **Step 1: Write failing page tests**

Cover these exact behaviors:

```ts
it('creates, edits, disables and refreshes the paged list', async () => {
  const api = createTagApi();
  const wrapper = mountWithAdmin(KnowledgeTagsView, { api });
  await flushPromises();

  await wrapper.get('[data-testid="create-tag"]').trigger('click');
  await wrapper.get('[data-testid="tag-name"]').setValue('产品');
  await wrapper.get('[data-testid="save-tag"]').trigger('click');
  expect(api.create).toHaveBeenCalledWith({ name: '产品', isGlobalPublic: false });

  await wrapper.get('[data-testid="toggle-tag-tag-1"]').trigger('click');
  expect(api.setEnabled).toHaveBeenCalledWith('tag-1', {
    isEnabled: false,
    expectedVersion: 1
  });
});
```

Also assert:

- filters reset to page 1;
- global-public state is visible;
- KnowledgeOperator sees no physical-delete button;
- Admin delete requires `confirmAction`;
- reference conflict shows counts and suggests disable;
- concurrency conflict replaces the stale row with the server `current` record;
- the old “后端暂未提供标签维护 API” alert is absent.

- [ ] **Step 2: Implement the page**

Use:

```ts
const props = withDefaults(defineProps<{
  api?: KnowledgeTagApi;
  confirmAction?: (message: string) => boolean | Promise<boolean>;
}>(), {
  api: () => knowledgeTagApi,
  confirmAction: message => window.confirm(message)
});

const auth = useAuthStore();
const canDelete = computed(() => auth.user?.roles.includes('Admin') === true);
const filters = reactive({
  q: '',
  state: 'all' as const,
  global: 'all' as const,
  page: 1,
  pageSize: 20
});
```

The table columns are name, scope, state, version, created time, and actions. Create/edit uses one dialog. State changes use the current row version. Delete confirmation text is:

```text
仅未被群、分段、审核或索引任务引用的标签可物理删除。确认删除“{name}”？
```

Conflict copy:

- concurrency: `标签已被其他操作员修改，页面已刷新为最新版本。`
- name: `已有同名标签，请使用其他名称。`
- referenced: `标签仍被引用，不能删除；可先停用。`

- [ ] **Step 3: Run page tests**

```powershell
npm test -- --run src/views/knowledge/KnowledgeTagsView.spec.ts src/views/element-plus-operational.spec.ts src/views/task16-operational.spec.ts
npm run typecheck
```

Expected: all selected tests and typecheck pass.

- [ ] **Step 4: Commit management UI**

```powershell
git add src/web/wechatrobot-admin/src/views/knowledge/KnowledgeTagsView.vue src/web/wechatrobot-admin/src/views/knowledge/KnowledgeTagsView.spec.ts src/web/wechatrobot-admin/src/views/element-plus-operational.spec.ts src/web/wechatrobot-admin/src/views/task16-operational.spec.ts
git commit -m "feat: manage knowledge tags in admin"
```

---

### Task 6: Replace document and review UUID fields with selectors

**Files:**

- Modify: `src/web/wechatrobot-admin/src/views/knowledge/DocumentDetailView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeReviewView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/task16-operational.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts`
- Delete: `src/web/wechatrobot-admin/src/utils/knowledgeTagIds.ts`

**Interfaces:**

- Document indexing continues to call:

```ts
queueIndex(documentId: string, versionId: string, tagIds: string[], reindex?: boolean)
```

- Knowledge review continues to call:

```ts
reviewCandidate(id: string, request: {
  decision: string;
  tagIds: string[];
  revisedAnswer?: string;
  idempotencyKey: string;
  expectedVersion: number;
})
```

- [ ] **Step 1: Rewrite failing consumer tests**

Document test:

```ts
expect(wrapper.find('input#index-tag-ids').exists()).toBe(false);
await wrapper.get('[data-testid="knowledge-tag-tag-1"]').setValue(true);
await wrapper.get('[data-testid="queue-index"]').trigger('click');
expect(api.queueIndex).toHaveBeenCalledWith(documentId, versionId, ['tag-1'], false);
```

Review test:

```ts
expect(wrapper.find('input#candidate-tags').exists()).toBe(false);
await wrapper.get('[data-testid="knowledge-tag-tag-2"]').setValue(true);
await wrapper.get('[data-testid="approve-candidate"]').trigger('click');
expect(api.reviewCandidate).toHaveBeenCalledWith(
  candidateId,
  expect.objectContaining({ decision: 'approve', tagIds: ['tag-2'] })
);
```

Group regression:

```ts
expect(wrapper.get('[data-testid="tag-disabled-bound"]').attributes('disabled')).toBeUndefined();
expect(wrapper.get('[data-testid="tag-disabled-unbound"]').attributes('disabled')).toBeDefined();
```

- [ ] **Step 2: Replace DocumentDetailView parsing**

Remove `parseKnowledgeTagIds`, `tagText`, and UUID error copy. Add:

```ts
import KnowledgeTagSelector from '../../components/knowledge/KnowledgeTagSelector.vue';

const selectedTagIds = ref<string[]>([]);

if (selectedTagIds.value.length === 0) {
  error.value = '建立索引时至少选择一个已启用的知识标签。';
  return;
}
await props.api.queueIndex(
  props.documentId,
  props.versionId,
  selectedTagIds.value,
  isActive.value);
```

Template:

```vue
<KnowledgeTagSelector
  v-model="selectedTagIds"
  required
  aria-label="索引知识标签"
/>
```

- [ ] **Step 3: Replace KnowledgeReviewView parsing**

Use `selectedTagIds` and require at least one selection only for approve. Reject sends `tagIds: []`. Preserve the existing revised-answer, idempotency, and expected-version behavior.

- [ ] **Step 4: Delete the parser and scan for UUID entry**

Delete `src/web/wechatrobot-admin/src/utils/knowledgeTagIds.ts` with `apply_patch`, then run:

```powershell
rg -n "知识标签 ID|标签 ID|parseKnowledgeTagIds|index-tag-ids|candidate-tags" src/web/wechatrobot-admin/src
```

Expected: no production-view matches. Test descriptions may mention the old fields only in negative assertions.

- [ ] **Step 5: Run consumer regressions**

```powershell
npm test -- --run src/views/task16-operational.spec.ts src/views/groups/GroupRulesView.spec.ts src/components/knowledge/KnowledgeTagSelector.spec.ts
npm run typecheck
```

Expected: all selected tests and typecheck pass.

- [ ] **Step 6: Commit selector adoption**

```powershell
git add -u src/web/wechatrobot-admin/src/utils/knowledgeTagIds.ts
git add src/web/wechatrobot-admin/src/views/knowledge/DocumentDetailView.vue src/web/wechatrobot-admin/src/views/knowledge/KnowledgeReviewView.vue src/web/wechatrobot-admin/src/views/task16-operational.spec.ts src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts
git commit -m "fix: replace knowledge tag uuid inputs"
```

---

### Task 7: Run the phase gate and update the roadmap

**Files:**

- Modify: `docs/superpowers/plans/2026-07-24-frontend-backend-alignment-roadmap.md`
- Review: every file changed in Tasks 1–6.

**Interfaces:**

- Produces the completion record for P1 Knowledge Tag Closure.
- Unlocks creation of the Knowledge Document Management detailed plan.

- [ ] **Step 1: Inspect constraints**

```powershell
git status --short --branch
Get-Process WechatRobot.Api,WechatRobot.Worker -ErrorAction SilentlyContinue |
  Select-Object Id,ProcessName,StartTime,Path
```

Do not stop either process. Use isolated outputs when needed.

- [ ] **Step 2: Run backend tag and group tests**

```powershell
dotnet test tests\server\WechatRobot.IntegrationTests\WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*KnowledgeTagManagerTests' '*KnowledgeTagEndpointTests' '*KnowledgeTagMySqlTests' '*GroupConfigurationTests' --minimum-expected-tests 1
```

Expected: all selected discovered tests pass.

- [ ] **Step 3: Run server regression and build**

```powershell
dotnet test tests\server\WechatRobot.UnitTests\WechatRobot.UnitTests.csproj --no-restore
dotnet test tests\server\WechatRobot.ContractTests\WechatRobot.ContractTests.csproj --no-restore
dotnet build WechatRobot.slnx --no-restore
```

Expected: all tests pass; build has zero errors. Record warnings exactly.

- [ ] **Step 4: Run frontend regression**

```powershell
Set-Location src\web\wechatrobot-admin
npm run typecheck
npm test -- --run
npm run build
```

Expected: all commands pass.

- [ ] **Step 5: Inspect safety and capability truth**

```powershell
Set-Location H:\Codex\WechatRobot\.worktrees\codex-wechatrobot-mvp
git diff --check
rg -n "知识标签 ID|标签 ID|parseKnowledgeTagIds|后端暂未提供标签维护 API" src/web/wechatrobot-admin/src
rg -n -i "authorization|callback.*secret|worktoolrobotid|api[-_]?key" src/server/WechatRobot.Api/Knowledge src/server/WechatRobot.Infrastructure/Knowledge tests/server/WechatRobot.IntegrationTests/Knowledge
```

Expected:

- no manual tag-ID or placeholder copy remains in production views;
- secret-name matches are either absent or explicit negative test assertions;
- no audit payload includes content or credentials.

- [ ] **Step 6: Update and commit roadmap state**

Change:

```markdown
| P1 Knowledge tag closure | Completed | `docs/superpowers/plans/2026-07-24-knowledge-tag-closure.md` | P0 |
```

Add actual test counts, build results, commit IDs, and any explicit skips. Then:

```powershell
git add docs/superpowers/plans/2026-07-24-frontend-backend-alignment-roadmap.md
git commit -m "docs: complete knowledge tag closure"
```

- [ ] **Step 7: Create the next plan**

Create:

```text
docs/superpowers/plans/2026-07-24-knowledge-document-management.md
```

Base it on the now-current tag options API and selector. Do not add handoff work.

---

## Plan Self-Review

- Spec coverage: pagination, create/edit, enable/disable, global-public flag, normalized uniqueness, optimistic concurrency, four reference sources, administration audit, management UI, document selector, group selector regression, and review selector are each assigned to a task.
- Completeness scan: every implementation step names concrete inputs, outputs, errors, tests, commands, and expected results.
- Type consistency: `KnowledgeTagRecord`, `KnowledgeTagOption`, `ExpectedVersion`, route paths, frontend API names, and selector model values are consistent across tasks.
- Scope: no document-list management, system settings, dashboard, full robot UI, user-role management, or human-handoff mapping is included.
