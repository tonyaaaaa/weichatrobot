# Conversation Latency and Output Safety Implementation Plan

**Goal:** Reduce private/group reply latency across the full callback-to-send path and prevent internal model tool calls from reaching users.

**Architecture:** Split durable message work into independent lanes, tune official Z.AI GLM requests for non-thinking execution, skip query rewriting only when formal context is empty, reuse identical request-scoped embeddings, repair the MySQL memory predicate, and strengthen the common model-output firewall.

**Tech Stack:** ASP.NET Core 10, .NET 10 Worker, Microsoft Agents AI, EF Core/MySQL, Qdrant, xUnit v3/Microsoft Testing Platform

## Constraints

- Preserve Agent Framework modes and existing appsettings values.
- Preserve contextual second-turn query rewriting and conversation ordering.
- Do not add a migration or expose `.local` secrets.
- Use RED/GREEN tests for every behavior change.

### Task 1: Lock the output-safety regression

**Files:**
- Modify: `tests/server/WechatRobot.UnitTests/Conversations/AnswerOutputFirewallTests.cs`
- Modify: `src/server/WechatRobot.Application/Conversations/AnswerOutputFirewall.cs`
- Verify: `tests/server/WechatRobot.UnitTests/Conversations/GroundedAnswerTests.cs`

- [x] Add the user-provided `<|tool_call|>` web-search response as a failing test.
- [x] Cover tool response/function-call variants, markerless web-search JSON and clean answer controls.
- [x] Implement one shared internal-protocol detector used by grounded and ungrounded validation.
- [x] Verify unsafe web/model fallback output becomes a safe terminal response.

### Task 2: Remove avoidable model latency

**Files:**
- Modify: `tests/server/WechatRobot.UnitTests/Conversations/MultiTurnRetrievalServiceTests.cs`
- Modify: `src/server/WechatRobot.Application/Conversations/MultiTurnRetrievalService.cs`
- Modify: Agent implementations under `src/server/WechatRobot.Infrastructure/Agents`

- [x] Add a failing test that an empty formal context never invokes the rewrite Agent.
- [x] Implement original-question fast path with complete audit evidence.
- [x] Assert a contextual follow-up still invokes the Agent exactly once.
- [x] Add bounded `MaxOutputTokens` to intent, template-routing, rewrite and answer Agent calls.

### Task 3: Tune official Z.AI GLM request serialization

**Files:**
- Add/modify: request tuning helper/handler under `src/server/WechatRobot.Infrastructure/Agents` or `Models`
- Modify: `OpenAiCompatibleAgentChatClientFactory.cs`
- Modify: `OpenAiCompatibleChatClient.cs`
- Add tests under `tests/server/WechatRobot.UnitTests/Models` or `Agents`

- [x] Add RED serialization tests for official Z.AI GLM and non-target OpenAI-compatible endpoints.
- [x] Inject `thinking.type=disabled` and `max_tokens=2048` only for supported official Z.AI GLM requests.
- [x] Preserve authorization redaction/removal and content headers.
- [x] Verify Agent Framework and legacy/web-search transports both apply the rule.

### Task 4: Remove queue head-of-line blocking

**Files:**
- Modify: `src/server/WechatRobot.Worker/Jobs/DurableJobWorker.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs`
- Modify: `src/server/WechatRobot.Application/Jobs/IDurableJobRepository.cs`
- Add/modify worker tests

- [x] Add tests for the lane plan and leased-job creation timestamp.
- [x] Run group work on four lanes, private replies on one lane, and private ingest on one lane.
- [x] Preserve `ProcessOnceAsync` compatibility and per-session ordering, without counting busy sessions as failures.
- [x] Renew long-running job leases and recover each lane after transient repository failures.
- [x] Add sanitized queue-age and processing-duration structured logs.

### Task 5: Reuse embeddings and repair memory recall

**Files:**
- Add: scoped embedding cache under Application/Infrastructure as appropriate
- Modify: `MemoryRecallService.cs`
- Modify: `KnowledgeRetrievalEvidenceProvider.cs`
- Add unit and real MySQL integration tests

- [x] Add RED test proving identical scoped embedding requests currently call upstream twice.
- [x] Implement request-scoped, configuration-aware caching.
- [x] Add a real-MySQL test for the active-memory GUID predicate.
- [x] Replace `Contains` with `GuidBatchQuery` and verify actual recalled memory.

### Task 6: Verify runtime improvement

- [x] Run focused unit and integration tests after every RED/GREEN cycle.
- [x] Run backend unit, contract and integration suites; report unrelated baselines separately.
- [x] Build `WechatRobot.slnx` in Release and run `git diff --check`.
- [ ] Start API, Worker and frontend from `.local` using `.local/.env`.
- [ ] Verify liveness, readiness, Worker heartbeat and frontend HTTP 200.
- [ ] Exercise private/group first turns and contextual second turns with the current model.
- [ ] Compare model calls, Embedding calls, queue age and end-to-end latency against the production baseline.
- [x] Review the diff for secrets and unrelated changes, then commit only this task's files.
