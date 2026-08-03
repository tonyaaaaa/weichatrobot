# Dynamic Reply Lane Configuration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make group reply, private reply, and private knowledge-ingest concurrency configurable in Worker JSON configuration with safe hot reload and defaults of `8/4/4`.

**Architecture:** Bind a non-secret `ReplyProcessing` options object, generate stable lane identities up to bounded maxima, and keep disabled lanes dormant behind a change-aware activation gate. JSON reload updates only the activation counts; a lane that is already processing finishes its job before observing a reduced count, so scale-down never cancels in-flight work.

**Tech Stack:** .NET 10, ASP.NET Core Generic Host, `IOptionsMonitor<T>`, xUnit v3, PowerShell packaging.

## Global Constraints

- Defaults are group `8`, private reply `4`, and private knowledge ingest `4`.
- Allowed ranges are group `1-32`, private reply `1-16`, and private knowledge ingest `1-8`.
- Do not add a database setting, migration, API, administration audit, frontend control, third-party scheduler, or in-memory work queue.
- Invalid startup values fail Worker startup; invalid hot reload values retain the last valid counts.
- Scale-down never cancels an in-flight LLM, retrieval, persistence, or delivery operation.
- `.local` remains local-only and must not be staged, committed, or included in the release ZIP.

---

### Task 1: Strongly typed lane configuration and planning

**Files:**
- Create: `src/server/WechatRobot.Application/Jobs/DurableReplyLaneOptions.cs`
- Modify: `src/server/WechatRobot.Application/Jobs/DurableReplyLanePlan.cs`
- Modify: `tests/server/WechatRobot.UnitTests/Jobs/DurableReplyLanePlanTests.cs`

**Interfaces:**
- Produces: `DurableReplyLaneOptions.SectionName`, default and maximum constants, `TryValidate(out string error)`.
- Produces: `DurableReplyLanePlan.Create(DurableReplyLaneOptions)` for enabled lanes and `DurableReplyLanePlan.Maximum` for all bounded lane identities.

- [x] **Step 1: Write failing option and plan tests**

Add assertions that the default plan contains 8 `ProcessInboundMessage`, 4 `ProcessPrivateMessage`, and 4 `ProcessPrivateKnowledgeIngest` lanes; custom counts produce the requested totals; zero and above-maximum values fail validation; and all maximum-plan names are unique.

- [x] **Step 2: Run the focused tests and confirm failure**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj -- --filter-class WechatRobot.UnitTests.Jobs.DurableReplyLanePlanTests
```

Expected: compilation or assertion failure because configurable options and `8/4/4` planning do not exist.

- [x] **Step 3: Implement options and plan generation**

Create an options class equivalent to:

```csharp
public sealed class DurableReplyLaneOptions
{
    public const string SectionName = "ReplyProcessing";
    public const int DefaultGroupLaneCount = 8;
    public const int DefaultPrivateLaneCount = 4;
    public const int DefaultPrivateKnowledgeIngestLaneCount = 4;
    public const int MaximumGroupLaneCount = 32;
    public const int MaximumPrivateLaneCount = 16;
    public const int MaximumPrivateKnowledgeIngestLaneCount = 8;

    public int GroupLaneCount { get; set; } = DefaultGroupLaneCount;
    public int PrivateLaneCount { get; set; } = DefaultPrivateLaneCount;
    public int PrivateKnowledgeIngestLaneCount { get; set; } = DefaultPrivateKnowledgeIngestLaneCount;
    public bool TryValidate(out string error)
    {
        if (GroupLaneCount is < 1 or > MaximumGroupLaneCount)
        {
            error = "group_lane_count_out_of_range";
            return false;
        }
        if (PrivateLaneCount is < 1 or > MaximumPrivateLaneCount)
        {
            error = "private_lane_count_out_of_range";
            return false;
        }
        if (PrivateKnowledgeIngestLaneCount is < 1 or > MaximumPrivateKnowledgeIngestLaneCount)
        {
            error = "private_ingest_lane_count_out_of_range";
            return false;
        }
        error = string.Empty;
        return true;
    }
}
```

Generate lanes as `group-1..N`, `private-1..N`, and `private-ingest-1..N`, preserving the existing job-type strings.

- [x] **Step 4: Run focused tests and confirm pass**

Run the command from Step 2 and expect all `DurableReplyLanePlanTests` to pass.

### Task 2: Hot-reload activation gate and Worker integration

**Files:**
- Create: `src/server/WechatRobot.Worker/Jobs/DurableReplyLaneActivation.cs`
- Modify: `src/server/WechatRobot.Worker/Jobs/DurableJobWorker.cs`
- Modify: `src/server/WechatRobot.Worker/Program.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Messaging/DurableJobWorkerResilienceTests.cs`

**Interfaces:**
- Consumes: `DurableReplyLanePlan.Maximum` and validated `DurableReplyLaneOptions`.
- Produces: `DurableReplyLaneActivation.Update(options)` and `WaitUntilEnabledAsync(lane, stoppingToken)`.
- `DurableJobWorker` consumes `IOptionsMonitor<DurableReplyLaneOptions>` while retaining constructor compatibility for existing direct tests.

- [x] **Step 1: Write failing hot-reload tests**

Add a mutable `IOptionsMonitor<DurableReplyLaneOptions>` test double and repository counters. Verify that changing group count from 1 to 2 starts a second group consumer, changing it back to 1 stops the second consumer from leasing another job after its current call returns, an invalid update is ignored, a subsequent valid update is applied, and host shutdown completes all lane tasks.

- [x] **Step 2: Run the focused integration tests and confirm failure**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class WechatRobot.IntegrationTests.Messaging.DurableJobWorkerResilienceTests
```

Expected: compilation or assertion failure because the Worker does not consume monitored lane options.

- [x] **Step 3: Implement the activation gate**

Maintain the last valid immutable counts and a replaceable asynchronous change signal. `WaitUntilEnabledAsync` returns immediately for enabled lane indices and otherwise waits for the next valid update. `Update` swaps counts and completes the previous signal so dormant lanes re-evaluate without polling MySQL.

- [x] **Step 4: Integrate monitored options into the Worker**

Bind the section in `Program.cs`:

```csharp
builder.Services.AddOptions<DurableReplyLaneOptions>()
    .BindConfiguration(DurableReplyLaneOptions.SectionName);
```

At Worker startup, validate `CurrentValue` before the first asynchronous wait. Subscribe to `OnChange`; valid values update the activation gate, while invalid values log only a stable reason code and retain the previous state. Start the bounded maximum lane set once. Each lane waits for activation before leasing a job and re-checks activation only after the job finishes. Application shutdown alone supplies the cancellation token used by processing.

- [x] **Step 5: Run focused integration tests and confirm pass**

Run the command from Step 2 and expect all `DurableJobWorkerResilienceTests` to pass.

### Task 3: Configuration, regression verification, and release package

**Files:**
- Modify: `src/server/WechatRobot.Worker/appsettings.json`
- Modify locally only: `.local/appsettings.json`
- Create generated output only: `artifacts/wechatrobot-<timestamp>.zip`

**Interfaces:**
- Consumes: the `ReplyProcessing` section contract from Task 1.
- Produces: committed package defaults and a local effective configuration of `8/4/4`.

- [x] **Step 1: Add the committed Worker configuration**

Add:

```json
"ReplyProcessing": {
  "_comment": "支持运行时热更新；缩容会等待正在执行的任务完成。环境变量覆盖值需要重启 Worker。",
  "GroupLaneCount": 8,
  "PrivateLaneCount": 4,
  "PrivateKnowledgeIngestLaneCount": 4
}
```

Update the configuration guide so it no longer claims every setting requires restart.

- [x] **Step 2: Update local configuration without exposing or staging secrets**

Use a structure-aware edit to add or replace only `.local/appsettings.json`'s `ReplyProcessing` object. Verify property names and numeric values without printing the rest of the file.

- [x] **Step 3: Run complete relevant verification**

Run:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -- --filter-class WechatRobot.IntegrationTests.Messaging.DurableJobWorkerResilienceTests
dotnet build src/server/WechatRobot.Worker/WechatRobot.Worker.csproj -c Release
git diff --check
```

Expected: all tests pass, Release build succeeds, and diff check reports no errors.

- [x] **Step 4: Publish and archive**

Create an empty staging directory under `artifacts/staging/wechatrobot-<timestamp>`, then run:

```powershell
dotnet publish src/server/WechatRobot.Api/WechatRobot.Api.csproj -c Release -r win-x64 --self-contained false -o artifacts/staging/wechatrobot-<timestamp>/api
dotnet publish src/server/WechatRobot.Worker/WechatRobot.Worker.csproj -c Release -r win-x64 --self-contained false -o artifacts/staging/wechatrobot-<timestamp>/worker
npm run build --prefix src/web/wechatrobot-admin
Copy-Item src/web/wechatrobot-admin/dist artifacts/staging/wechatrobot-<timestamp>/web -Recurse
Compress-Archive -Path artifacts/staging/wechatrobot-<timestamp> -DestinationPath artifacts/wechatrobot-<timestamp>.zip
```

Inspect archive entries to confirm `.local`, `.env`, development logs, test outputs, and secret-bearing files are absent.

- [x] **Step 5: Review and commit implementation**

Review `git status --short` and `git diff`, stage only the plan and implementation files, then commit:

```powershell
git commit -m "feat: configure dynamic reply concurrency"
```

Report the commit, ZIP path and size, verification commands, and whether runtime hot reload was tested against a fresh local Worker process.
