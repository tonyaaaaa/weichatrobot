# Legacy Visa Knowledge Import Design

## Goal

Convert the supplied legacy MySQL extraction program into a secure, repeatable import tool and use it to load legacy visa products and applicant-material requirements into the production WechatRobot knowledge base under the exact enabled tag `签证知识`. Here, “文档扫描进签证知识” describes the import action; the production tag itself is `签证知识`.

## Scope

The tool imports active legacy visa products and their `visadoc` plus `docsecond` material relationships. Each visa product becomes one Markdown knowledge document. The import uses the existing WechatRobot upload, parsing, preview approval, indexing, and consistency-check contracts instead of writing knowledge tables or Qdrant points directly.

The work does not delete knowledge, create a tag, change group bindings, import customer or applicant personal data, or expose a generic SQL execution facility.

## Security boundaries

The legacy connection string is supplied only through `LEGACY_VISA_CONNECTION_STRING`. It must not appear in source, committed configuration, generated Markdown, logs, test output, audit detail, command output, or the final report. For this one migration, the executor may parse the connection string from the supplied attachment into the child process environment without printing or persisting it.

WechatRobot target configuration comes from `H:\Codex\WechatRobot\.local\.env` and `.local/appsettings.json`. The tool may read the bootstrap administrator identity and password from that environment to authenticate to the local API, but it must never print them. `.local` remains ignored and is not committed.

The supplied attachment contains a plaintext credential. Completion reporting must recommend rotating that legacy credential after the migration.

## Source extraction

The source query is read-only and selects legacy `visa` rows where `IsDel = 0 OR IsDel IS NULL`. For each visa, it reads linked `visadoc` and `docsecond` rows. It preserves these source fields when available:

- Visa ID for internal stable import identity and the joined visa title for knowledge content.
- Country ID for internal lookup and `country.zh_name` joined through `visa.CountryId = country.id` for knowledge content.
- Area rule and visa center.
- Work day and stay day.
- `Price2` is read for source compatibility but intentionally excluded from knowledge content.
- Applicant type code.
- Material name, original type, mandatory flag, extended instructions, template URL, and sample URL.

Applicant type strings split on comma and semicolon. Empty types become `alllist`. Known codes may receive a Chinese display label, but every section retains the original code so an uncertain translation cannot lose source meaning. Duplicate material rows within the same visa and applicant type are collapsed only when every imported field is equal.

## Knowledge document contract

Each product generates one UTF-8 Markdown document named with a stable identity:

```text
legacy-visa-{VisaId}-{sanitized-title}.md
```

The first-level heading is the visa title. A metadata section contains explicit `签证名称` and joined `国家名称`, area rule, visa center, processing time, stay duration, and import timestamp. Non-empty `visa.NoticeDesc` is converted from legacy HTML to plain text under an explicit `注意事项` heading and field so it is present in chunk text and embeddings. Raw visa and country IDs remain internal import keys and never enter knowledge content. A product whose country join is missing is excluded from import and reported with the stable reason `country_relation_missing`; the importer never falls back to the numeric country ID. Legacy price is intentionally excluded. Each material name is emitted both as a heading and as an explicit `材料名称` body field so the existing Markdown chunker retains it in chunk text.

Each applicant type is a second-level section. Materials are rendered as individually headed entries with mandatory status, original type, instructions, template URL, and sample URL. Blank values are omitted rather than replaced with invented facts. Markdown control characters and untrusted HTML are escaped or normalized so legacy content cannot alter the document structure.

## Searchable heading context

The production knowledge pipeline must not rely on `HeadingsJson` alone for Markdown or DOCX structure. Qdrant embeddings are generated from chunk text, so every non-QA chunk with headings receives a deterministic `标题路径：一级标题 > 二级标题 > ...` text prefix before preview persistence and embedding. Structured headings remain unchanged in `HeadingsJson` for display and editing contracts.

The prefix is part of the chunk token budget. Long source blocks are split using the remaining token allowance so every resulting chunk retains the complete heading path without exceeding the configured maximum. Plain-text blocks without headings and QA chunks retain their existing text shape. Existing documents adopt the fix when a new version is parsed and indexed; this import republishes every changed legacy visa document through that normal workflow.

## Idempotency and update behavior

The stable filename and legacy ID form the import identity. Before upload, the tool lists existing documents and matches only an exact stable filename. Zero matches creates a new document. One match uploads to that document ID and creates a new version when content differs. Multiple exact matches stop that item with `ambiguous_existing_document` and do not mutate any match.

The tool computes a SHA-256 content hash before upload. If the current source document has identical content, the item is skipped. It never physically deletes or disables an existing document. A failed update leaves the previously active version available.

## Target pipeline

The tool authenticates against the local API started from `.local`, retrieves tag options, and requires exactly one enabled tag whose name is `签证知识`. Missing, disabled, or ambiguous tags stop the run before the first upload.

For each non-skipped document, the tool performs the existing workflow:

1. Upload the Markdown file, optionally with the matched document ID.
2. Wait for the Worker to finish object-storage and parse stages.
3. Generate previews using the existing smart chunk policy: target 800 tokens, overlap 120 tokens, maximum 1000 tokens.
4. Approve the generated preview revision.
5. Queue indexing with only the exact target tag ID.
6. Poll index status until active and run the existing consistency check.

The API and Worker must use `.local` as working directory and `WECHATROBOT_ENV_FILE` must point to `.local\.env`. The production MySQL, OSS, Qdrant, chat, and embedding configurations remain the authoritative target settings.

## Execution modes and limits

`--dry-run` connects to the source, validates rows, renders documents, checks filenames and hashes, resolves the target tag, and reports the intended actions without uploading or indexing.

Dry-run is the default; mutation requires an explicit `--apply` flag. The tool processes documents sequentially so the existing API limits and durable Worker leases remain authoritative. It writes a local non-secret checkpoint manifest containing legacy ID, source hash, document ID, version ID, and pipeline state. Credentials and upstream bodies are excluded.

Re-running after interruption reads both current API state and the checkpoint. The tool resumes uploaded, approved, indexing, and consistent versions without creating another version. Exact duplicate source documents share the first resolved document/version while retaining a checkpoint entry for each legacy ID. Retry applies to rate-limited or temporarily unavailable reads; upload and index mutations are retried only after checking authoritative state.

## Failure handling

Source connection or schema mismatch stops before production mutation. A single malformed visa row is recorded as a validation failure without fabricating required values. Authentication, tag resolution, or target readiness failure stops before upload.

Once mutation begins, an unrecovered upload, parse, preview, or index failure stops the process with a stable safe code. The completed checkpoint remains valid, and the next run resumes from it. Logs use stable safe error codes and legacy IDs; they do not include connection strings, passwords, API keys, tokens, decrypted model credentials, or raw upstream response bodies.

## Verification

Before applying, the dry run reports product count, material-row count, documents to create, documents to update, identical documents to skip, invalid rows, and estimated output bytes.

After applying, verification checks:

- MySQL has the expected document, version, approved chunks, tag links, and successful index-job state.
- The uploaded source object exists through the system's configured object-storage contract.
- Qdrant consistency reports the expected active generation and point count.
- An online sample workbench and index-status check confirms the active version contains country, material-name, and notice heading-path text and has matching approved-chunk and active-point counts.

The final report lists counts and safe identifiers. It explicitly distinguishes fully indexed, skipped, and failed items and states whether any verification boundary could not be completed.

## Deliverables

- A reusable `tools/WechatRobot.LegacyVisaImport` console project using centrally managed repository dependencies.
- Unit tests for normalization, Markdown rendering, stable naming, applicant-type splitting, deduplication, and secret redaction.
- Production chunking regression tests proving every Markdown chunk retains the full searchable heading path within the maximum token budget.
- Contract tests or a fake HTTP server test for authentication, tag resolution, idempotent create/update decisions, workflow ordering, and failure checkpointing.
- A non-secret execution manifest outside committed source.
- Production knowledge documents indexed under `签证知识` with verified MySQL, object-storage, and Qdrant evidence.
