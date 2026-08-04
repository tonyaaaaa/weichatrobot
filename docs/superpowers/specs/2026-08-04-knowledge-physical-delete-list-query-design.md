# 知识库物理删除后列表查询修复设计

## 背景

管理员提交知识文档物理删除后，文档会先进入“等待物理清理”状态。管理端随后刷新 `/api/knowledge/documents` 时，API 在 `KnowledgeDocumentAdministrationQuery.BuildSummariesAsync` 查询对应 `CleanupKnowledgeDocument` 任务状态，并在 MySQL Provider 创建数据库命令时抛出 `NullReferenceException`，导致整个知识库列表返回 500。

异常仅出现在存在待物理删除文档时。普通列表查询以及提交物理删除本身均已完成。

## 根因

`BuildSummariesAsync` 将所有待清理任务 ID 组成 `Guid[]`，随后执行：

```csharp
cleanupJobIdValues.Contains(job.Id)
```

该写法要求 MySQL EF Provider 翻译运行时 GUID 集合参数。项目已经通过 `GuidBatchQuery` 统一规避 MySQL 对 GUID 集合参数的兼容问题，但这条后来新增的清理任务状态查询没有使用该机制。

因此，问题不是文档、版本或清理任务数据为空，而是待删除分支新增的 GUID 集合查询使用了与项目 MySQL 兼容策略不一致的表达式。堆栈中的失败行正是此查询的 `ToDictionaryAsync`。

## 目标

- 待物理删除文档存在时，知识库列表能够正常返回。
- 保留“等待物理清理”和“可重试物理删除”的现有业务语义。
- 保持列表查询有界，避免逐文档 N+1 查询。
- 不修改物理删除任务、Worker 清理流程、数据库结构或已有数据。

## 非目标

- 不同步执行物理删除。
- 不改变清理任务重试规则。
- 不升级或替换 MySQL Provider。
- 不启动 Worker，不主动处理当前等待中的清理任务。
- 不增加数据库迁移、数据修复脚本或 Qdrant 操作。

## 设计

### 1. 使用现有 GUID 批量查询兼容器

`KnowledgeDocumentAdministrationQuery.BuildSummariesAsync` 继续根据文档 ID 生成确定性的清理任务 ID，但不再把整个 `Guid[]` 交给 LINQ `Contains`。

改为通过 `GuidBatchQuery.CreateBatches` 分批，每批最多 100 个 ID，并用 `GuidBatchQuery.BuildPredicate<DurableJobEntity>` 生成显式 GUID 相等条件。每批仅投影任务 ID 和状态，查询结果汇总后在内存构建字典。

列表页现有 `pageSize` 最大值也是 100，因此正常情况下只产生一批任务状态查询；设计仍支持未来复用时的多批行为。

建议在查询类内增加专用私有方法：

```csharp
private async Task<Dictionary<Guid, string>> LoadCleanupStatusesAsync(
    IReadOnlyCollection<Guid> cleanupJobIds,
    CancellationToken cancellationToken)
```

方法行为：

1. 空集合立即返回空字典。
2. 对 `GuidBatchQuery.CreateBatches(cleanupJobIds)` 逐批执行查询。
3. 每批使用 `BuildPredicate` 限定 `DurableJobEntity.Id`。
4. 同时限定 `JobType == "CleanupKnowledgeDocument"`。
5. 仅投影 `Id` 和 `Status`。
6. 将结果写入单个字典；同一确定性任务 ID 最多对应一条数据库记录。

### 2. 保持列表返回契约不变

`CanRetryPhysicalDelete` 的计算规则不变：

- 文档必须已经请求物理删除；
- 必须存在对应清理任务；
- 清理任务状态必须为 `deadLetter` 或 `cancelled`。

任务不存在或仍处于 `pending`、`processing` 等状态时，列表正常返回且 `CanRetryPhysicalDelete` 为 `false`。

不修改 API DTO、前端类型或页面显示。

### 3. 错误处理

不捕获并吞掉数据库异常。修复查询表达式后，真实数据库连接或执行错误仍由现有全局错误边界记录和返回。

这样可以避免把数据库故障伪装成“不可重试”，同时解决当前由 Provider GUID 集合翻译触发的异常。

## 测试设计

### MySQL 集成回归测试

扩展 `KnowledgeDocumentAdministrationMySqlTests`：

- 建立至少两个待物理删除文档，分别对应 `pending` 和 `deadLetter` 清理任务；
- 再建立一个普通文档，确保混合列表场景正常；
- 调用 `KnowledgeDocumentAdministrationQuery.ListAsync`；
- 断言所有文档均正常返回；
- 断言 `pending` 文档不可重试；
- 断言 `deadLetter` 文档可重试；
- 断言普通文档不受影响。

该用例覆盖引发生产异常的多 GUID 任务状态查询，不只验证单个任务。

### GUID 批量边界测试

如果现有测试基础设施可以低成本记录查询批次，则增加超过 100 个 ID 的纯查询辅助测试，验证分批合并结果。否则依赖已经覆盖的 `GuidBatchQuery.CreateBatches` 单元测试，并保持本次集成用例聚焦生产故障。

### 回归验证

- 运行知识管理 MySQL 集成测试。
- 运行完整后端单元测试。
- 构建 API Release。
- 执行 `git diff --check`。

## 部署影响

- 需要重新发布 API，因为异常发生在 API 的知识文档管理查询中。
- Worker 代码不因本修复发生变化；但本次与聊天修复共同发布时，仍按聊天修复的发布要求生成 API、Worker 和前端发布包。
- 不需要 MySQL 迁移。
- 不需要重新索引知识库或修改 Qdrant 集合。
- 发布后刷新知识库页面即可验证列表恢复；当前待清理任务仍由原有 Worker 流程处理。

## 验收标准

- 提交物理删除后刷新知识库页面不再返回 500。
- “等待物理清理”文档能正常显示。
- `deadLetter` 或 `cancelled` 清理任务仍显示可重试操作。
- 普通知识文档列表、详情、上传、审核和私聊入库流程不受影响。
- 没有新增迁移、数据清理或向量索引操作。
