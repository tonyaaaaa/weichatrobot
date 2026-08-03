# Shared Knowledge Vector Collection Design

## Goal

Replace the current per-document-version Qdrant collection strategy with shared collections keyed by embedding-space contract. The change must restore RAG retrieval for the 322 active visa documents, preserve existing tag authorization and version semantics, and prevent collection fan-out from growing with the number of documents or tags.

The production migration may temporarily stop knowledge-answer processing. It must reuse the vectors already stored in Qdrant, validate every migrated active version, and retain the old exclusive collections until the shared collection has passed acceptance checks.

## Current problem

Every index job currently creates an exclusive staging collection named like:

```text
kb_cosine_1024_g1_<job-id>
```

After activation, each document points to its own collection. Retrieval resolves the visible active versions in MySQL, groups them by collection, and sends one Qdrant search per collection. `MaximumCollectionsPerSearch` is 64, while the enabled `签证知识` tag currently exposes more than 64 active exclusive collections. `QdrantKnowledgeService.SearchVisibleAsync` therefore raises `KnowledgeSearchCapacityException` before knowledge-vector search begins. `GroundedAnswerService` maps this to `retrieval_unavailable` and sends the configured system-failure reply.

Increasing the limit is not an acceptable fix. The configured range stops at 256, the current visa corpus already contains 322 active documents, and unbounded search fan-out would increase latency and Qdrant load for every answer.

## Scope

This design covers:

- Deterministic shared-collection identity.
- Shared-collection indexing, activation, replacement, cleanup, and retrieval.
- Qdrant payload indexes required for filtered search.
- A resumable offline migration of active vectors and MySQL collection references.
- Rollback and deletion safeguards.
- Regression coverage for private and group RAG retrieval.
- A separate narrow diagnosis and fix for the observed non-fatal EF Core/MySQL exception in memory recall.

It does not change document parsing, chunk text, embeddings, tag-binding rules, conversation context, confidence thresholds, answer generation, or model-provider selection. It does not merge vectors produced by incompatible embedding spaces.

## Collection boundary

Documents are not partitioned by tag. A chunk can have multiple tags, groups can bind multiple tags with OR semantics, and globally public tags participate without an explicit group binding. A tag-per-collection design would either duplicate vectors or lose multi-tag visibility, and tag growth would eventually recreate the same fan-out problem.

One shared collection represents one embedding-space contract:

```text
embedding provider/model identity + vector dimension + distance algorithm
```

The implementation derives a non-secret, deterministic contract key from the semantic model settings and vector contract. The key excludes credentials and operational settings such as timeout or retry count. The Qdrant collection name uses a bounded hash of that key, for example:

```text
kb_<contract-hash>_cosine_1024
```

Different embedding models remain in different collections even when their vector dimensions match. Active document and version records persist the contract key used to produce their vectors. Changing to an incompatible contract requires explicit reindexing; retrieval never compares a query vector with points from another embedding space.

## Vector payload and indexes

The existing point identity remains the globally unique chunk ID. Every shared-collection point retains:

```text
chunk_id
document_id
version_id
tag_ids
active
generation
```

Collection creation also ensures Qdrant payload indexes for:

- `active` as a boolean field.
- `version_id` as a keyword field.
- `document_id` as a keyword field.
- `tag_ids` as a keyword field.

Index creation is idempotent and validates existing field schemas. An incompatible collection or payload-index schema fails safely before activation.

## Index and activation flow

New and reindexed versions write to the deterministic shared collection and set `IsCollectionExclusive` to `false`. Points are initially written with `active=false`.

Activation follows this order:

1. Ensure the shared collection and payload indexes exist.
2. Upsert all new-version points as inactive and verify their IDs and metadata.
3. Set the new version's points to `active=true`.
4. In a MySQL transaction, switch the document's active version, collection, contract key, dimension, distance, and generation; publish the new version; and complete the index job.
5. Set the previous version's points to `active=false`.
6. Queue cleanup that deletes only the previous `version_id` from the shared collection.

This ordering preserves correct answers across the Qdrant/MySQL boundary. Before the MySQL switch, retrieval includes only the old active version ID, so pre-activated new points are ignored. After the switch, retrieval includes only the new active version ID, so a still-active old payload is ignored. A crash cannot make an unselected version visible because every search includes the MySQL-authoritative active version IDs.

Candidate and private-ingest batch activation retain their existing all-or-nothing MySQL transition. All new versions are uploaded and verified before activation. The batch pre-activates its new Qdrant versions, commits the existing MySQL batch switch, then deactivates and cleans the previous versions.

## Cleanup safety

Cleanup behavior remains provenance-aware:

- An exclusive collection may be deleted only when its recorded exclusive provenance is true and no active, historical, pending, or leased contract still references it.
- A shared collection is never deleted by document, version, retry, disable, replacement, or physical-delete workflows.
- Shared cleanup calls `DeleteVersionAsync` with the exact `version_id`.
- Collection deletion rejects known shared-collection names even if a malformed job incorrectly claims exclusivity.

Consistency checks continue to inspect one version at a time. Disabling or physically deleting one document removes only that document version's points and cannot affect other documents in the collection.

## Retrieval flow

MySQL remains authoritative for visibility. Retrieval first resolves the current default embedding configuration and its contract key, then resolves enabled effective tags and active published versions produced by that same contract. It sends one Qdrant request to that contract's shared collection using all three filters:

```text
active = true
version_id IN effective active version IDs
tag_ids IN effective visible tag IDs
```

Results are ranked using the existing bounded candidate policy. One answer performs one knowledge-vector search regardless of document or tag count. An active document with a different embedding contract is configuration drift and must be explicitly reindexed before it can participate; the system does not compare vectors from incompatible spaces or silently omit an otherwise authorized document.

`MaximumCollectionsPerSearch` remains temporarily for legacy exclusive collections during migration and as a configuration-drift guard. It no longer scales with document count and must not be used to silently omit eligible collections. After migration, the expected collection count for the active query contract is exactly one.

## Offline production migration

The migration is implemented as a bounded operational command that loads the approved `.local` environment and configuration without printing secrets. It supports dry-run, apply, resume, verification, and rollback-before-cleanup modes. Its checkpoint contains only non-secret IDs, collection names, counts, hashes, and migration state.

The apply run follows these steps:

1. Deploy the shared-collection-capable binaries without starting the Worker.
2. Stop the existing Worker and confirm there are no leased or activating index jobs.
3. Allow the API to keep acknowledging callback ingress; inbound work remains durable until the Worker restarts. Administrative indexing must not be run during the maintenance window.
4. Resolve every active document/version, its source exclusive collection, vector contract, approved chunk IDs, and expected point count.
5. Create the destination shared collection and payload indexes for each distinct embedding contract.
6. Scroll source points in bounded pages with vectors and payloads, validate ownership, and upsert them into the destination with their current active state.
7. Verify every version independently: point count, chunk IDs, document ID, version ID, tag IDs, generation, and active state must match MySQL and the source collection.
8. In one bounded MySQL transaction, update the migrated active documents and versions to the shared collection, persist the embedding contract key, and set exclusive provenance to false.
9. Execute authenticated private and group retrieval probes and confirm the audit result is not `retrieval_unavailable`.
10. Restart the Worker and verify liveness, readiness, heartbeat, queue progress, and real retrieval behavior.
11. After acceptance, enqueue deletion of the old exclusive collections and verify they are no longer referenced before physical deletion.

The migration is idempotent. A destination point upsert may be repeated because chunk IDs are stable. A completed version is skipped only after its destination metadata passes verification. Any mismatch stops before the MySQL switch. Old collections remain authoritative and recoverable until the acceptance gate completes.

## Rollback

Before old-collection cleanup, rollback stops the Worker, restores the saved active collection and exclusive-provenance mapping in one MySQL transaction, and reruns consistency checks against the original collections. The destination shared collection may remain orphaned because MySQL active-version and collection filters prevent it from participating in retrieval.

Old exclusive collections are deleted only after the user-visible retrieval probes and post-restart checks pass. After that deletion gate, rollback requires reindexing or restoring the Qdrant backup and is not presented as an immediate mapping-only operation.

## Failure handling and observability

Migration and runtime logs use safe stable codes and counts. They do not log vectors, chunk text, credentials, connection strings, upstream response bodies, or secret-bearing URLs.

Runtime failures distinguish collection-capacity, embedding-contract mismatch, payload-index mismatch, Qdrant unavailability, migration consistency mismatch, and activation-reconciliation failure. Retrieval audit continues to record the sanitized public failure code.

An activation reconciler repairs safe partial states:

- New version active in Qdrant but not selected in MySQL: leave it invisible and retry or clean it after the owning job is resolved.
- New version selected in MySQL but old points still active: deactivate the old version; retrieval remains correct because of the version filter.
- New version selected in MySQL but its points are missing or inactive: mark the index inconsistent and block cleanup of the old collection so operations can roll back.

## Memory-recall query defect

The production logs also show an EF Core/MySQL `NullReferenceException` while iterating a memory-entry query after memory-vector search. Memory recall currently catches this exception and the main knowledge retrieval continues, so it is not the cause of the collection-capacity failure.

Implementation must first add the narrowest regression that reproduces the query failure with the production MySQL provider and current sender/scope inputs. The fix stays inside memory recall or its query helper, preserves sender isolation and bounded GUID batching, and keeps memory failure non-fatal. It must not weaken knowledge retrieval or convert memory contents into logs.

## Verification

Automated coverage includes:

- More than 64 active documents sharing one contract produce one collection search and return ranked evidence.
- Multi-tag chunks are stored once and remain visible through any allowed tag with existing OR semantics.
- Globally public and group-bound tag behavior remains unchanged.
- Different embedding contracts never share a collection or result set.
- Payload indexes are created idempotently and incompatible schemas fail safely.
- New-version activation remains correct at every injected failure boundary between Qdrant and MySQL.
- Shared cleanup deletes one version and cannot call collection deletion.
- Exclusive cleanup still deletes an unreferenced legacy collection.
- Disable, physical delete, candidate publish, private-ingest batch activation, retry, and consistency inspection remain document-scoped.
- Migration dry-run is read-only; apply is resumable; mismatched counts prevent the database switch; rollback restores original mappings before cleanup.
- The memory-recall MySQL regression no longer throws while isolation and bounds remain intact.

Fresh runtime acceptance requires:

- API liveness and authenticated readiness are healthy.
- Worker heartbeat is fresh after restart.
- The shared collection's per-version point counts match approved MySQL chunks.
- A private question and a group question over `签证知识` reach RAG retrieval and no longer audit as `retrieval_unavailable`.
- Updating one test document activates its new version without affecting another document in the same collection.
- No old exclusive collection is deleted until the explicit cleanup acceptance gate.

## Deliverables

- Shared-collection identity and persisted embedding-contract metadata.
- Qdrant payload-index management and shared indexing/retrieval behavior.
- Provenance-safe activation, cleanup, disable, and physical-delete changes.
- A resumable offline migration command and runbook.
- Backend unit, contract, and MySQL/Qdrant integration regression coverage.
- A focused memory-recall MySQL regression and fix.
- Production migration evidence and private/group retrieval audit evidence, executed only after explicit authorization for production mutation.
