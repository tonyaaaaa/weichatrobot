# WechatRobot Agent Instructions

## Project overview

WechatRobot is an AI knowledge-base group-chat platform for WeCom and WorkTool
scenarios. It receives group messages, retrieves authorized knowledge, invokes
configured models, sends automated replies, supports human handoff and
knowledge review, and exposes an administration UI.

Treat the repository and verified external-platform behavior as the source of
truth. Do not infer unsupported WeCom or WorkTool capabilities from product
names, display names, UI labels, or planned documents.

## Technology stack

- Backend: ASP.NET Core 10, split into an HTTP API and background Worker.
- Frontend: Vue 3, TypeScript, Vite, and Element Plus.
- Business database: MySQL with Entity Framework Core migrations.
- Vector database: Qdrant.
- Messaging and robot control: WorkTool.
- External providers: OCR, OSS-compatible object storage, and
  OpenAI-compatible chat and embedding APIs.
- Testing: xUnit v3/Microsoft Testing Platform, Vitest, and Playwright.

Use the versions and centrally managed dependencies already declared in
`global.json`, `Directory.Build.props`, `Directory.Packages.props`, and the
frontend package files. Do not introduce a second dependency-management
pattern.

## Repository map

- `src/server/WechatRobot.Domain`: domain models and business invariants.
- `src/server/WechatRobot.Application`: use cases, application contracts, and
  interfaces implemented by outer layers.
- `src/server/WechatRobot.Infrastructure`: EF Core persistence, external
  clients, durable jobs, and provider implementations.
- `src/server/WechatRobot.Api`: HTTP endpoints, authentication, authorization,
  callback ingress, and administration APIs.
- `src/server/WechatRobot.Worker`: retryable and long-running background work.
- `src/server/WechatRobot.PdfRenderer`: isolated PDF rendering support.
- `src/web/wechatrobot-admin`: Vue administration application.
- `tests/server`: backend unit, contract, and integration tests.
- `tests/e2e`: Playwright end-to-end tests.
- `tools`: bounded operational and evidence-gathering utilities.
- `scripts`: local operations and callback-management scripts.
- `docs/runbooks`: operational, deployment, and acceptance procedures.
- `docs/superpowers/specs`: approved designs.
- `docs/superpowers/plans`: implementation plans.

Check for more specific nested `AGENTS.md` files before editing a subtree. A
more specific file overrides this repository-level guide for its scope.

## Architecture boundaries

- Keep dependencies pointing inward: Domain must not depend on Application,
  Infrastructure, API, Worker, or the frontend.
- Application owns use-case contracts; Infrastructure implements persistence
  and external-service details.
- API endpoints own transport, authentication, authorization, validation, and
  fast acknowledgement. Keep slow or retryable work out of callback request
  handling.
- Worker owns durable processing, retries, leases, reconciliation, indexing,
  and other background workflows.
- Reuse existing stores, services, typed clients, queues, and outboxes before
  adding parallel abstractions.
- Preserve backend contracts in the frontend. Never fabricate lists, CRUD
  behavior, platform capabilities, or success states when no backend contract
  exists.

## Local startup

- Treat `.local` as the source of truth for local runtime configuration.
- Load environment variables from `.local/.env` by setting
  `WECHATROBOT_ENV_FILE` to its absolute path.
- Start the API and Worker with `.local` as their working directory so they
  load `.local/appsettings.json`.
- Do not use or create a repository-root `.env` for local startup.
- Keep `.local` local-only. Never commit its files or print their values.
- The default local endpoints are API `http://127.0.0.1:5268` and admin UI
  `http://127.0.0.1:5173`.
- After startup, verify:
  - `http://127.0.0.1:5268/health/live` returns healthy.
  - Authenticated `/api/admin/health/ready` reports required dependencies.
  - The Worker heartbeat is fresh.
  - The frontend returns HTTP 200.
- A liveness response proves only that the API process is running. It does not
  prove MySQL, Qdrant, providers, Worker, authentication, or callbacks are
  ready.

## Configuration and security

- Never expose API keys, passwords, JWTs, connection strings, callback tokens,
  robot identifiers, decrypted credentials, or secret-bearing URLs in source,
  commands, logs, tests, screenshots, or responses.
- Keep secret values in the approved environment configuration. Keep only
  non-secret runtime settings in `appsettings` files.
- Preserve the existing encrypted-at-rest model for robot credentials and
  model-provider API keys. The encryption master key must remain environment
  supplied.
- Use the existing redaction and safe-failure-code boundaries. Do not return
  upstream response bodies or exception details that may contain secrets.
- Never read or display `.local` values merely to explain configuration.
  Inspect variable names or presence only when possible.
- Do not weaken authentication, authorization, rate limits, callback-token
  validation, audit logging, or transport validation for local convenience.

## Working tree safety

- Before substantive work, run `git status --short`, `git branch
  --show-current`, and `git worktree list`.
- Treat all existing modifications and untracked files as user-owned unless
  the current task clearly created them.
- Preserve unrelated edits. Do not revert, overwrite, reformat, stage, or
  include them in the current change.
- Keep changes scoped to the smallest relevant files. Avoid opportunistic
  refactors, mass formatting, and unrelated dependency updates.
- Do not run destructive Git or filesystem commands against broad or
  unresolved paths.
- Do not commit, push, create a PR, deploy, reset, clean, or delete data unless
  the user explicitly requests that action.
- If the active checkout or branch is ambiguous, inspect worktrees before
  deciding that code is missing or before moving implementation elsewhere.

## Backend development

- Follow the existing dependency-injection and layer ownership patterns.
- Use async APIs and propagate `CancellationToken` through I/O and database
  boundaries.
- Use existing typed `HttpClient` registrations and transport handlers for
  external services. Preserve endpoint-specific timeout, retry, connection,
  rate-limit, and redaction behavior.
- Retry only operations whose delivery semantics make retry safe. Do not retry
  non-idempotent message delivery merely because administrative probes retry.
- Validate requests at the boundary and return sanitized, stable failure
  contracts.
- Preserve authorization policies and administration audit records when
  adding or changing administrative actions.
- Keep controllers and minimal API handlers thin; place reusable behavior in
  Application or Infrastructure services according to ownership.
- Use the repository clock/time abstractions where present instead of
  scattering direct wall-clock calls.

## Frontend development

- Reuse the API modules and shared TypeScript types under
  `src/web/wechatrobot-admin/src/api`.
- Follow existing Vue 3 Composition API, router, state, and Element Plus
  interaction patterns.
- Keep server and frontend contracts aligned: routes, request bodies,
  response types, validation, error handling, and workflow states must change
  together.
- Preserve exact user-facing terminology supplied by the user. Do not merge
  distinct platform concepts into one label.
- Keep administrative pages read-only when mutation APIs or authoritative data
  sources do not exist.
- Provide visible loading, success, empty, and failure states for asynchronous
  operations.
- Maintain keyboard access, labels, focus behavior, and responsive layout when
  changing UI.
- Do not redesign unrelated pages while implementing a focused change.

## WorkTool integration

- Verify WorkTool behavior against current official documentation, recorded
  real samples, and the actual code path before changing contracts.
- Treat HTTP success and WorkTool business success as separate checks. Use the
  success codes verified for the specific endpoint; do not apply one global
  assumption to every WorkTool API.
- Do not silently convert malformed or rejected WorkTool responses into a
  configured, delivered, online, or otherwise successful state.
- WorkTool display fields such as received names, group names, and master
  names are not stable member-directory identifiers.
- Do not implement member synchronization or assignee identity matching from
  display names alone. Require an authoritative WeCom identity source or an
  explicit manual-binding design.
- Keep message callbacks and command-result callbacks as separate platform
  concepts and verify both after configuration.
- Public callback configuration must use a reachable HTTPS origin, not
  `127.0.0.1` or another loopback address.
- Never expose callback tokens, robot credentials, or secret-bearing callback
  URLs in API responses, logs, audit detail, or test failure messages.
- When adding command types or callback fields, capture the real response
  shape and add a contract test before relying on it in production logic.

## Data and migrations

- Keep EF Core entities, mappings, migrations, and the model snapshot aligned.
- Generate a new migration for schema changes. Do not rewrite an already
  applied migration.
- Review generated migrations for destructive operations, unintended column
  changes, defaults, indexes, and provider-specific behavior.
- Never run data cleanup or migrations against an unconfirmed database target.
- Bound queries and batch operations. Avoid unbounded scans, N+1 queries, and
  loading entire datasets when a database-side filter is available.
- With `MySql.EntityFrameworkCore`, never translate `Contains` over a runtime
  `Guid[]`, `List<Guid>`, or other Guid collection into SQL. Use
  `GuidBatchQuery.CreateBatches` and `GuidBatchQuery.BuildPredicate` with at
  most 100 IDs per batch; do not replace batching with one query per ID.
- Treat `RelationalCommand.CreateDbCommand` or
  `TypeMappedRelationalParameter.AddDbParameter` null references as a likely
  provider-query compatibility failure. Check runtime collection parameters
  and bulk updates before blaming empty business data.
- Cover provider-sensitive `ExecuteUpdateAsync`/`ExecuteDeleteAsync`
  expressions, especially nullable assignments, with real MySQL integration
  coverage or an equivalent provider-boundary regression test. Follow
  `docs/runbooks/mysql-ef-provider-query-compatibility.md`.
- Treat MySQL state and Qdrant state as separate sources that require separate
  verification.
- Preserve durable job idempotency, leases, generations, retry metadata, and
  activation/cleanup ordering.
- Model and embedding changes must preserve the configuration ID, version, and
  vector-dimension contract used by queued or active index work.

## Testing and verification

Choose the smallest verification set that fully covers the changed behavior,
then expand when the change crosses boundaries.

- Backend unit tests:
  `tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj`
- WorkTool and external contracts:
  `tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj`
- Database and API integration:
  `tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj`
- Frontend from `src/web/wechatrobot-admin`:
  - `npm run typecheck`
  - `npm test -- --run`
  - `npm run build`
- End-to-end from `tests/e2e`: `npm test`
- Diff hygiene: `git diff --check`

Additional rules:

- For a bug fix, first add the narrowest practical regression test that
  reproduces the original symptom and confirm it fails for the expected
  reason.
- Run focused tests while iterating, then the relevant complete test project or
  frontend suite before completion.
- Contract changes require contract tests. Persistence changes require
  integration coverage. UI behavior changes require component tests and,
  when workflow-critical, end-to-end coverage.
- Runtime changes require fresh process and endpoint evidence after rebuilding
  and restarting the affected process.
- Old logs, stale binaries, mocked success, compilation alone, or an HTTP 200
  from an outer proxy are not proof that the requested behavior works.
- Report unrelated baseline failures separately; do not silently fix or hide
  them unless they block the requested work and the user authorizes expansion.

## Documentation

- Keep stable operational procedures in `docs/runbooks/`.
- Keep approved designs in `docs/superpowers/specs/`.
- Keep implementation plans in `docs/superpowers/plans/`.
- Update documentation when a public contract, architecture boundary, startup
  method, deployment requirement, or operational recovery path changes.
- Do not link Agent instructions to `.local`, transient logs, generated
  evidence, or one-off scratch output.
- Prefer concise links to the authoritative document over copying long,
  drift-prone procedures into this file.

## Delivery checklist

Before reporting completion:

- Re-read the request and confirm every explicit requirement is covered.
- Review `git diff` and distinguish current-task changes from pre-existing
  changes.
- Run the relevant tests, builds, formatting checks, and runtime probes.
- Confirm no secret or sensitive identifier appears in source, output, logs,
  screenshots, or diff.
- Report changed files, verification commands and results, and any remaining
  blocker or unverified boundary.
- State explicitly when a full build or live external verification was blocked
  by unrelated code, unavailable infrastructure, permissions, or credentials.
- Do not claim success for work that was not freshly verified.
