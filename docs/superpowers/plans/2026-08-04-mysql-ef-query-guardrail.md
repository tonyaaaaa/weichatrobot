# MySQL EF Query Guardrail Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 MySQL EF Provider 的运行时 Guid 集合查询兼容性经验固化为仓库强制规则和可执行排障手册，避免 API 500、Worker 退出及静默业务降级再次发生。

**Architecture:** 根目录 `AGENTS.md` 只承载必须遵守的简短禁令和 runbook 链接；专项 runbook 承载故障识别、安全代码模式、测试边界、验证命令和审查清单。此次仅修改文档，不修改运行时代码、数据库结构或配置。

**Tech Stack:** Markdown、ASP.NET Core 10、Entity Framework Core、MySql.EntityFrameworkCore 10.0.7、xUnit v3

## Global Constraints

- 禁止对运行时 `Guid[]`、`List<Guid>` 或其他 Guid 集合直接使用 `Contains` 生成 MySQL 查询。
- 必须使用 `GuidBatchQuery.CreateBatches` 与 `GuidBatchQuery.BuildPredicate`，每批最多 100 个 Guid。
- 禁止用逐 Guid 查询替代批处理，避免 N+1。
- `ExecuteUpdateAsync`/`ExecuteDeleteAsync` 的空值赋值和 Provider SQL 必须有真实 MySQL 覆盖或等价的 Provider 边界回归测试。
- 文档必须覆盖 API 500、Worker 退出和异常被捕获后的静默业务降级。
- 不得写入连接字符串、数据库地址、正式数据或 `.local` 配置值。

---

### Task 1: Add the repository-level mandatory rule

**Files:**
- Modify: `AGENTS.md:185`

**Interfaces:**
- Consumes: `GuidBatchQuery.CreateBatches(IEnumerable<Guid>, int)` and `GuidBatchQuery.BuildPredicate<TEntity>(IReadOnlyCollection<Guid>, Expression<Func<TEntity, Guid>>)` from `WechatRobot.Infrastructure.Persistence`.
- Produces: a mandatory repository rule linked to `docs/runbooks/mysql-ef-provider-query-compatibility.md`.

- [ ] **Step 1: Add the MySQL EF Guid-query guardrail to `Data and migrations`**

Insert the following rules after the existing bounded-query rule:

```markdown
- With `MySql.EntityFrameworkCore`, never translate `Contains` over a runtime
  `Guid[]`, `List<Guid>`, or other Guid collection into SQL. Use
  `GuidBatchQuery.CreateBatches` and `GuidBatchQuery.BuildPredicate` with at
  most 100 IDs per batch; do not replace batching with one query per ID.
- Treat `RelationalCommand.CreateDbCommand` or
  `TypeMappedRelationalParameter.AddDbParameter` null references as a likely
  provider-query compatibility failure. Check runtime collection parameters
  and bulk updates before blaming empty business data.
- Cover provider-sensitive `ExecuteUpdateAsync`/`ExecuteDeleteAsync`
  expressions, especially nullable assignments, with real MySQL integration
  coverage or an equivalent provider-boundary regression test. Follow
  `docs/runbooks/mysql-ef-provider-query-compatibility.md`.
```

- [ ] **Step 2: Verify the mandatory terms are searchable**

Run:

```powershell
rg -n "GuidBatchQuery|runtime.*Guid|Contains|CreateDbCommand|AddDbParameter|mysql-ef-provider-query-compatibility" AGENTS.md
```

Expected: the `Data and migrations` section contains all six concepts and the runbook link.

- [ ] **Step 3: Check Markdown whitespace**

Run:

```powershell
git diff --check -- AGENTS.md
```

Expected: exit code 0 with no output.

- [ ] **Step 4: Commit the repository rule**

```powershell
git add -- AGENTS.md
git commit -m "docs: enforce mysql ef query guardrail"
```

### Task 2: Add the MySQL EF Provider compatibility runbook

**Files:**
- Create: `docs/runbooks/mysql-ef-provider-query-compatibility.md`

**Interfaces:**
- Consumes: the mandatory rule added in Task 1 and the existing fixed-reply, knowledge-list, and cleanup recovery design records.
- Produces: an operational guide for diagnosis, implementation, testing, and review of MySQL EF Guid batching and provider-sensitive bulk updates.

- [ ] **Step 1: Write the runbook with known symptoms and root cause**

Create the document with these explicit facts:

```markdown
# MySQL EF Provider 查询兼容性

## 适用范围

本项目使用 `MySql.EntityFrameworkCore 10.0.7`。凡是 EF Core 查询需要把运行时 Guid 集合或批量更新表达式翻译成 MySQL SQL，都必须遵守本手册。

## 已知症状

- API 在列表或候选加载时返回 500，堆栈包含 `RelationalCommand.CreateDbCommand`。
- Worker 在后台恢复或清理任务启动后退出，堆栈包含 `TypeMappedRelationalParameter.AddDbParameter`。
- 上层捕获候选加载异常并降级到 RAG，最终仍有回复，但固定回复或 Agent 路由未生效。

这些表现不等于业务数据为空。优先检查运行时 Guid 集合 `Contains`、Provider 参数绑定和 `ExecuteUpdateAsync`/`ExecuteDeleteAsync` 表达式。
```

- [ ] **Step 2: Document forbidden and safe Guid query patterns**

Include a forbidden example:

```csharp
var ids = requestedIds.Distinct().ToArray();
var rows = await database.Entities
    .Where(entity => ids.Contains(entity.Id))
    .ToArrayAsync(cancellationToken);
```

Include the required pattern:

```csharp
var rows = new List<Entity>();
foreach (var batch in GuidBatchQuery.CreateBatches(requestedIds))
{
    var predicate = GuidBatchQuery.BuildPredicate<Entity>(batch, entity => entity.Id);
    rows.AddRange(await database.Entities
        .AsNoTracking()
        .Where(predicate)
        .ToArrayAsync(cancellationToken));
}
```

State that the default maximum is 100 IDs per batch, results are merged in memory, ordering must be restored explicitly when observable, and one query per Guid is prohibited because it creates N+1 behavior.

- [ ] **Step 3: Document provider-sensitive bulk update boundaries**

Explain that `ExecuteUpdateAsync` and `ExecuteDeleteAsync` expressions are not assumed safe merely because they compile. Nullable assignments and runtime collection filters require real MySQL verification or an equivalent query-expression interceptor regression test. If the provider boundary is unsafe and the batch is bounded, load tracked entities, apply explicit property changes, preserve concurrency-token behavior, and call `SaveChangesAsync`.

- [ ] **Step 4: Add regression and verification commands**

Document these checks:

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore
rg -n "\.Contains\(" src/server -g "*.cs"
git diff --check
```

Explain that the `Contains` scan requires manual review of runtime Guid collections rather than blind replacement, and that runtime fixes require rebuilding/restarting the affected API or Worker plus fresh endpoint/log evidence.

- [ ] **Step 5: Add the code-review checklist and incident references**

The checklist must verify:

- collection element type and whether it is a runtime Guid collection;
- bounded batching at no more than 100 IDs;
- no per-ID N+1 query;
- deterministic result ordering after merging batches;
- cancellation-token propagation;
- real MySQL or equivalent provider-boundary regression coverage;
- fallback paths do not hide candidate-loading failures without structured logs.

Link these existing records:

- `docs/superpowers/specs/2026-08-04-fixed-reply-mysql-guid-batch-design.md`
- `docs/superpowers/specs/2026-08-04-knowledge-physical-delete-list-query-design.md`
- `docs/superpowers/plans/2026-07-31-physical-delete-recovery.md`
- `docs/superpowers/plans/2026-08-04-chat-source-privacy-and-web-search-prompt-plan.md`

- [ ] **Step 6: Verify the completed documentation**

Run:

```powershell
rg -n "API.*500|Worker|静默|GuidBatchQuery|100|N\+1|ExecuteUpdateAsync|ExecuteDeleteAsync|CreateDbCommand|AddDbParameter" docs/runbooks/mysql-ef-provider-query-compatibility.md
rg -n "connection string|password|api.?key|\.local" docs/runbooks/mysql-ef-provider-query-compatibility.md
git diff --check -- AGENTS.md docs/runbooks/mysql-ef-provider-query-compatibility.md
```

Expected: the first command finds every required concept; the second command has no output; the diff check exits 0.

- [ ] **Step 7: Commit the runbook**

```powershell
git add -- docs/runbooks/mysql-ef-provider-query-compatibility.md
git commit -m "docs: add mysql ef compatibility runbook"
```

### Task 3: Final documentation review

**Files:**
- Review: `AGENTS.md`
- Review: `docs/runbooks/mysql-ef-provider-query-compatibility.md`
- Review: `docs/superpowers/specs/2026-08-04-mysql-ef-query-guardrail-design.md`

**Interfaces:**
- Consumes: the repository rule and runbook from Tasks 1 and 2.
- Produces: evidence that the approved spec is fully represented and the working tree contains no accidental task-related files.

- [ ] **Step 1: Compare implementation to the approved spec**

Run:

```powershell
git show --stat --oneline HEAD~2..HEAD
Get-Content docs/superpowers/specs/2026-08-04-mysql-ef-query-guardrail-design.md
Get-Content AGENTS.md | Select-Object -Skip 184 -First 30
Get-Content docs/runbooks/mysql-ef-provider-query-compatibility.md
```

Expected: every design acceptance item appears in either the root rule or runbook, with no runtime code, schema, or configuration edits.

- [ ] **Step 2: Run final hygiene checks**

Run:

```powershell
git diff --check HEAD~2..HEAD
git status --short
```

Expected: diff check exits 0; status shows no uncommitted files created by this task. Any unrelated pre-existing changes remain untouched and are reported separately.
