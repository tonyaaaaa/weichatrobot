# Knowledge Document Physical Deletion Design

## Problem

The physical-delete API currently marks a knowledge document as delete
requested and queues a `CleanupKnowledgeDocument` durable job. The Worker
removes the document's object-storage files and Qdrant vectors, verifies vector
cleanup, and then marks the job completed.

The Worker does not delete the MySQL `knowledge_document` or
`knowledge_document_version` rows. As a result:

- the deleted document remains visible as a tombstone;
- the unique SHA-256 row remains in `knowledge_document_version`;
- uploading the same file again is rejected as a duplicate even though the
  external file and vectors have already been removed.

## Decision

After external cleanup has succeeded and been verified, the Worker will
physically delete the document root from MySQL.

Before deleting the root, it will set
`knowledge_candidate.KnowledgeDocumentVersionId` to `NULL` for candidates that
reference any version of the document. This preserves human-answer and review
history while satisfying the existing restrictive foreign key.

Deleting the document root will use the existing cascade relationships to
remove:

- document versions;
- chunks and chunk-tag links;
- chunk previews;
- OCR pages;
- knowledge index jobs.

The cleanup durable-job row and administrative audit history are retained.
This change does not require a schema migration.

## Processing Order

For each leased `CleanupKnowledgeDocument` job, the Worker will:

1. Read the document's object keys and vector contracts.
2. Wait for active index work to drain using the existing deadline behavior.
3. Delete all object-storage objects.
4. Delete all document vector generations.
5. Re-read the vector contracts and repeat deletion to cover races.
6. Verify that no targeted vector collection or version remains.
7. In one MySQL transaction:
   - clear candidate references to the document's versions;
   - delete the document root and its cascading dependents.
8. Mark the durable job completed.

The MySQL records must not be removed before external cleanup verification.

## Failure and Retry Behavior

- Object-storage, Qdrant, verification, or MySQL failures use the existing
  durable-job failure and retry path.
- A failure before the MySQL transaction leaves the document tombstone and
  version hashes in place, preventing reuse while cleanup is incomplete.
- External delete operations remain safe to repeat during retries.
- If the MySQL document is already absent on a retry, the Worker treats the
  database cleanup as complete and proceeds to complete the durable job.
- The job is never marked completed while a known database deletion failure
  remains.

## Upload Behavior

Once the cleanup job completes, the old version row and its unique SHA-256
value no longer exist. A later upload of identical content follows the normal
new-document upload path.

Uploads attempted while physical cleanup is pending or retrying continue to be
rejected as duplicates. This prevents two active records from claiming the
same content during an incomplete deletion.

## Verification

Regression coverage will prove:

- successful cleanup removes object-storage objects and Qdrant vectors;
- successful cleanup removes the document, versions, chunks, previews, OCR
  pages, chunk-tag links, and index jobs from MySQL;
- linked knowledge candidates and reviews remain, with the version reference
  cleared;
- the cleanup durable job reaches `completed`;
- the same file can be uploaded again after cleanup completes;
- an external cleanup or verification failure retains the MySQL document and
  version rows and fails the durable job for retry;
- repeated cleanup processing is idempotent.

Focused Worker tests will cover orchestration and failure ordering. A real
MySQL integration test will cover the restrictive candidate relationship,
cascades, SHA-256 uniqueness release, and same-content re-upload.

## Non-Goals

- Changing the duplicate-file rule for active or delete-pending documents.
- Removing durable-job or administrative audit history.
- Changing the physical-delete HTTP response contract.
- Adding a new database schema or migration.
