# MySQL EF 查询兼容性文档护栏设计

## 背景

项目使用 `MySql.EntityFrameworkCore 10.0.7`。生产环境已经多次出现运行时 Guid 集合查询导致的 Provider 异常，影响过知识库列表、物理删除恢复和固定回复候选加载。典型堆栈位于 `RelationalCommand.CreateDbCommand` 或 `TypeMappedRelationalParameter.AddDbParameter`，外部表现可能是 API 500、Worker 退出，或异常被捕获后静默降级到其他业务路径。

仅在单次修复 spec 中记录不足以约束后续开发，需要将该经验提升为仓库级开发规则。

## 目标

- 后续 Agent 和开发者在编写 EF/MySQL Guid 批量查询前能够看到明确禁令。
- 给出可直接复制的安全查询模式和测试要求。
- 说明异常可能被降级逻辑隐藏，不能只根据最终业务回复判断 Agent 是否执行。
- 不修改运行时代码、数据库结构或配置。

## 文档结构

### 根目录 AGENTS.md

在 `Data and migrations` 中增加醒目的强制规则：

- 禁止对运行时 `Guid[]`、`List<Guid>` 或其他 Guid 集合直接使用 `Contains` 生成 MySQL 查询。
- 必须使用 `GuidBatchQuery.CreateBatches` 与 `BuildPredicate`，每批最多 100 个 Guid。
- 禁止用逐 Guid 查询替代，避免 N+1。
- 对 `ExecuteUpdateAsync`/`ExecuteDeleteAsync` 的空值赋值和 Provider SQL 必须使用真实 MySQL 覆盖或等价的 Provider 边界回归测试。
- 出现 `CreateDbCommand`、`AddDbParameter` 空引用时，优先排查集合参数和批量更新，而不是归因于业务数据为空。
- 链接专项 runbook。

### MySQL EF 兼容性 runbook

新增 `docs/runbooks/mysql-ef-provider-query-compatibility.md`，包含：

1. 已知故障症状及业务影响。
2. 禁止写法示例。
3. `GuidBatchQuery` 正确写法示例。
4. 批量更新空值的安全边界。
5. TDD 回归要求和验证命令。
6. 代码审查检查清单。
7. 已修复案例链接，包括固定回复、知识库列表和物理删除恢复。

## 验收

- `AGENTS.md` 中能够直接搜索到 `GuidBatchQuery`、`runtime Guid` 和 `Contains` 禁令。
- runbook 同时说明 API 500、Worker 退出和静默业务降级三种表现。
- 文档不包含任何连接字符串、数据库地址、正式数据或 `.local` 配置值。
- Markdown 格式检查和 `git diff --check` 通过。
