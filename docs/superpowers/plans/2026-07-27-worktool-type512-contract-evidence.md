# WorkTool Type 512 Contract Evidence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Verify the real WorkTool `type=512` group-member nickname result contract without exposing names or inventing fields, then produce the evidence needed for a separate exact member-snapshot implementation plan.

**Architecture:** Add only the two operations that official WorkTool documentation proves: submit a `type=512` raw command and query raw command results. Keep the undocumented result portions as raw JSON strings, and use a development-only evidence runner to capture one real response into a gitignored local directory. No production parser, member table, or group-agent enablement is allowed until that evidence has been reviewed.

**Tech Stack:** .NET 10, `IHttpClientFactory`, `System.Text.Json`, xUnit v3, PowerShell 7, WorkTool HTTP API.

## Verified Contract Boundary

- Official request: `POST /wework/sendRawMessage?robotId={robotId}` with `socketType=2` and one list item containing only `type=512` and `groupName`.
- Official result query: `GET /robot/rawMsg/list` with `robotId`, `page`, `size`, `sort`, `desc`, `startTime`, `endTime`, `type`, and `messageId`.
- The documented result row contains `rawMsg`, `rawSuccess`, `errorReason`, `runTime`, `apiSend`, `robotId`, `type`, `messageId`, `successList`, `failList`, and `timeCost`.
- The official documentation does not identify which `type=512` field contains nicknames or provide a `type=512` result example.
- Therefore this plan must not define a nickname DTO, infer member identity, or persist a member snapshot.
- Raw evidence may contain personal data. It stays under `.local/worktool-type512-evidence/`, is never logged, and is never committed.
- Plan `2026-07-27-worktool-official-contract-hardening.md` must be completed first so all probe traffic uses the shared 60 QPM limiter.
- Preserve unrelated dirty-worktree changes.

---

### Task 1: Add the Exact Type 512 Submission Contract

**Files:**
- Modify: `src/server/WechatRobot.Application/WorkTool/IWorkToolClient.cs`
- Modify: `src/server/WechatRobot.Infrastructure/WorkTool/WorkToolClient.cs`
- Modify: `tests/server/WechatRobot.ContractTests/WorkTool/SendRawMessageContractTests.cs`

**Interfaces:**

```csharp
Task<WorkToolCommandSubmission> RequestGroupMemberSnapshotAsync(
    Guid robotConfigId,
    string groupName,
    CancellationToken cancellationToken);
```

- [ ] **Step 1: Write the failing request contract test**

Add this test to `SendRawMessageContractTests`:

```csharp
[Fact]
public async Task RequestGroupMemberSnapshotAsync_sends_only_the_official_type512_fields()
{
    var handler = new RecordingHandler(
        HttpStatusCode.OK,
        """{"code":0,"message":"accepted","data":"member-command-1"}""");
    using var client = new HttpClient(handler)
    {
        BaseAddress = new Uri("https://fake.worktool.test/")
    };
    var sut = new WorkToolClient(client, new FixedCredentials());

    var result = await sut.RequestGroupMemberSnapshotAsync(
        Guid.NewGuid(),
        "售后客户群",
        TestContext.Current.CancellationToken);

    Assert.True(result.Accepted);
    Assert.Equal("member-command-1", result.MessageId);
    Assert.Equal("/wework/sendRawMessage?robotId=robot-7", handler.PathAndQuery);

    using var json = JsonDocument.Parse(handler.Body);
    Assert.Equal(2, json.RootElement.GetProperty("socketType").GetInt32());
    var list = json.RootElement.GetProperty("list");
    Assert.Equal(1, list.GetArrayLength());
    var command = list[0];
    Assert.Equal(2, command.EnumerateObject().Count());
    Assert.Equal(512, command.GetProperty("type").GetInt32());
    Assert.Equal("售后客户群", command.GetProperty("groupName").GetString());
}
```

- [ ] **Step 2: Run the contract test and confirm it fails**

Run:

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --filter FullyQualifiedName~RequestGroupMemberSnapshotAsync_sends_only_the_official_type512_fields
```

Expected: FAIL because `IWorkToolClient` and `WorkToolClient` do not yet expose the method.

- [ ] **Step 3: Implement the request without adding undocumented fields**

Add the interface method and implement it through the existing `SendCommandAsync` path:

```csharp
public async Task<WorkToolCommandSubmission> RequestGroupMemberSnapshotAsync(
    Guid robotConfigId,
    string groupName,
    CancellationToken cancellationToken)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
    var robotId = await credentials.ResolveEnabledRobotIdAsync(
        robotConfigId,
        cancellationToken);

    return await SendCommandAsync(
        robotId,
        new
        {
            type = 512,
            groupName
        },
        cancellationToken);
}
```

Do not add `titleList`, `nameList`, `atList`, pagination, or callback fields.

- [ ] **Step 4: Run the focused contract tests**

Run:

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --filter FullyQualifiedName~SendRawMessageContractTests
```

Expected: PASS.

- [ ] **Step 5: Commit Task 1**

```powershell
git add src/server/WechatRobot.Application/WorkTool/IWorkToolClient.cs src/server/WechatRobot.Infrastructure/WorkTool/WorkToolClient.cs tests/server/WechatRobot.ContractTests/WorkTool/SendRawMessageContractTests.cs
git commit -m "feat: add official WorkTool type 512 request"
```

---

### Task 2: Add the Documented Raw Result Query Without Nickname Parsing

**Files:**
- Create: `src/server/WechatRobot.Application/WorkTool/WorkToolRawCommandResult.cs`
- Modify: `src/server/WechatRobot.Application/WorkTool/IWorkToolClient.cs`
- Modify: `src/server/WechatRobot.Infrastructure/WorkTool/WorkToolClient.cs`
- Create: `tests/server/WechatRobot.ContractTests/WorkTool/RawCommandResultQueryContractTests.cs`

**Interfaces:**

```csharp
public sealed record WorkToolRawCommandResult(
    string? RawMessage,
    int RawSuccess,
    string? ErrorReason,
    string? RunTimeRaw,
    int ApiSend,
    int Type,
    string MessageId,
    string? SuccessListRaw,
    string? FailListRaw,
    long? TimeCost);

Task<IReadOnlyList<WorkToolRawCommandResult>> ListGroupMemberSnapshotResultsAsync(
    Guid robotConfigId,
    string messageId,
    DateTimeOffset startTime,
    DateTimeOffset endTime,
    CancellationToken cancellationToken);
```

- [ ] **Step 1: Write the failing exact-query and response test**

Create a test that returns this documented envelope:

```json
{
  "code": 200,
  "message": "success",
  "data": [
    {
      "rawMsg": "{\"socketType\":2,\"list\":[{\"type\":512,\"groupName\":\"售后客户群\"}]}",
      "rawSuccess": 1,
      "errorReason": "",
      "runTime": "2026-07-27 12:00:00",
      "apiSend": 1,
      "robotId": "must-not-be-returned",
      "type": 512,
      "messageId": "member-command-1",
      "successList": "[\"opaque\"]",
      "failList": "[]",
      "timeCost": 345
    }
  ]
}
```

Assert:

```csharp
Assert.Equal(HttpMethod.Get, handler.Method);
Assert.Contains("robot/rawMsg/list?", handler.PathAndQuery);
Assert.Contains("robotId=robot-7", handler.PathAndQuery);
Assert.Contains("page=1", handler.PathAndQuery);
Assert.Contains("size=20", handler.PathAndQuery);
Assert.Contains("sort=run_time", handler.PathAndQuery);
Assert.Contains("desc=true", handler.PathAndQuery);
Assert.Contains("type=512", handler.PathAndQuery);
Assert.Contains("messageId=member-command-1", handler.PathAndQuery);
Assert.Single(results);
Assert.Equal(512, results[0].Type);
Assert.Equal("member-command-1", results[0].MessageId);
Assert.Equal("[\"opaque\"]", results[0].SuccessListRaw);
Assert.DoesNotContain("robot-7", JsonSerializer.Serialize(results[0]));
```

Also add tests that:

- reject a blank `messageId`;
- return an empty list for documented `data: []`;
- return a typed failure for non-2xx, invalid JSON, or a WorkTool code other than `200`;
- URL-encode `messageId` and use UTC timestamps formatted as `yyyy-MM-dd HH:mm:ss`.

- [ ] **Step 2: Run the new contract test and confirm it fails**

Run:

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --filter FullyQualifiedName~RawCommandResultQueryContractTests
```

Expected: FAIL because the raw-result query is not implemented.

- [ ] **Step 3: Implement only documented response fields**

Create private transport DTOs in `WorkToolClient` matching the official names exactly. Map `successList` and `failList` to raw strings and do not deserialize their contents.

Build the query with:

```csharp
var query = string.Join(
    "&",
    $"robotId={Escape(robotId)}",
    "page=1",
    "size=20",
    "sort=run_time",
    "desc=true",
    $"startTime={Escape(startTime.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}",
    $"endTime={Escape(endTime.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}",
    "type=512",
    $"messageId={Escape(messageId)}");
```

Use the existing administrative send path so the request is subject to the same transport, timeout, redaction, and egress limit. Do not return `robotId` from the application record.

- [ ] **Step 4: Run WorkTool contract tests**

Run:

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --filter FullyQualifiedName~WorkTool
```

Expected: PASS.

- [ ] **Step 5: Commit Task 2**

```powershell
git add src/server/WechatRobot.Application/WorkTool/WorkToolRawCommandResult.cs src/server/WechatRobot.Application/WorkTool/IWorkToolClient.cs src/server/WechatRobot.Infrastructure/WorkTool/WorkToolClient.cs tests/server/WechatRobot.ContractTests/WorkTool/RawCommandResultQueryContractTests.cs
git commit -m "feat: query documented WorkTool raw results"
```

---

### Task 3: Build a Development-Only Evidence Runner

**Files:**
- Create: `tools/WechatRobot.WorkToolEvidence/WechatRobot.WorkToolEvidence.csproj`
- Create: `tools/WechatRobot.WorkToolEvidence/Program.cs`
- Modify: `WechatRobot.slnx`
- Create: `tests/server/WechatRobot.ContractTests/WorkTool/Type512EvidenceSanitizerTests.cs`
- Create: `docs/runbooks/worktool-type512-evidence.md`

**Command contract:**

```powershell
dotnet run --project tools/WechatRobot.WorkToolEvidence -- `
  --robot-config-id 17341f22-a502-4fd4-8e1d-4746fb667c48 `
  --group-name "仅用于测试的客户群" `
  --output-directory .local/worktool-type512-evidence
```

The runner uses the same `.env` loader, dependency injection, database credential resolver, `WorkToolClient`, and global limiter as API/Worker. It refuses to run when `DOTNET_ENVIRONMENT` is not `Development`.

- [ ] **Step 1: Write failing evidence-sanitizer tests**

Define `Type512EvidenceShape` with no raw values:

```csharp
public sealed record Type512EvidenceShape(
    int Type,
    bool MessageIdMatched,
    int ResultCount,
    string SuccessListJsonKind,
    string FailListJsonKind,
    IReadOnlyList<string> RawMessagePropertyNames,
    IReadOnlyList<string> SuccessListObjectPropertyNames,
    IReadOnlyList<string> FailListObjectPropertyNames);
```

Tests must prove:

- JSON array/object/string/null kinds are reported accurately;
- object property names are sorted ordinally and deduplicated;
- array item values, group names, nicknames, robot IDs, message IDs, and error text never appear in serialized `shape.json`;
- malformed `successList` or `failList` is reported as `InvalidJson` without copying the raw string.

- [ ] **Step 2: Run the sanitizer tests and confirm they fail**

Run:

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --filter FullyQualifiedName~Type512EvidenceSanitizerTests
```

Expected: FAIL because the evidence tool and sanitizer do not exist.

- [ ] **Step 3: Implement the evidence runner**

The runner performs this exact sequence:

1. Verify `DOTNET_ENVIRONMENT=Development`.
2. Validate that `--robot-config-id`, `--group-name`, and `--output-directory` are present.
3. Resolve the output directory and reject it unless it is under the repository `.local` directory.
4. Submit `RequestGroupMemberSnapshotAsync`.
5. Require a non-empty returned `MessageId`.
6. Poll `ListGroupMemberSnapshotResultsAsync` every five seconds for at most 90 seconds, using a query window from five minutes before submission to five minutes after current UTC.
7. Match both `type=512` and the returned message ID.
8. Write the unmodified matched result to `raw.json` using `FileShare.None`.
9. Write only `Type512EvidenceShape` to `shape.json`.
10. Print only the output paths and completion status; never print WorkTool values.

Set both output files to be readable only by the current Windows user after creation. If ACL restriction fails, delete the just-created files and exit nonzero.

- [ ] **Step 4: Add the operator runbook**

The runbook must state:

- use a test group whose members consent to the diagnostic;
- run only on a trusted development machine;
- confirm `.local/` is ignored before execution with `git check-ignore .local/worktool-type512-evidence/raw.json`;
- inspect `raw.json` locally and never paste it into chat, issues, logs, or commits;
- report only the property locations and JSON kinds from `shape.json`;
- securely remove the evidence files after the contract has been documented.

- [ ] **Step 5: Run tests and build the runner**

Run:

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --filter FullyQualifiedName~Type512EvidenceSanitizerTests
dotnet build tools/WechatRobot.WorkToolEvidence/WechatRobot.WorkToolEvidence.csproj
git check-ignore .local/worktool-type512-evidence/raw.json
```

Expected: tests PASS, build PASS, and `git check-ignore` prints the test path.

- [ ] **Step 6: Commit Task 3**

```powershell
git add tools/WechatRobot.WorkToolEvidence WechatRobot.slnx tests/server/WechatRobot.ContractTests/WorkTool/Type512EvidenceSanitizerTests.cs docs/runbooks/worktool-type512-evidence.md
git commit -m "test: add private WorkTool type 512 evidence runner"
```

---

### Task 4: Capture and Review One Real Type 512 Result

**Files:**
- Local only, never commit: `.local/worktool-type512-evidence/raw.json`
- Local only, never commit: `.local/worktool-type512-evidence/shape.json`
- Modify after review: `docs/superpowers/specs/2026-07-27-worktool-group-import-and-agent-nickname-design.md`

- [ ] **Step 1: Verify the preconditions**

Confirm:

- the selected robot is online and belongs to the chosen test group;
- the group name exactly matches WorkTool;
- no production-only secrets will be copied into source-controlled files;
- the global rate limiter migration has been applied;
- `.local/worktool-type512-evidence/raw.json` is ignored.

- [ ] **Step 2: Run the evidence command**

Use the exact command documented in `docs/runbooks/worktool-type512-evidence.md`, substituting only the existing robot configuration ID and the consenting test group name.

Expected: the command returns success and creates both local evidence files. If it times out or returns no matching result, stop here and record the observed WorkTool error code; do not guess a response schema.

- [ ] **Step 3: Review the raw evidence locally**

Answer only these contract questions:

1. Which documented result field contains the nickname payload?
2. Is the payload a JSON string, array, or object?
3. For each member, which exact properties are present?
4. Is the group name repeated in the result?
5. Are member roles or stable IDs present, or only names?
6. What does an empty group result look like?
7. What exact fields distinguish success, partial success, and failure?

Do not infer stable identity from a nickname and do not preserve unrelated personal values in the specification.

- [ ] **Step 4: Update the approved specification with sanitized facts**

Add a `type=512 已验证返回契约` section to the group import and agent nickname specification. Record:

- WorkTool version/date and official endpoint links;
- exact property names and JSON kinds;
- success/empty/failure examples with all names replaced by `成员甲` and `成员乙`;
- which facts were directly observed and which remain unknown;
- the rule that group-agent configuration stays disabled if only ambiguous names are returned.

- [ ] **Step 5: Delete raw local evidence**

After the sanitized contract section has been reviewed:

```powershell
Remove-Item -LiteralPath .local\worktool-type512-evidence\raw.json
Remove-Item -LiteralPath .local\worktool-type512-evidence\shape.json
```

Verify:

```powershell
git status --short
```

Expected: no evidence files are listed.

---

### Task 5: Write the Evidence-Based Member Snapshot Implementation Plan

**Files:**
- Create only after Task 4 succeeds: `docs/superpowers/plans/2026-07-27-worktool-type512-member-snapshot.md`

- [ ] **Step 1: Use the writing-plans skill against the verified specification**

The follow-on plan must name the exact observed nickname path and include complete tasks for:

- response DTO and contract tests using the sanitized real sample;
- snapshot entity, migration, uniqueness, observed timestamp, and source metadata;
- polling/retry behavior keyed by WorkTool message ID;
- stale snapshot expiration and refresh action;
- exact-name matching with ambiguity detection;
- group-agent enablement only for a fresh unambiguous match;
- API and Vue contracts;
- personal-data audit/redaction;
- MySQL 5.7 migration and rollback verification;
- unit, contract, integration, frontend, and production-build verification.

If Task 4 shows stable WorkTool or WeCom member IDs, the plan may use them. If it shows names only, the plan must retain nickname-only semantics and explicitly reject duplicate or ambiguous matches.

- [ ] **Step 2: Self-review the follow-on plan**

Reject the plan if it:

- names any field not present in the captured evidence or official documentation;
- treats a nickname as a stable platform identity;
- enables assignment from a stale or ambiguous snapshot;
- logs raw member results;
- bypasses the shared WorkTool limiter;
- depends on 企业微信 member APIs that are not configured in this phase.

- [ ] **Step 3: Commit only the sanitized specification and follow-on plan**

```powershell
git add docs/superpowers/specs/2026-07-27-worktool-group-import-and-agent-nickname-design.md docs/superpowers/plans/2026-07-27-worktool-type512-member-snapshot.md
git diff --cached --check
git commit -m "docs: plan verified WorkTool member snapshots"
```

---

## Final Verification

- [ ] `dotnet build WechatRobot.slnx`
- [ ] `dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj`
- [ ] `dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj`
- [ ] `dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter FullyQualifiedName~WorkTool`
- [ ] No committed file contains a real group name, nickname, robot ID, or raw `type=512` result
- [ ] No production API exposes the evidence runner or raw WorkTool result
- [ ] Group-agent controls remain disabled until the follow-on member-snapshot plan is implemented and verified
- [ ] `git diff --check`

## Stop Conditions

Stop without implementing a nickname parser when any of these occurs:

- the official query never returns a row matching both `type=512` and `messageId`;
- the returned payload location cannot be identified from one real result;
- WorkTool returns only aggregate counts and no nickname list;
- the result contains duplicate names with no stable identifier;
- obtaining evidence would require exposing production personal data.

These outcomes are evidence, not implementation failures. Record the limitation in the specification and keep group-agent assignment disabled.
