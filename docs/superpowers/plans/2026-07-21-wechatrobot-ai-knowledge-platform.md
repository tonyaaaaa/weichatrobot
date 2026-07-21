# WechatRobot AI Knowledge Platform Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Build and locally verify a single-tenant AI employee assistant that receives unmentioned text messages from WorkTool external groups, answers from a tag-scoped self-built knowledge base, and hands unresolved questions to human staff.

**Architecture:** Use an ASP.NET Core 10 API for synchronous HTTP boundaries and an ASP.NET Core Worker for durable asynchronous work. Store business state and durable jobs in MySQL, vectors and retrieval filter payloads in Qdrant, source files in one public-read Alibaba OSS bucket, and OCR behind a private FastAPI/PaddleOCR service. A Vue 3 administration SPA manages all configuration and operational workflows. External providers are hidden behind application interfaces so contract tests can run without sending real group messages.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, ASP.NET Core Identity, EF Core 10 with MySql.EntityFrameworkCore 10.0.7, xUnit v3, Testcontainers, Vue 3.5, TypeScript 7, Vite 8, Element Plus 2.14, Pinia 4, Vitest 4, Playwright 1.61, MySQL 8.4, Qdrant 1.18, Qdrant.Client 1.18.1, Aliyun OSS SDK 2.14.1, Python 3.11, FastAPI 0.139, PaddleOCR 3.7, Docker Compose.

## Global Constraints

- Treat the approved design at docs/superpowers/specs/2026-07-21-wechatrobot-ai-knowledge-platform-design.md as the product contract.
- Use test-driven development: add one failing test, prove the expected failure, add the smallest implementation, and prove the test passes before broadening the change.
- Keep HTTP handlers thin. Domain decisions belong in Domain/Application services, provider details in Infrastructure, and polling/background coordination in Worker.
- The WorkTool callback must validate and durably enqueue within three seconds. It must never call OCR, Qdrant, an LLM, OSS, or WorkTool send APIs inline.
- Do not log or commit model keys, OSS credentials, WorkTool robot IDs, callback secrets, authorization headers, document URLs containing sensitive query data, or plaintext encrypted settings.
- Store timestamps as UTC. Convert to Asia/Shanghai only in the Vue presentation layer.
- Use MySQL-backed durable jobs and idempotency keys. Do not use an in-memory queue as the source of truth.
- Enforce tag scope in the Qdrant query filter. Application-side post-filtering is an additional guard, not the primary access boundary.
- Keep the accepted public-read OSS warning visible in document administration. Tags restrict robot retrieval, not possession of a public object URL.
- Keep the schema single-tenant. Do not add tenant selection, tenant claims, tenant billing, or tenant-scoped routing in this MVP.
- Real WorkTool operations are opt-in tests protected by RUN_WORKTOOL_E2E=1. Normal test runs must not create groups or send messages.
- Do not copy MaxKB GPL source. Implement the approved behavior behind IKnowledgeService and keep a future provider boundary.
- After each task, run the focused tests, run git diff --check, inspect git status, and commit only the files named by that task.

## Target Repository Layout

```text
WechatRobot.slnx
global.json
Directory.Build.props
Directory.Packages.props
docker-compose.yml
.env.example
scripts/
  start-dev.ps1
  stop-dev.ps1
  update-worktool-callback.ps1
src/server/
  WechatRobot.Domain/
  WechatRobot.Application/
  WechatRobot.Infrastructure/
  WechatRobot.Api/
  WechatRobot.Worker/
src/web/wechatrobot-admin/
src/ocr-service/
tests/server/
  WechatRobot.UnitTests/
  WechatRobot.IntegrationTests/
  WechatRobot.ContractTests/
tests/e2e/
docs/runbooks/
```

## Core Contract Sketches

These signatures fix the direction of dependencies. Implementations may add cancellation, telemetry, and typed result details, but must not move provider SDK types into Application or Domain.

```csharp
public interface IWorkToolClient
{
    Task<WorkToolCommandResult> SendTextAsync(
        RobotConnection robot,
        GroupAddress group,
        string text,
        IReadOnlyCollection<string> mentionedEmployeeIds,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<WorkToolCommandResult> CreateExternalGroupAsync(
        RobotConnection robot,
        CreateExternalGroupCommand command,
        CancellationToken cancellationToken);

    Task<WorkToolCommandResult> ModifyExternalGroupAsync(
        RobotConnection robot,
        ModifyExternalGroupCommand command,
        CancellationToken cancellationToken);
}

public interface IChatCompletionClient
{
    Task<ChatCompletionResult> CompleteAsync(
        ChatModelSettings settings,
        IReadOnlyList<ChatMessage> messages,
        CancellationToken cancellationToken);
}

public interface IEmbeddingClient
{
    Task<EmbeddingBatchResult> EmbedAsync(
        EmbeddingModelSettings settings,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken);
}

public interface IKnowledgeService
{
    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(
        KnowledgeQuery query,
        IReadOnlySet<Guid> allowedTagIds,
        CancellationToken cancellationToken);
}

public interface IObjectStorage
{
    Task<StoredObject> PutAsync(
        string objectKey,
        Stream content,
        string contentType,
        CancellationToken cancellationToken);

    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
}
```

The OCR service request and response contract is:

```text
POST /v1/ocr/pages
Content-Type: multipart/form-data
fields: document_id, page_number, image
```

```json
{
  "pageNumber": 1,
  "blocks": [
    { "text": "recognized text", "confidence": 0.98, "order": 0 }
  ],
  "error": null
}
```

The Vue API layer uses discriminated operational states rather than free-form status strings:

```typescript
export type JobStatus =
  | 'pending'
  | 'leased'
  | 'retrying'
  | 'completed'
  | 'failed'
  | 'deadLetter';

export type DocumentStatus =
  | 'uploaded'
  | 'parsing'
  | 'ocr'
  | 'awaitingChunking'
  | 'awaitingIndex'
  | 'available'
  | 'failed'
  | 'disabled';
```

## Wave 1: Foundation and Durable Message Skeleton

### Task 1: Scaffold the solution and deterministic local dependencies

**Files:**

- Create: .gitignore
- Create: global.json
- Create: Directory.Build.props
- Create: Directory.Packages.props
- Create: WechatRobot.slnx
- Create: docker-compose.yml
- Create: .env.example
- Create: src/server/WechatRobot.Domain/WechatRobot.Domain.csproj
- Create: src/server/WechatRobot.Application/WechatRobot.Application.csproj
- Create: src/server/WechatRobot.Infrastructure/WechatRobot.Infrastructure.csproj
- Create: src/server/WechatRobot.Api/WechatRobot.Api.csproj
- Create: src/server/WechatRobot.Worker/WechatRobot.Worker.csproj
- Create: tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj
- Create: tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj
- Create: tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj
- Create: tests/server/WechatRobot.UnitTests/Architecture/ProjectReferenceTests.cs

**Interfaces:**

- Produces a compilable solution with Domain <- Application <- Infrastructure and API/Worker composition roots.
- Consumes only local SDKs and the MySQL/Qdrant services declared in Docker Compose.

- [ ] Create the failing architecture test first. It should assert that Domain references no other project and Application references Domain but not Infrastructure:

```csharp
public sealed class ProjectReferenceTests
{
    [Fact]
    public void Domain_and_application_dependencies_point_inward()
    {
        var domain = ProjectFile.Load("src/server/WechatRobot.Domain/WechatRobot.Domain.csproj");
        var application = ProjectFile.Load("src/server/WechatRobot.Application/WechatRobot.Application.csproj");

        Assert.Empty(domain.ProjectReferences);
        Assert.Contains("WechatRobot.Domain", application.ProjectReferences);
        Assert.DoesNotContain("WechatRobot.Infrastructure", application.ProjectReferences);
    }
}
```

- [ ] Run dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj and confirm it fails because the solution/projects do not exist yet.
- [ ] Scaffold the five projects and three test projects with dotnet new, add project references in the approved direction, and add all projects to WechatRobot.slnx.
- [ ] Pin SDK 10.0.301 in global.json with rollForward set to latestFeature.
- [ ] Centralize package versions in Directory.Packages.props, including Microsoft.EntityFrameworkCore 10.0.10, Microsoft.AspNetCore.Identity.EntityFrameworkCore 10.0.10, MySql.EntityFrameworkCore 10.0.7, Qdrant.Client 1.18.1, Aliyun.OSS.SDK.NetCore 2.14.1, DocumentFormat.OpenXml 3.5.1, UglyToad.PdfPig 1.7.0-custom-5, xunit.v3 3.2.2, Microsoft.AspNetCore.Mvc.Testing 10.0.10, and Testcontainers.MySql 4.13.0.
- [ ] Configure docker-compose.yml with mysql:8.4.10 and qdrant/qdrant:v1.18.2, named volumes, health checks, localhost-only ports, and values sourced from .env.
- [ ] Put safe example values only in .env.example. Ignore .env, .superpowers/, bin, obj, node_modules, dist, coverage, Playwright artifacts, Python caches, .venv, local logs, and user secrets.
- [ ] Run docker compose config, dotnet restore WechatRobot.slnx, dotnet build WechatRobot.slnx -warnaserror, and the architecture test; expect all to pass.
- [ ] Commit:

```powershell
git add .gitignore global.json Directory.Build.props Directory.Packages.props WechatRobot.slnx docker-compose.yml .env.example src/server tests/server
git commit -m "build: scaffold WechatRobot solution"
```

### Task 2: Add domain primitives, rule matching, and tag visibility

**Files:**

- Create: src/server/WechatRobot.Domain/Common/Entity.cs
- Create: src/server/WechatRobot.Domain/Common/UtcDateTime.cs
- Create: src/server/WechatRobot.Domain/Groups/GroupRule.cs
- Create: src/server/WechatRobot.Domain/Groups/GroupRuleMatcher.cs
- Create: src/server/WechatRobot.Domain/Knowledge/KnowledgeTag.cs
- Create: src/server/WechatRobot.Domain/Knowledge/KnowledgeVisibility.cs
- Create: tests/server/WechatRobot.UnitTests/Groups/GroupRuleMatcherTests.cs
- Create: tests/server/WechatRobot.UnitTests/Knowledge/KnowledgeVisibilityTests.cs

**Interfaces:**

- Produces GroupRuleMatchResult and a set of allowed tag IDs for later callback and retrieval tasks.
- Consumes group names, include/exclude patterns, bound tags, and the global-public tag ID.

- [ ] Write parameterized failing tests for exact, contains, and regex includes; exclude precedence; no include match; invalid regex; and a 100 ms regex timeout.
- [ ] Write a failing visibility test proving that group tags use OR semantics and always include the enabled global-public tag.
- [ ] Run the two focused test classes and confirm failures reference missing domain types.
- [ ] Implement immutable rule records and GroupRuleMatcher.Match. Compile regex patterns with CultureInvariant, IgnoreCase where configured, NonBacktracking where supported, and a bounded timeout.
- [ ] Implement KnowledgeVisibility.BuildAllowedTagIds without loading document content.
- [ ] Run dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj; expect all tests to pass.
- [ ] Commit:

```powershell
git add src/server/WechatRobot.Domain tests/server/WechatRobot.UnitTests
git commit -m "feat: add group rules and tag visibility"
```

### Task 3: Add MySQL persistence, Identity, roles, and migrations

**Files:**

- Create: src/server/WechatRobot.Infrastructure/Persistence/WechatRobotDbContext.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Configurations/
- Create: src/server/WechatRobot.Infrastructure/Identity/ApplicationUser.cs
- Create: src/server/WechatRobot.Infrastructure/Identity/SystemRoles.cs
- Create: src/server/WechatRobot.Infrastructure/Identity/IdentitySeeder.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/RobotConfigEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/GroupProfileEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/GroupRuleEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/KnowledgeTagEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/ConversationMessageEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/DurableJobEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/SendCommandEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/DeadLetterEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/ModelConfigEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Migrations/
- Create: src/server/WechatRobot.Api/Auth/AuthEndpoints.cs
- Create: src/server/WechatRobot.Api/Auth/JwtOptions.cs
- Create: src/server/WechatRobot.Api/Program.cs
- Create: tests/server/WechatRobot.IntegrationTests/Infrastructure/MySqlFixture.cs
- Create: tests/server/WechatRobot.IntegrationTests/Auth/RoleAuthorizationTests.cs
- Create: tests/server/WechatRobot.IntegrationTests/Persistence/MigrationTests.cs

**Interfaces:**

- Produces authenticated JWT principals with Admin, KnowledgeOperator, or HumanAgent roles.
- Produces a migrated MySQL schema used by all subsequent durable workflows.
- Consumes the database connection string, JWT signing key, and bootstrap-admin credentials from environment variables.

- [ ] Write a failing Testcontainers integration test that applies migrations to an empty MySQL database and verifies the three roles exist after seeding.
- [ ] Write failing API authorization tests: anonymous is 401, wrong role is 403, and the allowed role succeeds for a protected probe endpoint.
- [ ] Run the focused integration tests and confirm they fail before DbContext and auth wiring exist.
- [ ] Add ASP.NET Core Identity with ApplicationUser and IdentityRole<Guid>. Configure strong password policy, lockout, JWT validation, role policies, and CORS from explicit configuration.
- [ ] Model the tables required by Waves 1 and 2 in an InitialIdentityMessaging migration: Identity, robot, group/rules/tags, inbound conversation message, durable job, send command, dead letter, and model configuration. Later tasks add knowledge, retrieval-audit, and handoff migrations with their own tests.
- [ ] Add unique indexes for WorkTool message ID, fallback hash window, send idempotency key, and normalized tag name.
- [ ] Seed roles idempotently. Create the first Admin only when all bootstrap environment variables are present; never ship a default password.
- [ ] Add GET /api/auth/me and POST /api/auth/login. Return identity/role data but never password hashes or stored secrets.
- [ ] Run migrations against Testcontainers and run all server tests; expect pass.
- [ ] Commit:

```powershell
git add src/server/WechatRobot.Infrastructure src/server/WechatRobot.Api tests/server/WechatRobot.IntegrationTests
git commit -m "feat: add persistence identity and role authorization"
```

### Task 4: Add encrypted settings and provider configuration boundaries

**Files:**

- Create: src/server/WechatRobot.Application/Security/ISecretProtector.cs
- Create: src/server/WechatRobot.Application/Models/IChatCompletionClient.cs
- Create: src/server/WechatRobot.Application/Models/IEmbeddingClient.cs
- Create: src/server/WechatRobot.Application/Models/ModelConfigurationService.cs
- Create: src/server/WechatRobot.Infrastructure/Security/AesGcmSecretProtector.cs
- Create: src/server/WechatRobot.Infrastructure/Models/OpenAiCompatibleChatClient.cs
- Create: src/server/WechatRobot.Infrastructure/Models/OpenAiCompatibleEmbeddingClient.cs
- Create: src/server/WechatRobot.Api/Models/ModelConfigurationEndpoints.cs
- Create: tests/server/WechatRobot.UnitTests/Security/AesGcmSecretProtectorTests.cs
- Create: tests/server/WechatRobot.ContractTests/Models/OpenAiCompatibleClientTests.cs

**Interfaces:**

- Produces independently configurable chat and embedding clients.
- Consumes BaseUrl, model name, encrypted API key, timeout, and retry limits.

- [ ] Write failing tests that assert AES-256-GCM round-trip, unique nonce per encryption, authentication failure after ciphertext tampering, and startup failure for a missing or wrong-length master key.
- [ ] Write contract tests against an in-process fake HTTP server and prove chat and embedding requests use separate base URLs, models, keys, and response DTOs.
- [ ] Implement versioned ciphertext containing algorithm version, nonce, authentication tag, and ciphertext. Read the 32-byte master key only from WECHATROBOT_MASTER_KEY_BASE64.
- [ ] Implement Admin-only model configuration endpoints. Responses expose hasApiKey and last-four metadata only; updates preserve an existing key when the submitted key field is empty.
- [ ] Add test-connection endpoints that call the selected provider without persisting plaintext or logging the Authorization header.
- [ ] Run focused unit/contract tests and the full server suite; expect pass.
- [ ] Commit:

```powershell
git add src/server/WechatRobot.Application src/server/WechatRobot.Infrastructure src/server/WechatRobot.Api tests/server
git commit -m "feat: add encrypted model provider settings"
```

### Task 5: Receive WorkTool callbacks atomically and idempotently

**Files:**

- Create: src/server/WechatRobot.Application/WorkTool/WorkToolCallbackDto.cs
- Create: src/server/WechatRobot.Application/Messaging/InboundMessageService.cs
- Create: src/server/WechatRobot.Application/Jobs/IDurableJobRepository.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs
- Create: src/server/WechatRobot.Api/WorkTool/WorkToolCallbackEndpoints.cs
- Create: src/server/WechatRobot.Api/WorkTool/WorkToolCallbackRateLimitPolicy.cs
- Create: tests/server/WechatRobot.UnitTests/Messaging/MessageDeduplicationTests.cs
- Create: tests/server/WechatRobot.IntegrationTests/WorkTool/CallbackIngestionTests.cs

**Interfaces:**

- Consumes POST /api/worktool/callback/{robotCode}?token={callbackSecret}.
- Produces one inbound conversation message and one ProcessInboundMessage durable job in the same MySQL transaction.

- [ ] Add failing unit tests for messageId idempotency and fallback hash generation from robot, group, sender, normalized text, and a configured time bucket.
- [ ] Add failing integration tests for invalid token, non-group input, non-text input, duplicate callback, transaction rollback, and a valid callback returning the exact accepted payload.
- [ ] Include a stopwatch assertion that the fake callback returns under 500 ms locally, leaving margin for the WorkTool three-second requirement.
- [ ] Implement strict DTO validation and constant-time secret comparison. Store only a one-way hash of the callback secret.
- [ ] Use a single EF Core transaction to insert message and job. On duplicate-key conflict, return accepted without creating another job.
- [ ] Return:

```json
{
  "code": 0,
  "message": "accepted"
}
```

- [ ] Add endpoint-specific rate limiting and redacted structured logs.
- [ ] Run the callback integration test repeatedly and confirm there is exactly one durable job.
- [ ] Commit:

```powershell
git add src/server/WechatRobot.Application src/server/WechatRobot.Infrastructure src/server/WechatRobot.Api tests/server
git commit -m "feat: ingest WorkTool callbacks durably"
```

### Task 6: Process jobs and send fixed replies with rate limiting and retries

**Files:**

- Create: src/server/WechatRobot.Application/WorkTool/IWorkToolClient.cs
- Create: src/server/WechatRobot.Application/Messaging/InboundMessageProcessor.cs
- Create: src/server/WechatRobot.Application/Messaging/SendCommandService.cs
- Create: src/server/WechatRobot.Infrastructure/WorkTool/WorkToolClient.cs
- Create: src/server/WechatRobot.Worker/Program.cs
- Create: src/server/WechatRobot.Worker/Jobs/DurableJobWorker.cs
- Create: src/server/WechatRobot.Worker/Jobs/RobotSendWorker.cs
- Create: tests/server/WechatRobot.UnitTests/Messaging/RetryScheduleTests.cs
- Create: tests/server/WechatRobot.ContractTests/WorkTool/SendRawMessageContractTests.cs
- Create: tests/server/WechatRobot.IntegrationTests/Messaging/FixedReplyPipelineTests.cs

**Interfaces:**

- Consumes pending durable jobs and WorkTool sendRawMessage.
- Produces per-robot ordered send commands with a configurable default cap of 50 QPM and a hard validation maximum of 60 QPM.

- [ ] Write failing tests for delays of 5, 15, and 45 seconds; fourth failure becomes dead-letter; completed idempotency key is never resent.
- [ ] Write a failing contract test for the exact WorkTool sendRawMessage request mapping and successful/failed response mapping.
- [ ] Write a failing integration test from an already-ingested message to a fake WorkTool endpoint receiving one fixed text reply.
- [ ] Implement database job leasing with lease owner, lease expiry, attempt count, next-attempt UTC, completion UTC, and optimistic concurrency. Recover expired leases on restart.
- [ ] Implement a token-bucket limiter per robot. Reject saved settings above 60 QPM.
- [ ] Initially make InboundMessageProcessor enqueue a configurable fixed reply. This proves the external boundary before RAG is introduced.
- [ ] Run Worker integration tests with two Worker instances and prove only one send occurs.
- [ ] Commit:

```powershell
git add src/server/WechatRobot.Application src/server/WechatRobot.Infrastructure src/server/WechatRobot.Worker tests/server
git commit -m "feat: add durable WorkTool reply pipeline"
```

## Checkpoint 1

- [ ] Start MySQL and Qdrant with Docker Compose.
- [ ] Start API and Worker with fake WorkTool configuration.
- [ ] POST the recorded no-at payload with roomType=1 and atMe=false twice.
- [ ] Confirm two HTTP accepted responses, one inbound row, one completed process job, and one fake send call.
- [ ] Record the commands and evidence in docs/runbooks/local-callback-smoke-test.md.

## Wave 2: Vue Administration and Group Operations

### Task 7: Scaffold the Vue admin shell, authentication, and role guards

**Files:**

- Create: src/web/wechatrobot-admin/package.json
- Create: src/web/wechatrobot-admin/vite.config.ts
- Create: src/web/wechatrobot-admin/tsconfig.json
- Create: src/web/wechatrobot-admin/src/main.ts
- Create: src/web/wechatrobot-admin/src/router/index.ts
- Create: src/web/wechatrobot-admin/src/stores/auth.ts
- Create: src/web/wechatrobot-admin/src/api/http.ts
- Create: src/web/wechatrobot-admin/src/layouts/AdminLayout.vue
- Create: src/web/wechatrobot-admin/src/views/LoginView.vue
- Create: src/web/wechatrobot-admin/src/views/DashboardView.vue
- Create: src/web/wechatrobot-admin/src/components/PublicOssWarning.vue
- Create: src/web/wechatrobot-admin/src/**/*.spec.ts

**Interfaces:**

- Consumes POST /api/auth/login and GET /api/auth/me.
- Produces authenticated routes and role-aware navigation; the API remains the authorization boundary.

- [ ] Pin Vue 3.5.40, Vite 8.1.5, plugin-vue 6.0.8, TypeScript 7.0.2, Element Plus 2.14.3, vue-router 5.2.0, Pinia 4.0.2, axios 1.18.1, and Vitest 4.1.10.
- [ ] Write failing component tests for login error handling, refresh-time user hydration, 401 logout, role-hidden menu items, and the public OSS warning.
- [ ] Implement the app shell, Axios bearer interceptor, auth store, route metadata, and Admin/KnowledgeOperator/HumanAgent navigation.
- [ ] Add placeholder route components only for routes completed in later tasks; each placeholder must state its owning task and must be removed by Task 16.
- [ ] Run npm ci, npm run test, npm run typecheck, and npm run build from src/web/wechatrobot-admin.
- [ ] Commit:

```powershell
git add src/web/wechatrobot-admin
git commit -m "feat: add Vue admin authentication shell"
```

### Task 8: Build group rule, tag binding, and context configuration

**Files:**

- Create: src/server/WechatRobot.Application/Groups/GroupConfigurationService.cs
- Create: src/server/WechatRobot.Api/Groups/GroupEndpoints.cs
- Create: src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue
- Create: src/web/wechatrobot-admin/src/components/groups/RuleEditor.vue
- Create: src/web/wechatrobot-admin/src/components/groups/RulePreview.vue
- Create: src/web/wechatrobot-admin/src/components/groups/ContextPolicyForm.vue
- Create: src/web/wechatrobot-admin/src/api/groups.ts
- Create: tests/server/WechatRobot.IntegrationTests/Groups/GroupConfigurationTests.cs
- Create: src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts

**Interfaces:**

- Consumes include/exclude exact, contains, or regex rules; group tags; and context overrides.
- Produces validated effective configuration and a preview of matching known group names.

- [ ] Write failing API tests for exclude precedence, regex validation/timeout, OR tag binding, global-public tag behavior, and defaults of six turns/30 minutes/group-shared.
- [ ] Write failing Vue tests for adding each match type, previewing hits before save, displaying excluded groups, and editing per-group context.
- [ ] Implement GET/PUT /api/groups/{id}/configuration and POST /api/group-rules/preview with Admin authorization.
- [ ] Keep system defaults distinct from nullable per-group overrides; return both configured and effective values.
- [ ] Implement the page with exact/contains/regex options, include/exclude lists, tag multi-select, sender-isolated/group-shared choice, history turns, idle timeout, token cap, summary toggle, bot-history toggle, and clear-context action.
- [ ] Run focused server and Vue tests, then both full suites.
- [ ] Commit:

```powershell
git add src/server src/web/wechatrobot-admin tests/server
git commit -m "feat: add group rules tags and context settings"
```

### Task 9: Add safe WorkTool robot and group lifecycle operations

**Files:**

- Create: src/server/WechatRobot.Application/Robots/RobotConfigurationService.cs
- Create: src/server/WechatRobot.Application/Groups/GroupLifecycleService.cs
- Create: src/server/WechatRobot.Api/Robots/RobotEndpoints.cs
- Create: src/server/WechatRobot.Api/Groups/GroupLifecycleEndpoints.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/ExternalActionAuditEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Migrations/AddExternalActionAudit.cs
- Create: src/web/wechatrobot-admin/src/views/robots/RobotSettingsView.vue
- Create: src/web/wechatrobot-admin/src/views/groups/GroupManagementView.vue
- Create: src/web/wechatrobot-admin/src/components/groups/ExternalActionConfirm.vue
- Create: tests/server/WechatRobot.ContractTests/WorkTool/CreateGroupContractTests.cs
- Create: tests/server/WechatRobot.ContractTests/WorkTool/ModifyGroupContractTests.cs
- Create: tests/server/WechatRobot.IntegrationTests/Groups/GroupLifecycleAuditTests.cs

**Interfaces:**

- Consumes WorkTool command type 206 for external-group creation and type 207 for member/group changes.
- Produces audited external actions with operator, sanitized request, command number, status, and result.

- [ ] Write failing contract tests for create group, add/remove member, rename group, update announcement, and update remark.
- [ ] Write failing authorization/audit tests proving only Admin can execute and every attempted external action has an audit record.
- [ ] Store robot credentials encrypted. Expose masked values and a non-mutating connection test.
- [ ] Implement dry-run request preview followed by a second POST containing a short-lived confirmation token. Reject a changed payload or expired token.
- [ ] Implement Vue forms for known groups, manual registration of an existing group after a human invitation, new-group creation, member changes, and command status.
- [ ] Clearly state in the existing-group flow that the first robot invitation is a manual Enterprise WeChat action.
- [ ] Run contract/integration/component tests with fake WorkTool only.
- [ ] Commit:

```powershell
git add src/server src/web/wechatrobot-admin tests/server
git commit -m "feat: add audited WorkTool group operations"
```

## Wave 3: Self-built Knowledge Base

### Task 10: Upload source documents to Alibaba OSS and version them

**Files:**

- Create: src/server/WechatRobot.Application/Storage/IObjectStorage.cs
- Create: src/server/WechatRobot.Application/Knowledge/DocumentUploadService.cs
- Create: src/server/WechatRobot.Infrastructure/Storage/OssOptions.cs
- Create: src/server/WechatRobot.Infrastructure/Storage/AliyunOssStorage.cs
- Create: src/server/WechatRobot.Api/Knowledge/DocumentEndpoints.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/KnowledgeDocumentEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/KnowledgeDocumentVersionEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/KnowledgeChunkEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/KnowledgeChunkTagEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Migrations/AddKnowledgeDocuments.cs
- Create: tests/server/WechatRobot.UnitTests/Knowledge/UploadValidationTests.cs
- Create: tests/server/WechatRobot.ContractTests/Storage/AliyunOssStorageContractTests.cs
- Create: tests/server/WechatRobot.IntegrationTests/Knowledge/DocumentUploadTests.cs

**Interfaces:**

- Consumes multipart .md, .txt, .pdf, and .docx documents.
- Produces OSS keys under wechatrobot/knowledge/{documentId}/{version}/source/{safeFileName} and a queued ParseDocument job.

- [ ] Write failing validation tests for unsupported .doc, spoofed extension/MIME/header, oversize input, duplicate hash, malformed archive, and configured expansion limits.
- [ ] Write an OSS contract test against a fake handler or an explicitly selected test bucket profile; normal CI must not require live credentials.
- [ ] Implement streaming SHA-256 validation and bounded buffering. Never trust the client filename for an object key.
- [ ] In one transaction, create document/version metadata and enqueue upload/parse work. If OSS upload fails, persist a retryable failed state without publishing the version.
- [ ] Return document state, public URL, object key metadata, and the accepted public-read risk indicator.
- [ ] Add an Admin/KnowledgeOperator endpoint to retry a failed upload and an Admin-only physical-delete request implemented as a durable cleanup job.
- [ ] Run focused tests and full server tests.
- [ ] Commit:

```powershell
git add src/server tests/server
git commit -m "feat: upload and version knowledge documents"
```

### Task 11: Parse Markdown, TXT, DOCX, and text PDFs

**Files:**

- Create: src/server/WechatRobot.Application/Knowledge/Parsing/IDocumentParser.cs
- Create: src/server/WechatRobot.Application/Knowledge/Parsing/ParsedDocument.cs
- Create: src/server/WechatRobot.Infrastructure/Knowledge/Parsing/MarkdownTextParser.cs
- Create: src/server/WechatRobot.Infrastructure/Knowledge/Parsing/DocxParser.cs
- Create: src/server/WechatRobot.Infrastructure/Knowledge/Parsing/PdfTextParser.cs
- Create: src/server/WechatRobot.Application/Knowledge/Chunking/ChunkingService.cs
- Create: src/server/WechatRobot.Api/Knowledge/ChunkPreviewEndpoints.cs
- Create: tests/fixtures/documents/
- Create: tests/server/WechatRobot.UnitTests/Knowledge/Parsing/
- Create: tests/server/WechatRobot.UnitTests/Knowledge/Chunking/

**Interfaces:**

- Produces normalized blocks with source page/heading/table metadata and editable chunk previews.
- Consumes source streams and smart, advanced, or QA chunk policies.

- [ ] Add small, license-safe fixtures for UTF-8/GB18030 TXT, Markdown headings, DOCX headings/tables, text PDF pages, and an empty-text scanned PDF marker.
- [ ] Write failing parser tests that assert normalized text, heading hierarchy, table preservation, page numbers, encoding failures, and empty-text detection.
- [ ] Write failing chunk tests for smart boundaries, custom separator/regex, maximum length, 120-token overlap, QA question/synonyms/answer, merge, split, edit, and delete.
- [ ] Implement parser selection by verified media type. Apply page, size, memory, and execution-time limits.
- [ ] Implement chunk preview persistence separate from active indexed chunks. Default target length is approximately 800 tokens with 120-token overlap, both configurable.
- [ ] Add endpoints to generate, edit, merge, split, delete, and approve previews.
- [ ] Run parser/chunk tests and verify fixture outputs are deterministic across two runs.
- [ ] Commit:

```powershell
git add src/server tests
git commit -m "feat: parse and preview knowledge chunks"
```

### Task 12: Add the bounded PaddleOCR service and scanned-PDF fallback

**Files:**

- Create: src/ocr-service/pyproject.toml
- Create: src/ocr-service/app/main.py
- Create: src/ocr-service/app/models.py
- Create: src/ocr-service/app/ocr_engine.py
- Create: src/ocr-service/tests/test_api.py
- Create: src/ocr-service/Dockerfile
- Modify: docker-compose.yml
- Create: src/server/WechatRobot.Application/Ocr/IOcrClient.cs
- Create: src/server/WechatRobot.Infrastructure/Ocr/PaddleOcrClient.cs
- Create: src/server/WechatRobot.Infrastructure/Knowledge/Parsing/PdfOcrFallback.cs
- Create: tests/server/WechatRobot.ContractTests/Ocr/PaddleOcrContractTests.cs

**Interfaces:**

- Consumes bounded page images over private HTTP.
- Produces ordered text blocks, confidence values, page number, and partial-page error details.

- [ ] Write failing pytest cases for health, successful recognition through a fake engine, input size rejection, timeout mapping, and per-page failure.
- [ ] Write a failing .NET contract test for the OCR JSON schema and timeout/cancellation behavior.
- [ ] Implement FastAPI endpoints GET /health and POST /v1/ocr/pages. Keep PaddleOCR behind an injected adapter so tests do not download models.
- [ ] Render PDF pages only after text extraction falls below the configured threshold. Enforce maximum pages, pixels, bytes, render time, and OCR time.
- [ ] Persist page-level status so failed pages can be retried without rerunning successful pages.
- [ ] Add the OCR container and health dependency to Docker Compose without exposing the service outside localhost/private Compose networking.
- [ ] Run python -m pytest src/ocr-service/tests -q, the .NET contract tests, and docker compose config.
- [ ] Commit:

```powershell
git add src/ocr-service src/server docker-compose.yml tests/server
git commit -m "feat: add scanned PDF OCR pipeline"
```

### Task 13: Index approved chunks in Qdrant with tag filters and atomic versions

**Files:**

- Create: src/server/WechatRobot.Application/Knowledge/IKnowledgeService.cs
- Create: src/server/WechatRobot.Application/Knowledge/IVectorStore.cs
- Create: src/server/WechatRobot.Application/Knowledge/KnowledgeIndexService.cs
- Create: src/server/WechatRobot.Infrastructure/Knowledge/QdrantVectorStore.cs
- Create: src/server/WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/KnowledgeIndexJobEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Migrations/AddKnowledgeIndexJobs.cs
- Create: src/server/WechatRobot.Worker/Jobs/KnowledgeIndexWorker.cs
- Create: tests/server/WechatRobot.UnitTests/Knowledge/KnowledgeIndexServiceTests.cs
- Create: tests/server/WechatRobot.IntegrationTests/Knowledge/QdrantKnowledgeTests.cs

**Interfaces:**

- Consumes approved chunks, document/version IDs, tag IDs, and embedding vectors.
- Produces Qdrant points keyed by chunk ID with filterable tag IDs and active-version markers.

- [ ] Write failing unit tests for batching, retryability, dimension mismatch, and no activation until every chunk is indexed.
- [ ] Write Qdrant integration tests proving: group tags use OR, global-public works, unrelated tags never return, inactive versions never return, delete removes points, and reindex swaps versions only after completion.
- [ ] Create a named collection per configured embedding dimension and distance metric. Reject a model configuration change that silently changes dimensions; require an explicit reindex operation.
- [ ] Store full chunk text and audit metadata in MySQL. Store only retrieval payload needed for filtering/lookup in Qdrant.
- [ ] Implement new-version activation in a MySQL transaction after Qdrant indexing succeeds. Schedule old-version cleanup afterward.
- [ ] Add Admin/KnowledgeOperator endpoints for index, reindex, retry, disable, and consistency-check status.
- [ ] Run Qdrant integration tests against the pinned Docker image and the full server suite.
- [ ] Commit:

```powershell
git add src/server tests/server
git commit -m "feat: index tag-scoped knowledge in Qdrant"
```

## Checkpoint 2

- [ ] Upload one file of every supported format.
- [ ] Prove the source object key/public URL, parser output, editable preview, approved chunks, embedding calls, Qdrant points, and active version can be traced by one correlation ID.
- [ ] Bind 产品 and 售后 tags to 技术部 and prove either tag is retrievable while an unrelated 财务 tag is not.
- [ ] Record evidence in docs/runbooks/knowledge-pipeline-smoke-test.md.

## Wave 4: RAG, Context, Audit, and Human Handoff

### Task 14: Generate grounded replies with configurable conversation context

**Files:**

- Create: src/server/WechatRobot.Application/Conversations/ConversationContextService.cs
- Create: src/server/WechatRobot.Application/Conversations/RetrievalQueryBuilder.cs
- Create: src/server/WechatRobot.Application/Conversations/GroundedAnswerService.cs
- Create: src/server/WechatRobot.Application/Conversations/AnswerDecision.cs
- Modify: src/server/WechatRobot.Application/Messaging/InboundMessageProcessor.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/ConversationSessionEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/RetrievalAuditEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Migrations/AddConversationContextAndAudit.cs
- Create: tests/server/WechatRobot.UnitTests/Conversations/ConversationContextTests.cs
- Create: tests/server/WechatRobot.UnitTests/Conversations/GroundedAnswerTests.cs
- Create: tests/server/WechatRobot.IntegrationTests/Conversations/RagReplyPipelineTests.cs

**Interfaces:**

- Consumes the current question, effective context policy, allowed tag IDs, Qdrant candidates, and chat model.
- Produces a plain-text reply or a typed handoff/clarification/system-failure decision plus retrieval audit.

- [ ] Write failing tests for group-shared and per-sender sessions, six-turn default, 30-minute idle reset, token cap, optional summary, optional reuse of robot answers, and manual clear.
- [ ] Write failing grounded-answer tests for allowed evidence, insufficient evidence, provider timeout, Qdrant failure, sensitive-topic bypass, and no group-visible citations.
- [ ] Build retrieval input from current question plus only the permitted short-term context.
- [ ] Query Qdrant with allowed tag IDs and active-version filters. Load authoritative chunk text from MySQL and recheck status/tag scope before prompting.
- [ ] Use a prompt that explicitly forbids unsupported claims and source markers in the group response. Keep document/version/chunk/page/similarity/tag evidence in retrieval_audit.
- [ ] Make confidence thresholds configurable and record the calibrated value used. Do not hard-code an unvalidated universal score.
- [ ] Replace the fixed reply from Task 6 with GroundedAnswerService while retaining a test-only fake answer provider.
- [ ] Run focused tests and the full server suite.
- [ ] Commit:

```powershell
git add src/server tests/server
git commit -m "feat: answer group questions from scoped knowledge"
```

### Task 15: Implement human handoff and reviewed answer learning

**Files:**

- Create: src/server/WechatRobot.Domain/Handoffs/HandoffCase.cs
- Create: src/server/WechatRobot.Application/Handoffs/HandoffService.cs
- Create: src/server/WechatRobot.Application/Handoffs/KnowledgeCandidateService.cs
- Create: src/server/WechatRobot.Api/Handoffs/HandoffEndpoints.cs
- Create: src/server/WechatRobot.Api/Knowledge/KnowledgeReviewEndpoints.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/HandoffCaseEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/HandoffMessageEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/KnowledgeCandidateEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Entities/KnowledgeReviewEntity.cs
- Create: src/server/WechatRobot.Infrastructure/Persistence/Migrations/AddHandoffsAndReviews.cs
- Create: tests/server/WechatRobot.UnitTests/Handoffs/HandoffStateMachineTests.cs
- Create: tests/server/WechatRobot.IntegrationTests/Handoffs/HandoffPipelineTests.cs
- Create: tests/server/WechatRobot.IntegrationTests/Knowledge/HumanAnswerReviewTests.cs

**Interfaces:**

- Consumes explicit transfer phrases, low-confidence/no-evidence decisions, repeated failures, sensitive rules, and manual transfers.
- Produces an assigned handoff, AI pause scope, WorkTool at notification, candidate answer, review decision, and approved QA chunk.

- [ ] Write failing state-machine tests for AIActive -> WaitingHuman -> HumanHandling -> Resolved -> AIActive and reject invalid transitions.
- [ ] Cover whole-group versus current-sender pause, assignment, reassignment, manual restore, and duplicate transition idempotency.
- [ ] Write an integration test proving the handoff message is sent once, includes a WorkTool at target, and saves the exact reason/evidence/assignee.
- [ ] Record messages from the assigned employee while handling. On resolution, require selection or editing of the final answer and create a pending knowledge candidate.
- [ ] Require KnowledgeOperator or Admin review and tag selection. On approval, create a QA chunk, embed/index it, and only then mark the candidate published.
- [ ] Add a regression test: the original question transfers; after approval a semantically equivalent question retrieves the approved answer.
- [ ] Run focused state/integration tests and the full server suite.
- [ ] Commit:

```powershell
git add src/server tests/server
git commit -m "feat: add human handoff and reviewed learning"
```

### Task 16: Complete all Vue operational pages

**Files:**

- Create: src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentsView.vue
- Create: src/web/wechatrobot-admin/src/views/knowledge/DocumentDetailView.vue
- Create: src/web/wechatrobot-admin/src/views/knowledge/KnowledgeTagsView.vue
- Create: src/web/wechatrobot-admin/src/views/knowledge/KnowledgeReviewView.vue
- Create: src/web/wechatrobot-admin/src/views/audit/ConversationAuditView.vue
- Create: src/web/wechatrobot-admin/src/views/handoffs/HandoffQueueView.vue
- Create: src/web/wechatrobot-admin/src/views/models/ModelSettingsView.vue
- Create: src/web/wechatrobot-admin/src/views/users/UserRolesView.vue
- Create: src/web/wechatrobot-admin/src/views/settings/SystemSettingsView.vue
- Create: src/web/wechatrobot-admin/src/api/
- Create: src/web/wechatrobot-admin/src/**/*.spec.ts
- Modify: src/web/wechatrobot-admin/src/router/index.ts

**Interfaces:**

- Consumes the completed Admin, KnowledgeOperator, and HumanAgent APIs.
- Produces the approved pages: dashboard, knowledge, tags, group rules/management, models, audit, handoff, review, users/roles, and settings.

- [ ] Replace every Task 7 placeholder route and add a test that fails if a route component contains the placeholder marker.
- [ ] Write failing component tests for upload progress/errors, .doc conversion message, public OSS warning, chunk preview/edit/merge/split, indexing status/retry, tag OR help text, secret masking, audit evidence without secrets, handoff transitions, knowledge approval, and role denial.
- [ ] Implement list filtering, pagination, loading/empty/error states, destructive-action confirmation, and Beijing-time formatting through one shared utility.
- [ ] Do not display retrieval sources in the group reply preview. Display full sources only on the authorized audit page.
- [ ] Add accessible labels, keyboard focus, predictable confirmation copy, and responsive behavior for 1366px desktop and narrower operational screens.
- [ ] Run npm run test, npm run typecheck, npm run build, and a placeholder-marker scan that returns no matches.
- [ ] Commit:

```powershell
git add src/web/wechatrobot-admin
git commit -m "feat: complete knowledge and operations admin"
```

## Wave 5: Security, Operations, and Real Acceptance

### Task 17: Add health, observability, security limits, and Windows dev scripts

**Files:**

- Create: src/server/WechatRobot.Api/Health/HealthEndpoints.cs
- Create: src/server/WechatRobot.Infrastructure/Health/
- Create: src/server/WechatRobot.Api/Security/RateLimitPolicies.cs
- Create: src/server/WechatRobot.Api/Logging/RedactionEnricher.cs
- Create: scripts/start-dev.ps1
- Create: scripts/stop-dev.ps1
- Create: scripts/update-worktool-callback.ps1
- Create: docs/runbooks/local-development.md
- Create: docs/runbooks/credential-rotation.md
- Create: tests/server/WechatRobot.IntegrationTests/Operations/HealthTests.cs
- Create: tests/server/WechatRobot.UnitTests/Security/LogRedactionTests.cs

**Interfaces:**

- Produces liveness/readiness plus component status for MySQL, Qdrant, OCR, OSS configuration, and Worker heartbeat.
- Produces repeatable local startup/shutdown and callback URL update commands on Windows.

- [ ] Write failing health tests for healthy, degraded optional dependency, failed required dependency, and stale Worker heartbeat.
- [ ] Write failing log-redaction tests covering API keys, callback tokens, robot IDs, OSS secrets, Authorization headers, and encrypted ciphertext.
- [ ] Implement /health/live and authenticated /api/admin/health/ready. Public liveness must reveal no provider names or configuration.
- [ ] Add separate rate limits for login, callback, upload, WorkTool commands, and ordinary APIs.
- [ ] Add startup validation for master key, MySQL, allowed CORS origins, upload limits, send limit <= 60, and required encryption configuration.
- [ ] Make start-dev.ps1 validate Docker, start dependencies, apply migrations, launch API/Worker/Vite with PID/log files, and print health URLs. Make stop-dev.ps1 stop only processes recorded by this repository.
- [ ] Make update-worktool-callback.ps1 accept the current Cloudflare tunnel URL and update the configured robot only after displaying the final callback URL and receiving explicit confirmation.
- [ ] Run security/health tests and exercise start/stop twice to prove idempotency.
- [ ] Commit:

```powershell
git add src/server scripts docs/runbooks tests/server
git commit -m "chore: add secure local operations"
```

### Task 18: Add browser and opt-in WorkTool end-to-end acceptance

**Files:**

- Create: tests/e2e/package.json
- Create: tests/e2e/playwright.config.ts
- Create: tests/e2e/admin-workflows.spec.ts
- Create: tests/server/WechatRobot.ContractTests/WorkTool/RecordedCallbackSamples.cs
- Create: tests/server/WechatRobot.IntegrationTests/WorkTool/RealWorkToolAcceptanceTests.cs
- Create: docs/runbooks/worktool-real-group-acceptance.md
- Create: docs/runbooks/release-readiness.md

**Interfaces:**

- Consumes a locally running stack, safe test documents, and optionally the real 技术部 group.
- Produces reproducible evidence for all eleven end-to-end acceptance conditions in the approved design.

- [ ] Add Playwright 1.61.1 and write failing browser tests for login/roles, robot settings, group rules preview, document upload/chunk approval/indexing, audit evidence, handoff queue, and human-answer approval.
- [ ] Use API-seeded test data and fake external providers for default E2E. Assert that no real WorkTool base URL is contacted.
- [ ] Add a real test category that skips unless RUN_WORKTOOL_E2E=1 and all required secrets/group identifiers are present.
- [ ] For the real 技术部 run, record UTC timestamps and audit IDs for: no-at reply, duplicate callback, allowed/disallowed tags, no visible source, explicit transfer, employee notification, AI pause, human resolution, approval, and later semantic retrieval.
- [ ] Add a separate confirmed test for type 206 group creation and type 207 modification; do not run it as part of the ordinary 技术部 reply test.
- [ ] Document manual steps that cannot be automated: inviting the bot to an existing group, confirming Enterprise WeChat permissions, observing account risk controls, and verifying the external participants.
- [ ] Run:

```powershell
dotnet test WechatRobot.slnx
npm ci --prefix src/web/wechatrobot-admin
npm run test --prefix src/web/wechatrobot-admin
npm run typecheck --prefix src/web/wechatrobot-admin
npm run build --prefix src/web/wechatrobot-admin
python -m pytest src/ocr-service/tests -q
npm ci --prefix tests/e2e
npm test --prefix tests/e2e
docker compose config
```

- [ ] Run the real WorkTool test only after explicitly setting RUN_WORKTOOL_E2E=1 and confirming 技术部 is the intended target.
- [ ] Commit:

```powershell
git add tests docs/runbooks
git commit -m "test: add end-to-end acceptance coverage"
```

## Final Verification Gate

- [ ] Run git status --short and separate any unrelated user files from project changes.
- [ ] Run git diff --check.
- [ ] Run dotnet clean WechatRobot.slnx followed by dotnet build WechatRobot.slnx -warnaserror.
- [ ] Run all server, Vue, OCR, Playwright, and Docker Compose verification commands from Task 18.
- [ ] Start the stack from scripts/start-dev.ps1 and verify API liveness, authenticated readiness, Worker heartbeat, Vue root, MySQL, Qdrant, and OCR health.
- [ ] Verify the database contains no plaintext secrets and application logs contain none of the configured test secret values.
- [ ] Execute the approved real 技术部 checklist and attach only sanitized audit IDs/timestamps to docs/runbooks/release-readiness.md.
- [ ] Verify every page named in the design is reachable by the intended role and denied to unintended roles at the API.
- [ ] Verify public OSS risk copy remains visible and the group reply contains no source citation while audit contains complete evidence.
- [ ] Confirm all durable queues are empty or intentionally retained, no expired lease remains, and no dead letter is unexplained.
- [ ] Use superpowers:verification-before-completion before claiming the MVP is complete.
- [ ] Use superpowers:requesting-code-review for a final requirements and security review.

## Execution Notes

- Recommended execution order is strictly Task 1 through Task 18 because later contracts consume earlier persistence and provider boundaries.
- Each checkpoint is a stop-and-review gate. Do not start the next wave while its focused tests or smoke test are failing.
- If a provider contract differs from the recorded WorkTool samples, update the contract test and link the authoritative WorkTool documentation in the commit; do not silently loosen DTO validation.
- If MySql.EntityFrameworkCore, Qdrant, PaddleOCR, or frontend package versions change during implementation, verify the current official compatibility matrix before upgrading and update Directory.Packages.props/package lock files in a dedicated commit.
- Production Windows Server topology, high availability, private OSS conversion, and MaxKB provider implementation remain outside this plan.
