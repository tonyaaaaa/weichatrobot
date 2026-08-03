# Legacy Visa Knowledge Import Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix searchable heading context in the production knowledge chunker, build a secure idempotent console importer, then use it with `.local` configuration to migrate active legacy visa products under the existing production tag `签证知识`.

**Architecture:** The production chunker prefixes every non-QA chunk with its structured heading path while accounting for that prefix in the token budget. A .NET 10 tool separately handles legacy MySQL extraction, deterministic Markdown rendering, and authenticated WechatRobot API orchestration, using a non-secret checkpoint under `.local`.

**Tech Stack:** .NET 10 console application, MySql.Data, HttpClient, System.Text.Json, xUnit v3, existing WechatRobot API and Worker.

## Global Constraints

- One UTF-8 Markdown document per active legacy visa product.
- The only target tag is the exact enabled tag `签证知识`; never create or guess a tag.
- Source and target credentials are environment-only and must never be printed, persisted, committed, or included in exception output.
- Use `H:\Codex\WechatRobot\.local\.env` and `.local/appsettings.json` for the production target.
- `--dry-run` performs no target mutation; `--apply` is required for upload or indexing.
- Never delete or disable existing knowledge.
- Existing exact stable filenames are updated only when rendered content differs; identical content is skipped.
- Production mutation is sequential and resumable through the local checkpoint.
- Every headed non-QA chunk contains `标题路径：...`, remains within `MaximumTokens`, and retains the unchanged structured heading metadata.
- Join `visa.CountryId = country.id`, emit `VisaTitle` and `country.zh_name`, and keep both raw IDs out of knowledge content.
- Extract `visa.NoticeDesc`, normalize legacy HTML, and render it under `## 注意事项` with an explicit `注意事项` field.
- Skip and audit products with no joined country name using `country_relation_missing`; never substitute the raw country ID.

---

### Task 0: Production searchable heading context

**Files:**
- Modify: `src/server/WechatRobot.Application/Knowledge/Chunking/ChunkingService.cs`
- Modify: `tests/server/WechatRobot.UnitTests/Knowledge/Chunking/ChunkingServiceTests.cs`
- Test: `tests/server/WechatRobot.UnitTests/Knowledge/Parsing/DocumentParserTests.cs`

**Interfaces:**
- Consumes: `ParsedBlock.Text`, `ParsedBlock.Headings`, and `ChunkPolicy`.
- Produces: each non-QA `ChunkPreview.Text` prefixed with `标题路径：{heading1} > {heading2}` when headings exist; `ChunkPreview.Headings` remains unchanged.

- [ ] **Step 1: Write the failing production regression tests**

Add a direct chunking test with literal expected text and a Markdown parser-to-chunker test. Assert that every chunk from a long headed block begins with the full heading path, the prefix is not duplicated by overlap, plain text stays unchanged, and every `EstimatedTokens` value is at most `MaximumTokens`.

- [ ] **Step 2: Run the focused tests and verify the missing prefix failure**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class WechatRobot.UnitTests.Knowledge.Chunking.ChunkingServiceTests
```

Expected: the new assertions fail because current chunks contain only `ParsedBlock.Text`.

- [ ] **Step 3: Implement token-budgeted heading prefixes**

Build the prefix from nonblank headings, reserve its tokens plus one separator token from the target and maximum limits, split source text with the remaining allowance, and prepend the prefix independently to every produced chunk. Reject a policy where the heading prefix alone leaves no content capacity with a stable argument exception.

- [ ] **Step 4: Run chunking and parsing unit tests**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class WechatRobot.UnitTests.Knowledge.Chunking.ChunkingServiceTests
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore -- --filter-class WechatRobot.UnitTests.Knowledge.Parsing.DocumentParserTests
```

Expected: both focused classes pass and existing QA/plain-text behavior remains unchanged.

---

### Task 1: Deterministic legacy normalization and Markdown rendering

**Files:**
- Create: `tools/WechatRobot.LegacyVisaImport/WechatRobot.LegacyVisaImport.csproj`
- Create: `tools/WechatRobot.LegacyVisaImport/LegacyVisaModels.cs`
- Create: `tools/WechatRobot.LegacyVisaImport/LegacyVisaMarkdownRenderer.cs`
- Create: `tests/server/WechatRobot.LegacyVisaImportTests/WechatRobot.LegacyVisaImportTests.csproj`
- Create: `tests/server/WechatRobot.LegacyVisaImportTests/LegacyVisaMarkdownRendererTests.cs`
- Modify: `WechatRobot.slnx`

**Interfaces:**
- Produces: `LegacyVisaProduct`, `LegacyApplicantMaterialSet`, and `LegacyMaterialRequirement` records.
- Produces: `RenderedVisaDocument LegacyVisaMarkdownRenderer.Render(LegacyVisaProduct product, DateOnly snapshotDate)` where the result contains stable filename, UTF-8 content, and SHA-256 hash.

- [ ] **Step 1: Write failing renderer tests**

Tests assert stable filename `legacy-visa-123-日本三年多次签证.md`, metadata headings, exclusion of legacy price, applicant-code preservation, comma/semicolon splitting, exact-row deduplication, omission of blank optional values, escaping of headings plus HTML, and complete visa/applicant/material context after the actual production parser and chunker run.

- [ ] **Step 2: Run renderer tests and observe failure**

```powershell
dotnet test tests/server/WechatRobot.LegacyVisaImportTests/WechatRobot.LegacyVisaImportTests.csproj -- --filter-class WechatRobot.LegacyVisaImportTests.LegacyVisaMarkdownRendererTests
```

Expected: compilation failure because the renderer types do not exist.

- [ ] **Step 3: Implement minimal models and renderer**

Normalize applicant types with `Split([',', ';'], RemoveEmptyEntries | TrimEntries)`, falling back to `alllist`. Sort applicant types and material entries ordinally for stable hashing. Escape backslash, Markdown heading prefixes, angle brackets, and line breaks in scalar fields. Hash the exact UTF-8 bytes with SHA-256.

- [ ] **Step 4: Re-run renderer tests**

Expected: all renderer tests pass.

### Task 2: Read-only legacy MySQL extraction

**Files:**
- Create: `tools/WechatRobot.LegacyVisaImport/LegacyVisaExtractor.cs`
- Create: `tests/server/WechatRobot.LegacyVisaImportTests/LegacyVisaExtractorTests.cs`

**Interfaces:**
- Produces: `Task<LegacyExtractionResult> LegacyVisaExtractor.ExtractAsync(string connectionString, CancellationToken token)`.
- `LegacyExtractionResult` contains products, source product count, material-row count, and safe validation failures.

- [ ] **Step 1: Write failing row-normalization tests**

Use in-memory row objects rather than a real credential. Assert null handling, `IsNeed` conversion, multi-applicant expansion, deterministic material grouping, and validation failure for missing visa ID or title.

- [ ] **Step 2: Run extractor tests and observe failure**

```powershell
dotnet test tests/server/WechatRobot.LegacyVisaImportTests/WechatRobot.LegacyVisaImportTests.csproj -- --filter-class WechatRobot.LegacyVisaImportTests.LegacyVisaExtractorTests
```

Expected: compilation failure because extraction and normalization are missing.

- [ ] **Step 3: Implement bounded read-only queries**

Open one MySQL connection using the supplied environment value. Query active visa rows ordered by ID, then query all linked material rows in one ordered join and group them in memory, avoiding one query per product. Select only the fields named in the design and never log the connection string or server error body.

- [ ] **Step 4: Re-run extractor tests**

Expected: all extractor tests pass.

### Task 3: Authenticated target API workflow and checkpointing

**Files:**
- Create: `tools/WechatRobot.LegacyVisaImport/WechatRobotKnowledgeClient.cs`
- Create: `tools/WechatRobot.LegacyVisaImport/ImportCheckpointStore.cs`
- Create: `tools/WechatRobot.LegacyVisaImport/LegacyVisaImportOrchestrator.cs`
- Create: `tests/server/WechatRobot.LegacyVisaImportTests/LegacyVisaImportOrchestratorTests.cs`

**Interfaces:**
- Produces: `WechatRobotKnowledgeClient.LoginAsync`, `ResolveExactTagAsync`, `ListExistingAsync`, `UploadAsync`, `WaitForWorkbenchAsync`, `GenerateAndApprovePreviewsAsync`, `QueueIndexAsync`, and `WaitForActiveConsistencyAsync`.
- Produces: `ImportCheckpointStore` reading and atomically replacing a JSON manifest with only safe IDs, hashes, states, and error codes.
- Produces: `LegacyVisaImportOrchestrator.PlanAsync` and `ApplyAsync`.

- [ ] **Step 1: Write failing HTTP workflow tests**

Use a fake `HttpMessageHandler`. Assert bearer authentication, exact enabled-tag resolution, refusal of zero or multiple tag matches, create versus update versus identical-skip decisions, upload before preview before index ordering, non-retry of ambiguous mutations, and redacted checkpoint fields.

- [ ] **Step 2: Run workflow tests and observe failure**

```powershell
dotnet test tests/server/WechatRobot.LegacyVisaImportTests/WechatRobot.LegacyVisaImportTests.csproj -- --filter-class WechatRobot.LegacyVisaImportTests.LegacyVisaImportOrchestratorTests
```

Expected: compilation failure because the client, checkpoint, and orchestrator do not exist.

- [ ] **Step 3: Implement the minimal API client and orchestrator**

Use the current endpoint contracts: `/api/auth/login`, `/api/knowledge/tags/options`, `/api/knowledge/documents`, workbench, preview generate/approve, version index, and document index-status. Parse only required response fields. Sanitize all failure messages to stable codes. Poll with cancellation and a bounded timeout.

- [ ] **Step 4: Re-run workflow tests**

Expected: all workflow tests pass.

### Task 4: CLI, configuration safety, and dry-run

**Files:**
- Create: `tools/WechatRobot.LegacyVisaImport/Program.cs`
- Create: `tools/WechatRobot.LegacyVisaImport/LegacyVisaImportOptions.cs`
- Create: `tests/server/WechatRobot.LegacyVisaImportTests/LegacyVisaImportOptionsTests.cs`

**Interfaces:**
- CLI arguments: optional `--apply`, `--local-dir`, and `--base-url`; omitting `--apply` is dry-run mode.
- Required environment: `LEGACY_VISA_CONNECTION_STRING`, `BootstrapAdmin__Email`, and `BootstrapAdmin__Password`.

- [ ] **Step 1: Write failing option and secret-safety tests**

Assert dry-run/apply behavior, checkpoint state under `.local`, absent required variables returning stable errors, and diagnostic text never containing supplied credential fragments.

- [ ] **Step 2: Run CLI tests and observe failure**

```powershell
dotnet test tests/server/WechatRobot.LegacyVisaImportTests/WechatRobot.LegacyVisaImportTests.csproj -- --filter-class WechatRobot.LegacyVisaImportTests.LegacyVisaImportOptionsTests
```

Expected: compilation failure because CLI options do not exist.

- [ ] **Step 3: Implement CLI composition**

Set `WECHATROBOT_ENV_FILE` to the selected local directory's `.env`, parse safe options, extract and render source rows, authenticate, resolve the tag, and print only aggregate counts plus safe IDs. Apply writes checkpoint entries and exits nonzero on an unrecovered failure.

- [ ] **Step 4: Run the complete importer test project and build**

```powershell
dotnet test tests/server/WechatRobot.LegacyVisaImportTests/WechatRobot.LegacyVisaImportTests.csproj
dotnet build tools/WechatRobot.LegacyVisaImport/WechatRobot.LegacyVisaImport.csproj -c Release
git diff --check
```

Expected: tests and build pass with no secret-bearing output.

### Task 5: Production preflight, dry-run, apply, and verification

**Files:**
- Local generated only: `.local/legacy-visa-import/checkpoint.json`
- Local generated only: `.local/legacy-visa-import/rendered/*.md`
- No committed production data files.

**Interfaces:**
- Consumes the tool and `.local` runtime configuration from Tasks 1-4.
- Produces production document/version/index IDs and a safe aggregate report.

- [ ] **Step 1: Start fresh local API and Worker against `.local`**

Set `WECHATROBOT_ENV_FILE` to the absolute `.local\.env`, use `.local` as working directory, start rebuilt API and Worker, and verify liveness, authenticated readiness, and fresh Worker heartbeat. Do not start a second Worker if an existing fresh local Worker is already using the production queue.

- [ ] **Step 2: Resolve target tag and source inventory without mutation**

Extract the legacy connection string from the supplied attachment into the child process environment without printing it. Run without `--apply`. Record aggregate product/document action counts and stop if the exact target tag is not uniquely enabled.

- [ ] **Step 3: Inspect rendered samples and apply**

Inspect rendered documents covering the known applicant-type combinations and confirm no secret or malformed Markdown. Run `--apply`. Persist only the safe checkpoint.

- [ ] **Step 4: Verify three storage boundaries and sample retrieval**

Use authenticated workbench and index-status APIs plus the full checkpoint to verify expected active documents, approved chunks, successful index jobs, and Qdrant consistency. Verify one representative Japan three-year visa document contains country, material-name, and notice heading-path text.

- [ ] **Step 5: Final verification and commit**

Run the importer tests, relevant server tests, tool Release build, and `git diff --check`. Review the diff for credentials. Commit only reusable tool, tests, plan, and documentation with:

```powershell
git commit -m "feat: import legacy visa knowledge"
```

Report successful, skipped, failed, and verified counts, safe IDs, remaining blockers, and the legacy credential-rotation requirement. Do not push unless explicitly requested.
