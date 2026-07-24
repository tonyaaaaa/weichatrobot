# Task 13 implementation report

## Scope

Implemented durable indexing of approved MySQL chunks into a pinned Qdrant collection with deterministic chunk IDs, tag filters, inactive staging, guarded MySQL activation, old-version cleanup, retry/lease state, consistency status and server-authorized operations. Qdrant payload contains only chunk/document/version/tag IDs and the active marker; authoritative text and audit/state remain in MySQL. No live model, OSS, OCR or WorkTool call was made.

## TDD evidence

### RED/GREEN - batching, retry, dimension and activation boundary

- RED: `KnowledgeIndexServiceTests` failed compilation because the wished `KnowledgeIndexService`, `IKnowledgeService`, `IVectorStore` and vector contracts did not exist.
- GREEN: the focused unit class passed 4/4. It proves point batches `2/2/1`, a retryable upsert is retried, dimension mismatch is a hard non-retryable configuration failure, and a failed final batch never activates either Qdrant or MySQL.

### RED/GREEN - real Qdrant visibility and collection contract

- RED: `QdrantKnowledgeTests` failed compilation because `QdrantVectorStore` did not exist.
- GREEN: a task-scoped `qdrant/qdrant:v1.18.2` container passed 2/2. It proves staged `active=false` points are invisible, group tags use OR, global-public is included, unrelated and inactive versions are excluded, deletion removes points, payload omits full text, and an existing collection rejects dimension or distance drift.

### RED/GREEN - authorized administration routes

- RED: `KnowledgeIndexEndpointTests` returned an empty route set.
- GREEN: it passed 1/1 after adding index, explicit reindex, retry, disable and consistency/status routes. Every route requires the server-side `KnowledgeOperator` policy, which also admits Admin by the existing policy definition.

### RED/GREEN - atomic MySQL activation and cleanup

- RED: the first real MySQL concurrency run failed because the winning transaction activated exactly one version but the tracked old-version cleanup entity was not saved; the cleanup collection was empty.
- GREEN: adding `SaveChangesAsync` before the same transaction commits made the same test pass 1/1 in 3m40s. Two independent contexts concurrently compete from the same old active version; exactly one wins, exactly one version is published, `ActiveVersionId` matches it, and exactly one cleanup job is committed with the swap.

### RED/GREEN - authoritative retrieval recheck

- RED: `KnowledgeRetrievalVisibilityTests` failed compilation because `SearchVisibleAsync` did not exist.
- GREEN: it passed 1/1. A deliberately malicious fake vector store returned both active and disabled-document hits; the MySQL recheck returned only the enabled active version and removed disabled tags before constructing the vector request.

## Implementation notes

- Collection names are deterministic: `kb_{distance}_{dimension}`. A queued job captures the collection contract, and Worker rejects it if runtime configuration changed without explicit reindex.
- Each chunk ID is its Qdrant point ID. Upserts are idempotent and partial batches remain `active=false`.
- All points are marked active only after all embeddings and upserts succeed. MySQL then atomically swaps the document's active version and collection contract. Retrieval always supplies MySQL-derived active version IDs and rechecks returned candidates against document/version/chunk/tag state.
- Durable index jobs use database leases and retry states. Initial index requests are idempotent per version; explicit reindex resets a completed/non-leased job, while a leased job cannot be replaced under a running worker.
- Disabling clears the MySQL active version immediately and queues durable Qdrant cleanup. Old-version cleanup is enqueued in the same transaction as activation.
- Embedding calls use the existing encrypted OpenAI-compatible embedding configuration. Unit tests use fakes; no provider call was made.

## Verification

- Focused Task 13 unit tests: PASS 4/4.
- Focused Qdrant/endpoint/retrieval integration tests: PASS 4/4.
- Real MySQL atomic concurrency test: PASS 1/1 in 3m40s after the attached RED fix.
- Full server unit suite: PASS 60/60.
- Full server contract suite: PASS 18/18.
- Solution build: `dotnet build WechatRobot.slnx --no-restore -warnaserror` — PASS, 0 warnings and 0 errors.
- EF model: `dotnet ef migrations has-pending-model-changes ... --no-build` — PASS, no pending model changes. The installed EF CLI 9.0.6 version-behind notice remains.
- `git diff --check` — PASS.

## Final cleanup-history and reversible-disable review

### RED/GREEN - failed staging generations remain discoverable

- RED: after a failed G1 staging write, explicit reindex reused the stable primary job row for G2. Querying for a G1 cleanup contract failed with `Sequence contains no elements`, proving the only copy of G1's collection/dimension/distance/generation had been overwritten.
- GREEN: before overwriting a non-active staging contract, `QueueIndexAsync` transactionally inserts a deterministic cleanup job keyed by version and collection. The immutable cleanup row records the source index job ID and generation contract. Repeated G1 to G2 to G3 reindex creates exactly one cleanup row per abandoned generation.
- The normal index cleanup Worker leases and completes these cleanup rows with the existing owner guard. Physical document cleanup discovers active, primary and completed/pending cleanup records, deletes them again idempotently and proves exact Qdrant absence.
- Real MySQL plus pinned Qdrant regression `Requeue_preserves_failed_generation_cleanup_and_physical_delete_removes_every_overwritten_generation` passes for G1/G2/G3, including normal cleanup and final physical-delete absence checks.

### RED/GREEN - disable is reversible and atomic

- RED: a MySQL command interceptor failed the cleanup-row insert. Without a surrounding transaction, the version remained persisted as `disabled` while the requested disable operation failed, proving partial state.
- GREEN: disable now uses one guarded MySQL transaction to clear retrieval activation, mark versions disabled/unpublished, cancel pending/retrying/leased/activating index ownership, and enqueue cleanup for active and staging generations. An injected cleanup failure rolls every document/version/job update back.
- Disable no longer writes `IsDeleteRequested`; that flag remains exclusive to Admin physical deletion. Normal index remains blocked while disabled. Existing explicit `/reindex` is the documented re-enable action and transactionally moves the selected disabled version/document back to `indexing` without changing the physical-delete flag.
- Authorization metadata was unchanged and the focused route-policy regression remains green.

### Final verification for this review

- Combined MySQL and pinned-Qdrant cleanup-history/disable tests: PASS 2/2 in 5m34s.
- Repeated G1/G2/G3 real cleanup regression: PASS 1/1 in 4m23s.
- Focused index unit tests: PASS 4/4.
- Focused retrieval, physical cleanup and authorization route tests: PASS 7/7.
- No unrelated full suite was rerun.

## Final re-review TDD wave

### RED/GREEN - physical delete versus an in-flight index write

- RED: a MySQL + real Qdrant regression blocked the vector call after Qdrant had durably accepted the point. Physical delete did not change the leased `KnowledgeIndexJob`, so the Worker retained ownership and the test timed out waiting for cancellation.
- GREEN: the physical-delete transaction now marks pending, retrying, leased and activating index/reindex jobs `cancelled`, clears their owner, and deliberately preserves the prior lease expiry and every job/collection contract. Lease renewal observes the ownership loss and cancels the provider/vector token.
- `KnowledgeDocumentCleanupWorker` waits through the preserved drain deadline, deletes all current contracts, rescans and deletes contracts again, then inspects every version/collection and completes the durable cleanup job only when exact Qdrant absence is proven. A missing Qdrant collection is treated as already absent.
- `Physical_delete_cancels_leased_index_and_cleanup_drains_then_removes_racing_qdrant_write` passes against MySQL and pinned `qdrant/qdrant:v1.18.2`. It proves the index job is cancelled, the cleanup durable job completes, and the racing Qdrant generation contains no remaining point.

### RED/GREEN - generation-scoped pending tag snapshot

- RED: a pinned-Qdrant test queued same-version tag B while tag A was active and immediately observed authoritative `KnowledgeChunkTags` changed from A to B before staging or activation.
- GREEN: each index/reindex job stores a deterministic, sorted JSON tag-ID snapshot in `PendingTagIdsJson`. Queueing does not mutate active tags; embedding/upsert reads the pending snapshot. The owner-guarded activation transaction switches document/version generation and authoritative chunk tags together before completing the owned job.
- `Same_version_tag_reindex_keeps_active_tags_until_atomic_activation_in_mysql_and_qdrant` passes. Before completion and after a staged failure, A remains retrievable and B is not; after successful activation, B is retrievable and A is not.
- Migration `AddKnowledgeIndexPendingTags` adds the required JSON snapshot column. Retry also rejects a failed job once its document has been physically deleted.

### RED/GREEN - null active generation consistency

- RED: exact point metadata with a null MySQL `ActiveIndexGeneration` was reported `consistent` because the point generation was used as its own expected value.
- GREEN: null expected generation now emits `missing-active-generation` and `drift`; only the persisted MySQL generation is accepted.

### Final re-review verification

- Combined MySQL + real pinned Qdrant race/tag tests: PASS 2/2 in 6m46s.
- Pinned Qdrant store suite: PASS 3/3 in 27s.
- Focused index unit tests: PASS 4/4.
- Focused retrieval/consistency and physical-cleanup tests: PASS 6/6.
- No full suite or duplicate MySQL test run was performed, per final review scope.
- The full integration suite was not run because its independent MySQL fixtures exceed the feasible Task 13 validation window; Task 13's real MySQL and pinned Qdrant paths were run directly.

## Review-fix TDD wave

This wave addresses every Critical/Important finding and the consistency-status finding from the first review. No live model, OSS, OCR or WorkTool call was made.

### RED/GREEN - durable active contract and explicit migration

- RED/code trace: retrieval selected active documents only when their persisted dimension, distance and collection exactly matched current runtime options. A runtime change therefore removed old active knowledge before reindex, while ordinary `/index` accepted the new contract.
- GREEN: every index/reindex job receives a unique staging collection generation. MySQL retains `ActiveCollectionName`, active dimension/distance and `ActiveIndexGeneration`; pending jobs retain the full previous active contract. Retrieval groups by persisted active contracts instead of runtime options. Ordinary indexing rejects an incompatible active contract and only explicit reindex can stage a migration.
- `Runtime_contract_change_keeps_old_active_collection_visible_and_requires_explicit_reindex` passes. It proves old knowledge remains visible after a 3/cosine to 4/dot runtime change, ordinary index is rejected, explicit reindex uses a new staging generation, and a failed staged job leaves the old collection visible.

### RED/GREEN - safe same-version generations

- RED/code trace: same-version reindex reused deterministic chunk IDs in the live collection and overwrote them with `active=false` batch by batch.
- GREEN: staging collection names include the base dimension/distance contract, job ID and monotonically increasing generation. Chunk IDs remain deterministic inside each collection. MySQL switches the active collection/generation only after all staging points become active, then transactionally enqueues old-collection cleanup.
- Real pinned Qdrant test `Same_version_reindex_stages_in_another_generation_and_failure_leaves_live_points_retrievable` passes. A partial inactive generation cannot be retrieved, the old live generation remains available, generation payload is verified, completed staging becomes searchable and old points are deleted afterward.

### RED/GREEN - physical delete cleanup

- RED/code trace: Task 10 created `CleanupKnowledgeDocument` durable jobs but no Worker leased that job type, so OSS objects and all Qdrant generations remained orphaned.
- GREEN: `KnowledgeDocumentCleanupWorker` leases the existing durable job, deletes every stored source object and every active, historical, pending or cleanup-recorded vector contract, and completes/retries through the existing durable repository. Operations are idempotent; Qdrant delete treats a missing collection as already clean. MySQL metadata remains available to drive retries and preserve the cleanup/audit contract.
- `Physical_delete_job_removes_every_oss_object_and_vector_generation_then_completes` passes 1/1 and proves a second pass has no job to reclaim. The first fixture run exposed a changing InMemory database name per scope; fixing the fixture to share one database made the same Worker path green.

### RED/GREEN - batch embeddings

- RED: wished batch contract/tests did not compile against the scalar `EmbeddingRequest`/single-vector interface, and the original service made one provider request per chunk.
- GREEN commit `f85b547`: the OpenAI-compatible client sends one `input` array per configured chunk batch, orders returned vectors by response `index`, rejects count/missing/duplicate/out-of-range index errors, and the index service validates every vector dimension before any batch upsert. Qdrant retry reuses already-created vectors and does not call the provider again.
- Focused `KnowledgeIndexServiceTests` pass 4/4; `OpenAiCompatibleClientTests` pass 3/3 with exact JSON and ordering assertions.

### RED/GREEN - lease renewal and ownership

- RED/code trace: the Worker held a fixed five-minute lease without renewal; activation, failure and cleanup completion did not require the current owner.
- GREEN: indexing uses a configurable lease and periodic renewal from independent DI scopes. Losing ownership cancels the in-flight provider/vector operation. Activation first conditionally moves the owned job to `activating`; activation completion, failure and cleanup completion all require the current owner. Reclaimed stale workers cannot switch MySQL or overwrite job state.
- Real MySQL shared-container tests prove a renewed short lease cannot be reclaimed, an expired owner can be recovered, and a Worker blocked in a slow embedding call renews repeatedly so a competing Worker receives no job. Provider call count remains exactly one and the owned job completes.

### RED/GREEN - exact consistency status

- RED/code trace: status compared only point count to chunk count and could report `consistent` for replaced IDs or corrupt payload.
- GREEN: Qdrant scroll inspection returns every point's chunk/document/version/tag/active/generation metadata. Status compares exact chunk IDs and exact required payload/tag/version/active/generation fields and returns `drift` with `missing:`, `unexpected:` or `payload:` details.
- `Consistency_check_reports_payload_drift_even_when_point_count_matches` passes with equal counts and deliberately wrong tags.

### Review-fix verification

- Full server Unit suite: PASS 60/60.
- Full server Contract suite: PASS 20/20.
- Focused non-Docker index endpoint, active-contract migration, exact consistency and physical cleanup integration tests: PASS 5/5.
- Pinned `qdrant/qdrant:v1.18.2` tests: PASS 3/3 in 28s; task-scoped container cleaned automatically.
- MySQL atomic generation activation: PASS 1/1 in 4m16s after isolating the assertion by document ID.
- In the preceding shared MySQL run, lease renewal/recovery and slow-Worker ownership tests passed; the only failing assertion was the now-fixed cross-test unfiltered document query.
- Solution: `dotnet build WechatRobot.slnx --no-restore -warnaserror` — PASS, 0 warnings and 0 errors.
- EF: no pending model changes after `AddKnowledgeIndexGenerations`.
- `git diff --check` — PASS.

## 0ee317d/corrective provenance RED/GREEN

- RED command: `dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore --filter-method "*Same_version_tag_reindex_keeps_active_tags_until_atomic_activation_in_mysql_and_qdrant" --no-progress --timeout 10m`.
- RED result: FAIL 0/1. After exclusive G1 was active, same-version G2 failed and the mutable stable job row was overwritten by G3; the G1 cleanup record incorrectly had `IsCollectionExclusive=false` (`Expected: True`, `Actual: False`).
- GREEN: `ActiveCollectionExclusive` and `IndexCollectionExclusive` are authoritative persisted state. Queue snapshots the former into `PreviousActiveCollectionExclusive`; activation creates old-generation cleanup from that snapshot and atomically writes the winning staging exclusivity to document and version. Physical cleanup enumerates persisted document/version provenance; legacy defaults remain `false` and use version-point deletion.
- GREEN command: the same focused MySQL + pinned `qdrant/qdrant:v1.18.2` command above.
- GREEN result: PASS 1/1 in 4m30s. Normal cleanup deleted exclusive G1 and verified collection 404, G3 remained searchable, and physical cleanup then deleted G3 and verified collection 404.
- Affected cleanup command: `dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore --filter-method "*Physical_delete_job_removes_every_oss_object_and_vector_generation_then_completes" --no-progress --timeout 3m` — PASS 1/1.
- Solution build: `dotnet build WechatRobot.slnx --no-restore` — PASS, 0 warnings and 0 errors.
- EF: migration `PersistActiveCollectionProvenance` generated with legacy-safe `false` defaults; `dotnet ef migrations has-pending-model-changes ... --no-build` reports no pending changes.
- `git diff --check` — PASS.
