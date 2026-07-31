# Private Knowledge Agent Duplicate Routing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ensure semantic duplicates identified by the private-knowledge Agent are reported as duplicates and never published as new knowledge.

**Architecture:** The Agent remains the semantic decision maker and uses RAG similarity results to return `Duplicate` plus the matched active version ID. `PrivateKnowledgeIngestProcessor` validates that ID against current active knowledge and executes the decision; deterministic exact question-and-answer matching remains only a fast path.

**Tech Stack:** ASP.NET Core 10, .NET 10, Microsoft Agents AI, EF Core InMemory integration tests, xUnit v3/Microsoft Testing Platform

## Global Constraints

- Semantic duplicate decisions belong to `PrivateKnowledgeProposalAgent`.
- Same question with a different answer is a duplicate unless the Agent explicitly identifies a factual supplement or correction.
- Differently worded questions with the same meaning are duplicates.
- `Duplicate` must reference a currently active and published version.
- Duplicate proposals create no document, version, chunk, or index job.
- Do not change appsettings, environment variables, or the database schema.
- Do not mutate or remove historical duplicate documents.

---

### Task 1: Preserve Agent Duplicate Decisions in the Ingest Processor

**Files:**
- Modify: `tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateKnowledgeIngestPipelineTests.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Agents/PrivateKnowledgeIngestProcessor.cs`

**Interfaces:**
- Consumes: `ProposedKnowledgeItem.SimilarVersionId` and `ProposedKnowledgeItem.ChangeKind`
- Produces: validated `Target? ResolveTargetAsync(ProposedKnowledgeItem, CancellationToken)` for `Duplicate`, `Supplement`, and `Correction`

- [ ] **Step 1: Write the failing duplicate-routing integration test**

Seed one active document, published version, approved chunk, and tag. Make the proposal Agent return a semantically equivalent reworded question, a different answer, `KnowledgeChangeKind.Duplicate`, and the seeded version ID. Assert:

```csharp
Assert.Equal("Activated", batch.Status);
Assert.Equal(0, batch.NewCount);
Assert.Equal(1, batch.DuplicateCount);
Assert.Equal("Duplicate", item.ChangeKind);
Assert.Equal(existingDocument.Id, item.MatchedDocumentId);
Assert.Equal(existingVersion.Id, item.MatchedVersionId);
Assert.Null(item.StagedDocumentId);
Assert.Null(item.StagedVersionId);
Assert.Single(await database.KnowledgeDocuments.AsNoTracking().ToArrayAsync(token));
Assert.Single(await database.KnowledgeDocumentVersions.AsNoTracking().ToArrayAsync(token));
Assert.Empty(await database.KnowledgeIndexJobs.AsNoTracking().ToArrayAsync(token));
```

The production mutation this test catches is restricting `ResolveTargetAsync` to `Supplement` and `Correction`, which silently changes a valid Agent `Duplicate` into `New`.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~PrivateKnowledgeIngestPipelineTests.Processor_honors_agent_semantic_duplicate_without_publishing_new_knowledge"
```

Expected: FAIL because the batch reports `NewCount = 1`, stages a second document/version, and creates an index job.

- [ ] **Step 3: Implement the minimal validated duplicate branch**

Allow `ResolveTargetAsync` to validate targets for all non-`New` decisions:

```csharp
if (proposal.ChangeKind == KnowledgeChangeKind.New
    || proposal.SimilarVersionId is not { } versionId)
{
    return null;
}
```

Immediately after resolving the target, execute a valid duplicate without staging:

```csharp
if (proposal.ChangeKind == KnowledgeChangeKind.Duplicate
    && target is not null)
{
    item.ChangeKind = KnowledgeChangeKind.Duplicate.ToString();
    item.MatchedDocumentId = target.DocumentId;
    item.MatchedVersionId = target.VersionId;
    item.ResolvedTagIdsJson = JsonSerializer.Serialize(
        await ResolveTagsAsync(proposal, target.VersionId, globalTagId, now, cancellationToken));
    continue;
}
```

An invalid or missing target continues through the existing `New` fallback.

- [ ] **Step 4: Run the focused test and verify GREEN**

Run the same filtered `dotnet test` command. Expected: PASS.

- [ ] **Step 5: Add and run the invalid-target regression**

Add a test where Agent returns `Duplicate` with an unknown or inactive version ID. Assert it does not bind that target and follows the documented `New` fallback. Run both duplicate-routing tests and expect PASS.

### Task 2: Make the Agent’s Semantic Duplicate Rule Explicit

**Files:**
- Modify: `src/server/WechatRobot.Infrastructure/Agents/PrivateKnowledgeProposalAgent.cs`
- Modify: `tests/server/WechatRobot.UnitTests/PrivateChat/PrivateKnowledgeProposalAgentTests.cs`

**Interfaces:**
- Consumes: Agent tool `find_similar_knowledge(question)`
- Produces: `ProposedKnowledgeItem` with `ChangeKind = Duplicate` and a retrieved `SimilarVersionId`

- [ ] **Step 1: Write the failing Agent semantic-guidance tool-flow test**

Use a fake retrieval provider returning one active similar result. Use a
deterministic fake chat client that inspects the real Agent system instruction:
it returns an invalid empty proposal when semantic duplicate guidance is absent;
when the guidance is present, it first invokes `find_similar_knowledge`, then
submits:

```csharp
new
{
    question = "加拿大旅游签证补件通知应该怎么处理？",
    answer = "按通知要求在期限内补交。",
    explicitTags = Array.Empty<string>(),
    suggestedTagId = (Guid?)null,
    similarVersionId = existingVersionId,
    changeKind = "Duplicate"
}
```

Assert the Agent queried retrieval with the reworded question and preserved both
`Duplicate` and `SimilarVersionId`. The production mutation this catches is
removing the semantic duplicate rule from the Agent instruction, removing
similarity retrieval from the Agent flow, or dropping the matched version during
proposal conversion.

- [ ] **Step 2: Run the focused Agent test and verify RED**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~PrivateKnowledgeProposalAgentTests.Agent_uses_similarity_result_for_semantic_duplicate"
```

Expected: FAIL with `private_knowledge_agent_invalid_output` because the current
instruction does not tell the Agent that same-meaning questions and same-question
different-answer items are duplicates.

- [ ] **Step 3: Clarify the Agent instruction**

Update the Agent instruction to require:

```text
Before deciding ChangeKind, call find_similar_knowledge for every proposed question.
Judge duplicates by question meaning, not exact wording or answer text.
Use Duplicate when the same question already exists even if the new answer differs,
or when wording differs but the question has the same meaning.
Use Supplement or Correction only when the source contains a genuine factual addition
or correction. Duplicate, Supplement, and Correction must include the matched
SimilarVersionId returned by find_similar_knowledge.
```

Do not add a second server-side similarity threshold.

- [ ] **Step 4: Run the focused Agent test and verify GREEN**

Run the same filtered unit-test command. Expected: PASS.

### Task 3: Verify the Complete Private-Knowledge Boundary

**Files:**
- Verify: `src/server/WechatRobot.Infrastructure/Agents/PrivateKnowledgeProposalAgent.cs`
- Verify: `src/server/WechatRobot.Infrastructure/Agents/PrivateKnowledgeIngestProcessor.cs`
- Verify: `tests/server/WechatRobot.UnitTests/PrivateChat/PrivateKnowledgeProposalAgentTests.cs`
- Verify: `tests/server/WechatRobot.IntegrationTests/PrivateChat/PrivateKnowledgeIngestPipelineTests.cs`

**Interfaces:**
- Consumes: completed Tasks 1 and 2
- Produces: verified source and release-ready binaries

- [ ] **Step 1: Run all private-knowledge focused tests**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~PrivateKnowledge"
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~PrivateKnowledgeIngestPipelineTests"
```

Expected: all tests PASS.

- [ ] **Step 2: Run relevant complete backend suites**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj
```

Expected: all tests PASS, or unrelated baseline failures are recorded separately with evidence.

- [ ] **Step 3: Build release binaries and check the diff**

```powershell
dotnet build WechatRobot.slnx --configuration Release --no-restore
git diff --check
git status --short
```

Expected: build succeeds, diff check is clean, and only task files are modified.

- [ ] **Step 4: Review the completion requirements**

Confirm from tests and the source diff that:

- Agent semantic `Duplicate` survives persistence and staging.
- The matched active document/version is recorded.
- No duplicate document/version/chunk/index job is created.
- Batch counts and final notification report duplicate instead of new.
- Invalid targets do not bind inactive knowledge.
- No configuration, migration, historical data, or secret-bearing file changed.

- [ ] **Step 5: Commit implementation and generate the Windows IIS package**

Stage only the four implementation/test files and commit with:

```powershell
git commit -m "fix: honor agent private knowledge duplicates"
```

Then use the repository’s existing Windows IIS packaging procedure to generate a fresh package under `.local/packages`, without committing local package output.
