# MySQL EF Provider 查询兼容性

## 适用范围

本项目使用 `MySql.EntityFrameworkCore 10.0.7`。凡是 EF Core 查询需要把运行时 Guid 集合或批量更新表达式翻译成 MySQL SQL，都必须遵守本手册。

本文解决的是 Provider 查询翻译和参数绑定边界，不代表所有 `Contains` 都有问题。代码审查时必须结合集合元素类型、集合产生方式、数据库 Provider 及最终 SQL 翻译路径判断。

## 已知症状

- API 在列表或候选加载时返回 500，堆栈包含 `RelationalCommand.CreateDbCommand`。
- Worker 在后台恢复或清理任务启动后退出，堆栈包含 `TypeMappedRelationalParameter.AddDbParameter`。
- 上层捕获候选加载异常并降级到 RAG，最终仍有回复，但固定回复或 Agent 路由未生效，形成静默业务降级。

这些表现不等于业务数据为空。优先检查运行时 Guid 集合 `Contains`、Provider 参数绑定和 `ExecuteUpdateAsync`/`ExecuteDeleteAsync` 表达式。不能只根据最终是否产生机器人回复判断候选加载或 Agent 路由是否成功，必须同时检查结构化日志中的失败代码和异常边界。

## 禁止的 Guid 集合查询

禁止让 `MySql.EntityFrameworkCore` 将运行时 `Guid[]`、`List<Guid>` 或其他 Guid 集合上的 `Contains` 直接翻译成 SQL。例如：

```csharp
var ids = requestedIds.Distinct().ToArray();
var rows = await database.Entities
    .Where(entity => ids.Contains(entity.Id))
    .ToArrayAsync(cancellationToken);
```

该写法可能在查询枚举时才触发 Provider 参数绑定异常，因此编译通过、InMemory 测试通过或成功构造 `IQueryable` 都不能证明真实 MySQL 安全。

## 必须使用的安全模式

使用仓库已有的 `GuidBatchQuery`，生成显式 Guid 等值谓词并执行有界批处理：

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

必须同时满足以下约束：

- 默认每批最多 100 个 Guid；不得绕过批次上限构造超大 OR 谓词。
- 在内存中合并各批次结果；如果结果顺序对外可见，合并后必须显式恢复确定性排序。
- 不得改成每个 Guid 单独查询，否则会造成 N+1 查询。
- 所有异步数据库调用必须继续传递原始 `CancellationToken`。
- 空集合应直接产生空结果，不应发送无意义查询。

## 批量更新和删除边界

`ExecuteUpdateAsync` 和 `ExecuteDeleteAsync` 表达式能够编译，不代表 Provider 能在真实 MySQL 上安全生成并绑定 SQL。以下情况必须有真实 MySQL 集成覆盖，或能复现同一 Provider 边界的等价回归测试：

- 更新或删除条件包含运行时 Guid 集合；
- setter 将可空字段赋值为 `null`；
- 表达式包含 Provider 特定的类型映射、转换或并发字段；
- 失败发生在 `CreateDbCommand` 或 `AddDbParameter`，而不是普通业务校验。

如果 Provider 边界不安全，并且待处理数据已经通过批次限制保持有界，可以采用跟踪实体更新：加载目标实体、逐项设置明确属性、保留现有并发令牌语义，最后调用 `SaveChangesAsync(cancellationToken)`。不要为了规避 Provider 问题改成无界加载，也不要吞掉并发冲突。

## 回归测试与验证

优先增加能复现原始边界的最小回归测试。对于运行时 Guid 集合，可以在测试中使用查询表达式拦截器，检测 `Enumerable.Contains<Guid>` 或 `Queryable.Contains<Guid>` 并主动抛出稳定异常，以证明生产代码不再依赖不安全翻译。Provider 特定的 SQL、参数类型和可空赋值仍应尽可能使用真实 MySQL 集成测试验证。

常用验证命令：

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore
rg -n "\.Contains\(" src/server -g "*.cs"
git diff --check
```

`Contains` 扫描结果必须人工审查，只处理会进入 MySQL SQL 翻译的运行时 Guid 集合，不能机械替换字符串集合、内存过滤或其他已验证安全的表达式。

运行时代码修复后，还必须重新编译并重启受影响的 API 或 Worker，再使用新进程产生的端点、结构化日志或真实业务链路证据验证。旧日志、旧二进制、外层代理 HTTP 200 或最终降级回复都不能证明故障已修复。

## 代码审查清单

- [ ] 确认集合元素类型，以及它是否为运行时生成的 Guid 集合。
- [ ] 使用 `GuidBatchQuery.CreateBatches`，每批不超过 100 个 ID。
- [ ] 使用 `GuidBatchQuery.BuildPredicate`，没有运行时 Guid 集合 `Contains`。
- [ ] 没有改成逐 ID 查询造成 N+1。
- [ ] 合并批次后恢复所有对外可见的确定性排序。
- [ ] 所有数据库 I/O 继续传递 `CancellationToken`。
- [ ] Provider 敏感表达式具有真实 MySQL 或等价 Provider 边界回归覆盖。
- [ ] 候选加载等异常具有结构化日志，不会被 fallback 路径无痕隐藏。
- [ ] API 或 Worker 运行时改动使用新构建、新进程完成验证。

## 已修复案例

- [固定回复模板 MySQL Guid 批量查询修复设计](../superpowers/specs/2026-08-04-fixed-reply-mysql-guid-batch-design.md)：候选模板、示例和群规则加载异常会被 Agent fallback 掩盖。
- [知识库物理删除列表查询修复设计](../superpowers/specs/2026-08-04-knowledge-physical-delete-list-query-design.md)：管理列表查询运行时 Guid 集合导致 API 500。
- [知识库物理删除恢复计划](../superpowers/plans/2026-07-31-physical-delete-recovery.md)：后台恢复中的 Provider 敏感批量更新导致 Worker 退出。
- [聊天来源隐藏与知识库查询修复计划](../superpowers/plans/2026-08-04-chat-source-privacy-and-web-search-prompt-plan.md)：知识库管理查询改为有界 `GuidBatchQuery`。
