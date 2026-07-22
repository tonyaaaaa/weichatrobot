# Task 15 report - human handoff and reviewed learning

## Delivered

- Added the durable handoff lifecycle `AIActive -> WaitingHuman -> HumanHandling -> Resolved -> AIActive`, invalid-transition rejection, optimistic versions, and idempotent duplicate start/assign/resolve/restore behavior.
- Added group pause and stable-sender pause semantics. Sender pause is rejected without a stable sender identifier; display names are never used as identity keys.
- Added automatic handoff triggers for explicit phrases, configured policy handoffs (including sensitive/no-evidence policy), and repeated system failures, plus authenticated manual transfer.
- Added one local-idempotent Task 6 `send_command` notification with the WorkTool type-203 native `atList` mention contract, sanitized reason code, and correlation id. External delivery remains at-least-once.
- Persisted structured reason/evidence, assignee, unverified WorkTool messages, authenticated resolution messages, and final answer. WorkTool display names are marked `worktool_display_name_unverified` and cannot resolve or publish.
- Added authenticated HumanAgent/Admin endpoints for manual transfer, assignment, resolution, AI restore, paged queue/detail, messages, and transitions. Added KnowledgeOperator/Admin review plus paged candidate queue/detail endpoints.
- Resolution creates one pending knowledge candidate. Approval atomically validates enabled tags and writes the review, request fingerprint, synthetic QA document/version/chunk, `approved_pending_index` candidate, and deterministic `PublishKnowledgeCandidate` durable outbox. The publisher owner-guards and atomically moves the candidate to `indexing` in the same MySQL transaction that makes Task 13 claimable; activation alone marks the candidate `published`. Failed index jobs are reopened on idempotent review replay.
- Handoff-triggered questions commit a typed Handoff audit and terminal inbound lifecycle without persisting or sending the calculated AI answer. Manual handoff creation and final answer commit serialize on the same `group_profile` row; the final transaction rechecks active handoffs so the losing answer path writes no answer or send command.
- Every persisted transition uses the domain state machine and an ordered, idempotent transition audit. Manual routing is derived server-side from the source message, enabled group/robot mapping, and validated Identity username mention target.
- Added auditable, idempotent approve/reject/revision decisions and an EF migration with relational constraints.
- Paused inbound group messages are retained in conversation history and captured as unverified handoff messages; no AI answer is generated while paused.

## TDD and verification

- RED observed for the original feature plus review regressions: non-durable post-commit indexing, AI-answer leakage on transfer, missing transition ordering, and relational concurrent transition cross-loss.
- GREEN:
  - `dotnet build WechatRobot.slnx --no-restore`: 0 warnings, 0 errors.
  - Full unit suite: 124 passed.
  - Full contract suite: 26 passed.
  - Focused handoff persistence + API authorization integration: 9 passed.
  - Real MySQL 8.4.10 + pinned Qdrant 1.18.2 regression passed in 5m39s: explicit transfer, authenticated resolution, review/outbox crash repair, owner-guarded atomic queueing, index activation before publisher completion, fake embeddings that distinguish equivalent/unrelated questions, real vector activation/retrieval, and Qdrant cleanup.
  - The same real MySQL test verifies invalid/disabled tag rejection without review writes, independent-context concurrent approval with one winner, failed-index replay reopening, one durable notification under concurrent start, ordered transition uniqueness, assignment CAS, and handoff-vs-answer transaction serialization with no leaked answer/send.
  - EF migration SQL generation confirmed migration-safe orphan cleanup, transition constraints, nullable Identity audit foreign keys, and candidate foreign keys to question/handoff/knowledge version.

## Provider boundary

- No live WorkTool, model, or embedding provider was called. The semantic regression uses deterministic fake model/embedding clients and an isolated pinned Qdrant Testcontainer.
