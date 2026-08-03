# Shared knowledge vector migration

This procedure moves active knowledge vectors from legacy per-version Qdrant collections into deterministic shared collections. It copies existing vectors and never calls the embedding model. MySQL remains the active-version source of truth.

The command runs directly on Windows. Do not start Docker or any other container runtime for this procedure.

## Safety boundaries

- Use `H:\Codex\WechatRobot\.local` as the working directory and configuration source.
- Stop both API and Worker before `--apply`, `--resume`, or `--rollback`. The tool also refuses mutation while an index/reindex job is active.
- `--dry-run` reads MySQL and Qdrant and writes only a local checkpoint.
- `--apply`, `--resume`, and `--rollback` mutate the configured MySQL/Qdrant targets and require explicit production authorization.
- `--verify` reads the switched mappings and destination metadata, then updates checkpoint state only.
- Keep the checkpoint until acceptance and rollback expiry. It contains identifiers and metadata hashes, but no vectors, document text, passwords, API keys, or connection strings.
- The tool does not delete old collections. Never delete them before acceptance, and do not delete a shared collection.

## Prepare

From PowerShell:

```powershell
$repoPath = 'H:\Codex\WechatRobot'
$localPath = Join-Path $repoPath '.local'
$checkpointPath = Join-Path $localPath 'knowledge-vector-migration\checkpoint.json'
$env:WECHATROBOT_ENV_FILE = Join-Path $localPath '.env'
Set-Location -LiteralPath $localPath
```

Confirm no API or Worker command is running. Display only process identity, not complete command lines:

```powershell
Get-CimInstance Win32_Process |
  Where-Object { $_.CommandLine -match 'WechatRobot\.(Api|Worker)' } |
  Select-Object ProcessId, Name
```

Build the command without starting the application:

```powershell
dotnet build ..\tools\WechatRobot.KnowledgeVectorMigration\WechatRobot.KnowledgeVectorMigration.csproj --no-restore
```

## Dry run

The dry run validates every legacy collection contract and active version's point metadata, then atomically writes the checkpoint:

```powershell
dotnet run --no-build --project ..\tools\WechatRobot.KnowledgeVectorMigration\WechatRobot.KnowledgeVectorMigration.csproj -- `
  --dry-run --local-dir $localPath --checkpoint $checkpointPath
```

Record the sanitized summary fields: `versions`, `sources`, `destinations`, `points`, and `mismatches`. Continue only when `state=Planned`, `mismatches=0`, and the counts match the expected active knowledge inventory. A missing model provenance, dimension mismatch, missing source collection, inactive point, wrong document/version ID, or generation mismatch blocks the migration.

Dry-run approval does not authorize `--apply`.

## Apply or resume

After separate production-mutation approval, keep API and Worker stopped and run:

```powershell
dotnet run --no-build --project ..\tools\WechatRobot.KnowledgeVectorMigration\WechatRobot.KnowledgeVectorMigration.csproj -- `
  --apply --local-dir $localPath --checkpoint $checkpointPath
```

The command creates payload indexes, copies at most 256 points per page, compares point count and the exact chunk/document/version/tag/active/generation metadata, and then switches all guarded MySQL mappings in one transaction. It does not recompute embeddings and does not delete source collections.

If the process is interrupted, use the same checkpoint:

```powershell
dotnet run --no-build --project ..\tools\WechatRobot.KnowledgeVectorMigration\WechatRobot.KnowledgeVectorMigration.csproj -- `
  --resume $checkpointPath --local-dir $localPath
```

Successful output is `state=Switched` with `mismatches=0`.

## Verify and accept

Before restarting the runtime, verify the destination vectors and MySQL mappings:

```powershell
dotnet run --no-build --project ..\tools\WechatRobot.KnowledgeVectorMigration\WechatRobot.KnowledgeVectorMigration.csproj -- `
  --verify $checkpointPath --local-dir $localPath
```

Require `state=Accepted` and `mismatches=0`. Start the API from `.local` and verify `/health/live` and authenticated `/api/admin/health/ready`. Start the Worker only when normal background processing is approved and confirm a fresh Worker heartbeat.

Acceptance requires all of the following:

1. Ask one private-chat and one group-chat question whose answer is under the `签证知识` tag.
2. In `会话审计`, inspect both new records and confirm `failureCode` is not `retrieval_unavailable`, the answer source is RAG, and the evidence references the expected knowledge.
3. Check a migrated document with `GET /api/knowledge/documents/{documentId}/index-status?checkConsistency=true` through an authenticated operator session and require a consistent result.
4. Reindex one accepted document through the normal workflow. Confirm it stays in the same shared collection and another document in that collection retains the same consistency result and point count.

## Roll back before cleanup

Rollback is allowed only while every old source version still matches the checkpoint and no source cleanup has begun. Stop API and Worker, obtain explicit rollback authorization, then run:

```powershell
dotnet run --no-build --project ..\tools\WechatRobot.KnowledgeVectorMigration\WechatRobot.KnowledgeVectorMigration.csproj -- `
  --rollback $checkpointPath --local-dir $localPath
```

The tool verifies every source version before transactionally restoring its original MySQL mappings. Destination points remain in place but are not selected by the restored mappings. Require `state=RolledBack`, then repeat runtime health and private/group RAG acceptance.

## Old collection cleanup

Old collection deletion is a separate destructive operation and is not performed by this command. Consider it only after the checkpoint is `Accepted`, the observation window has passed, private/group acceptance succeeds, and rollback is no longer required. Resolve every exact collection name from the checkpoint, confirm it is not a shared collection and not referenced by any active MySQL document, then obtain separate deletion approval. Never use a wildcard, broad prefix, or computed unresolved target.
