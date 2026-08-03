# Dynamic Reply Lane Configuration Design

## Goal

Allow operators to configure the Worker concurrency of group replies, private replies, and private knowledge ingestion in `appsettings.json`, with safe runtime resizing and no administration UI or database-backed setting.

## Configuration contract

The Worker configuration adds this non-secret section:

```json
"ReplyProcessing": {
  "GroupLaneCount": 8,
  "PrivateLaneCount": 4,
  "PrivateKnowledgeIngestLaneCount": 4
}
```

The code defaults and committed Worker configuration use `8`, `4`, and `4`. Local development may place the same section in `.local/appsettings.json`; `.local` remains local-only and must not be committed.

Allowed ranges are:

- `GroupLaneCount`: 1 through 32.
- `PrivateLaneCount`: 1 through 16.
- `PrivateKnowledgeIngestLaneCount`: 1 through 8.

The Worker must fail startup when the initial values are outside these ranges. A later invalid file reload must keep the last valid lane counts and write a sanitized warning without logging configuration contents.

## Runtime behavior

The Worker listens for JSON configuration reloads. Increasing a lane count starts the additional consumers without restarting the process. Decreasing a lane count retires excess consumers only after their current job completes; an in-flight LLM, retrieval, persistence, or message-delivery operation is not cancelled merely because concurrency was reduced.

Repeated changes converge on the newest valid desired counts. Returning from an invalid configuration to a valid configuration resumes normal resizing. Application shutdown continues to cancel all consumers through the host stopping token.

JSON file updates normally become visible within the configuration provider reload delay. Environment-variable overrides are not hot-reloadable and require a Worker restart.

## Architecture

`DurableReplyLaneOptions` owns the section name, defaults, and range validation. `DurableReplyLanePlan` creates stable unique lane identities from validated counts. `DurableJobWorker` supervises the active lane tasks, reconciles them with the newest valid options, and separates graceful lane retirement from application shutdown cancellation.

The existing MySQL durable-job repository remains the only work queue. The implementation does not introduce an in-memory message queue, third-party scheduler, database migration, administration API, audit history, or frontend control.

## Configuration files

The committed `src/server/WechatRobot.Worker/appsettings.json` contains the production/package defaults and documents that only this section supports hot reload. Development defaults remain aligned where applicable. The local runtime file `.local/appsettings.json` receives `8/4/4` for local verification but is never staged or committed.

## Failure handling

- Startup configuration is validated before processing begins.
- Invalid hot reloads preserve the last valid desired counts.
- A lane failure is logged and recovered without terminating sibling lanes.
- Scaling down does not take a new job after the current job finishes.
- If configuration monitoring fails transiently, existing lanes continue operating.

## Verification

Tests cover default `8/4/4` planning, all three configurable counts, range validation, scale-up, graceful scale-down, invalid-reload retention, recovery after a valid reload, unique lane names, and clean Worker shutdown. Verification also includes the complete backend unit-test project, Worker publish, archive inspection, and `git diff --check`.

The release output is packaged as a ZIP under `artifacts/`, excluding local secrets and `.local` files.
