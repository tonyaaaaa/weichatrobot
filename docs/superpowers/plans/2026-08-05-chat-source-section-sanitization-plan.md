# Chat Source Section Sanitization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep all RAG and Web Search source information out of private and group replies while preserving a valid RAG business answer when the model appends a trailing source section.

**Architecture:** Preserve the existing strict output firewall and source-free grounded prompt. Add one bounded grounded-output sanitizer that removes only a trailing source section beginning on its own line, then validate the remaining answer normally. Sources remain unchanged in retrieval audit.

**Tech Stack:** .NET 10, ASP.NET Core application services, xUnit v3/Microsoft Testing Platform.

## Global Constraints

- Work directly on the current `master` checkout.
- Private and group replies must not display RAG or Web Search source documents, filenames, identifiers, URLs, page markers, or citations.
- Sources remain available in retrieval audit.
- Do not change MySQL schema, migrations, Qdrant data, API contracts, or frontend code.
- Do not commit, push, publish, or restart services without explicit authorization.

---

### Task 1: Reproduce the trailing RAG source leak

**Files:**

- Modify: `tests/server/WechatRobot.UnitTests/Conversations/AnswerOutputFirewallTests.cs`
- Modify: `tests/server/WechatRobot.UnitTests/Conversations/GroundedAnswerTests.cs`

- [ ] Add a failing firewall test for `办理材料如下。\n来源：legacy-visa.md` that expects the sanitizer to return only `办理材料如下。`.
- [ ] Add a failing service test proving a strong RAG hit returns `Answer`, keeps evidence in audit, and does not expose `来源` or `legacy-visa.md`.
- [ ] Add an empty-after-sanitization test proving a source-only completion remains unsafe.
- [ ] Run the focused tests and confirm they fail because the sanitizer does not exist or the source line remains visible.

### Task 2: Implement bounded grounded source sanitization

**Files:**

- Modify: `src/server/WechatRobot.Application/Conversations/AnswerOutputFirewall.cs`
- Modify: `src/server/WechatRobot.Application/Conversations/GroundedAnswerService.cs`
- Restore: `src/server/WechatRobot.Infrastructure/Agents/KnowledgeEvidenceProvider.cs`
- Remove: `src/server/WechatRobot.Application/Conversations/RetrievalEvidencePromptFormatter.cs`

- [ ] Restore strict grounded validation for generic source markers, evidence filenames, URIs, and identifiers.
- [ ] Add `SanitizeGrounded(string)` that removes only a trailing source section beginning on an independent source-heading line.
- [ ] Sanitize before grounded validation; validate and send only the sanitized result.
- [ ] Restore the grounded prompt prohibition on citations, source markers, filenames, internal IDs, and page markers.
- [ ] Keep Agent Framework evidence context limited to evidence content and retain delimiter escaping.
- [ ] Run focused tests and confirm GREEN.

### Task 3: Verify the final boundary

**Files:**

- Review all Task 1 and Task 2 files plus the approved specification.

- [ ] Run all `AnswerOutputFirewallTests`, `GroundedAnswerTests`, and `KnowledgeEvidenceProviderTests`.
- [ ] Run the complete backend unit-test project.
- [ ] Run `dotnet build WechatRobot.slnx -c Release`.
- [ ] Run `git diff --check` and confirm no database, Qdrant, API, frontend, `.local`, or secret-bearing files changed.

### Task 4: Apply a shared visa customer-service tone

**Files:**

- Modify: `src/server/WechatRobot.Application/Conversations/GroundedAnswerService.cs`
- Modify: `tests/server/WechatRobot.UnitTests/Conversations/GroundedAnswerTests.cs`

- [ ] Add failing tests proving grounded RAG, Web Search, and model-knowledge prompts all require a professional visa customer-service tone.
- [ ] Require natural Chinese for Chinese questions, direct polite address, and concise follow-up questions for missing visa type, jurisdiction, occupation, age, or applicant category.
- [ ] Explicitly prohibit internal-system framing and empty “consult further” deflection.
- [ ] Keep evidence, source privacy, threshold, audit, fixed-template, and fallback behavior unchanged.
- [ ] Run focused prompt tests, all `GroundedAnswerTests`, the complete unit-test project, and Release builds for API and Worker.
