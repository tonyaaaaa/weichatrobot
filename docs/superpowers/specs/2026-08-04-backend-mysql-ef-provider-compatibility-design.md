# 后端 MySQL EF Provider 全面兼容性修复设计

## 背景

项目使用 `MySql.EntityFrameworkCore 10.0.7`。生产环境已经出现多类 Provider 运行时异常：运行时 Guid 集合通过 `Contains` 进入 SQL 翻译时可能在 `RelationalCommand.CreateDbCommand` 失败；包含可空字段赋值的 `ExecuteUpdateAsync` 可能在 `TypeMappedRelationalParameter.AddDbParameter` 失败。外部表现包括 API 500、Worker 退出，以及异常被 fallback 捕获后静默降级为其他业务路径。

MySQL 官方 NuGet 包声明 `MySql.EntityFrameworkCore 10.0.7` 提供 `net10.0` 资产，并依赖 EF Core 10.0.7 与 MySql.Data 9.7.0，因此本项目不是使用了官方未声明支持的 .NET/EF 组合。当前异常发生在客户端 Provider 构造命令和绑定参数阶段，SQL 通常尚未提交给 MySQL Server；调整 MySQL Server 版本不能替代代码侧的表达式兼容性修复。官方兼容声明也不代表所有 LINQ 和 bulk setter 形状都已经被 Provider 正确处理。

当前代码已对固定回复、知识库管理列表和物理清理 Worker 的部分查询进行了修复，但物理删除 API 等路径仍使用高风险批量 setter。全库扫描发现 `src/server` 中共有 74 处 `ExecuteUpdateAsync` 或 `ExecuteDeleteAsync` 调用，分布在 10 个文件；仅修复当前 DELETE 接口无法阻止相同问题在任务、会话或 WorkTool 状态机中再次出现。

## 目标

- 审计整个后端所有 EF 批量更新、批量删除和运行时 Guid 集合查询。
- 消除未经验证的运行时 Guid 集合 `Contains` 和 Provider 高风险可空 bulk setter。
- 保留任务抢占、租约、幂等、并发令牌、事务和审计语义。
- 建立自动化守卫，使新增危险模式在测试阶段失败。
- 修复知识库物理删除 API 当前的生产 500。
- 不修改 API 合同、数据库结构或迁移。

## 非目标与约束

- 不机械删除所有 `ExecuteUpdateAsync`；原子条件更新仍是部分 Worker 抢占路径的必要机制。
- 不引入第二套通用原生 SQL 持久化框架。
- 不使用容器。
- 不向正式 MySQL 写测试数据，也不创建或删除正式服务器上的测试库。
- 本地 Provider 边界测试不能宣称等同于真实 MySQL 运行时验收。
- 不改变 Qdrant、WorkTool、消息回调或知识库 HTTP 合同。

## 已识别的审计范围

批量更新与删除调用当前分布如下：

| 文件 | 调用数 | 主要职责 |
| --- | ---: | --- |
| `WechatRobot.Api/WorkTool/WorkToolGroupOperationEndpoints.cs` | 2 | WorkTool 管理操作重试 |
| `WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs` | 13 | 消息归属、会话租约和终态 |
| `WechatRobot.Infrastructure/Groups/EfGroupLifecycleStore.cs` | 1 | 群生命周期 |
| `WechatRobot.Infrastructure/Knowledge/KnowledgeCandidatePublishProcessor.cs` | 1 | 候选知识发布 |
| `WechatRobot.Infrastructure/Knowledge/QdrantKnowledgeService.cs` | 12 | 索引任务、激活、禁用和租约 |
| `WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs` | 23 | Durable Job 抢占、完成、失败和恢复 |
| `WechatRobot.Infrastructure/Persistence/EfHandoffStore.cs` | 1 | 人工接管状态 |
| `WechatRobot.Infrastructure/Persistence/KnowledgeDocumentStore.cs` | 13 | 上传、重试、物理删除和清理重投 |
| `WechatRobot.Worker/Jobs/WorkToolGroupOperationWorker.cs` | 5 | WorkTool 命令投递状态机 |
| `WechatRobot.Worker/Jobs/WorkToolGroupReconciliationWorker.cs` | 3 | WorkTool 对账状态机 |

运行时 Guid 查询审计同时覆盖 `src/server` 全部 EF 查询，不局限于以上 10 个文件。纯内存过滤、Qdrant 请求载荷和已经使用 `GuidBatchQuery` 的查询不应被机械替换。

## 方案选择

### 采用：风险分级修复

每个审计点必须归入以下一种类别：

1. **改用 `GuidBatchQuery`**：运行时 Guid 集合需要进入 MySQL 查询时，使用 `CreateBatches` 和 `BuildPredicate`，每批最多 100 个 ID，汇总后恢复原有顺序。
2. **改用跟踪实体转换**：bulk setter 清空可空字段、使用可空运行时值，或已经观察到 Provider 参数绑定失败时，加载有界实体并通过 `SaveChangesAsync` 更新。
3. **保留原子批量更新**：任务抢占、租约获取等必须使用单条条件更新保证 CAS，并且 setter 只包含已经验证的非空、稳定类型表达式。每个保留点必须由自动化白名单和行为测试覆盖。
4. **确认不进入 EF SQL**：纯内存 `Contains` 等操作记录为安全，不改变代码。

不采用“全部改跟踪实体”，因为会破坏 Worker 的原子抢占并增加 N+1；不采用“全部改原生 SQL”，因为会复制 EF 映射并扩大字段漂移和维护风险。

## 状态变更设计

### 跟踪实体转换

高风险批量转换使用以下统一流程：

1. 在现有事务边界内有界加载目标实体。
2. 验证当前状态、所有权和 `Version` 或 `StateVersion`。
3. 修改跟踪实体，包括明确清空可空字段。
4. 调用 `SaveChangesAsync(cancellationToken)`。
5. 将 `DbUpdateConcurrencyException` 映射回该模块原有的冲突、抢占失败或重试结果。
6. 保持审计、幂等键和任务入队与状态修改处于原有事务语义内。

知识库物理删除请求应复用并强化现有 `RequestPhysicalDeleteTrackedAsync` 语义，不再让 MySQL 分支执行包含 `ActiveVersionId = null`、租约字段清空等操作的多段 `ExecuteUpdateAsync`。状态版本冲突仍返回 409；成功仍返回 202 并异步入队清理任务。

### 原子批量更新

原子抢占和租约获取可以保留 `ExecuteUpdateAsync`，条件必须包含现有状态、所有者或版本令牌，返回行数继续决定是否抢占成功。保留表达式不得包含：

- `null` 常量 setter；
- 可空运行时变量 setter；
- 运行时 Guid 集合参数；
- 未由 Provider 边界测试覆盖的新类型转换。

当一次转换既需要原子抢占又需要清空可空字段时，拆为两个有版本保护的阶段：先用安全的非空原子更新取得所有权，再加载已归属当前所有者和版本的实体完成清理。第二阶段遇到并发冲突时使用现有重试或失败语义，不静默覆盖。

## Guid 查询设计

凡运行时 Guid 集合进入 EF SQL：

- 使用 `GuidBatchQuery.CreateBatches`；
- 每批最多 100 个 ID；
- 使用 `GuidBatchQuery.BuildPredicate` 生成显式等值 OR；
- 禁止每个 Guid 单独查询；
- 批次结果合并后恢复原有确定性排序；
- 继续传递原始 `CancellationToken`。

如果 `Contains` 仅对已加载到内存的数组、字典或 HashSet 执行，则保留并在审计清单中标记为“内存操作”。

## 错误处理和可观测性

- API 层继续把领域并发冲突映射为现有 409，不把 Provider 异常伪装成未找到或业务未命中。
- Worker 继续使用现有 Durable Job 重试、失败和 dead-letter 规则。
- fallback 捕获候选或查询异常时必须产生结构化失败日志或既有失败代码，禁止无痕降级。
- 日志保持现有脱敏边界，不输出 SQL 参数值、连接字符串、文档内容或凭据。
- 本次不通过增加宽泛异常捕获来隐藏 500；根因在持久化表达式边界修复。

## 测试设计

### Red-green 回归

实施前先添加失败测试，证明当前代码违反以下边界：

- EF 查询表达式包含运行时 `Enumerable.Contains<Guid>` 或 `Queryable.Contains<Guid>`；
- `ExecuteUpdateAsync` setter 包含 `null`、可空捕获变量或禁止的类型映射；
- 跟踪实体转换丢失原有版本条件、事务、审计或幂等行为；
- fallback 捕获 Provider 异常后没有结构化失败证据。

### Provider 边界模拟

复用仓库已有的查询表达式拦截模式，构造一个拒绝上述危险表达式的测试 Provider 边界。测试必须执行真实业务仓储方法，而不是只断言辅助函数或 mock 调用次数。

对于保留的非空原子 `ExecuteUpdateAsync`，测试应证明：

- 只有满足状态和版本条件的一行被更新；
- 两个竞争执行者只有一个取得租约；
- 更新表达式不包含禁止 setter；
- 失败时保持原有返回或重试语义。

### 全库静态守卫

新增后端 Provider 兼容性契约测试，扫描 `src/server`：

- 记录并核验全部 bulk update/delete 调用；
- 对保留的原子批量更新使用精确、可审查的白名单；
- 禁止新增包含可空 setter 的 bulk update；
- 禁止新增进入 EF SQL 的运行时 Guid 集合 `Contains`；
- 白名单中的文件或表达式发生变化时必须重新审核，测试不能自动放宽。

静态守卫用于防回归，不替代行为测试。

### 验证集合

- 后端 UnitTests；
- ContractTests；
- 不依赖 MySQL 容器的 IntegrationTests；
- 前端类型检查、Vitest 和构建，确认 API 合同未改变；
- `git diff --check`；
- 全库 Provider 风险扫描。

MySQL Testcontainers 测试不在本地验收中运行。发布后需要使用新 API/Worker 二进制和真实业务链路验证物理删除、Worker 心跳、消息处理、索引任务及 WorkTool 状态机，并从新日志确认没有 `CreateDbCommand` 或 `AddDbParameter` 异常。

## 交付与发布

- 代码按模块分批提交，最终提交到当前 `master`。
- 生成新的 API 和 Worker 发布包及压缩包。
- 不需要数据库迁移。
- 推送到当前 Gitee `origin/master`。
- 发布前明确区分“本地模拟 Provider 验证通过”和“正式 MySQL 运行时尚待部署验收”。

## 验收标准

- 74 个现有 bulk update/delete 调用全部有审计结论，没有未分类项。
- `src/server` 中不存在未经审计的运行时 Guid 集合 EF 查询。
- 物理删除请求不再执行高风险 nullable bulk setter，且保持 202/409 合同。
- Worker 原子抢占、租约、重试、dead-letter 和幂等语义不变。
- 自动化守卫能在重新引入危险模式时失败。
- 所有约定的非容器测试、前端检查和差异检查通过。
- 发布产物包含 API 与 Worker，不包含 `.local`、密钥、日志或测试数据。
- 正式环境验收前不宣称已经通过真实 MySQL 验证。
