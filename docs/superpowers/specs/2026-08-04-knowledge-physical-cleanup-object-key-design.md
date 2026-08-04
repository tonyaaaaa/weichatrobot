# 知识库物理清理对象键兼容修复设计

## 背景

正式库中的私聊直接入库版本使用 `private-chat/...` 作为 `ObjectKey`，而 OSS 实现只允许删除 `wechatrobot/` 前缀下的对象。物理清理 Worker 当前会删除所有非空 `ObjectKey`，因此即使私聊知识从未上传 OSS，也会调用对象存储删除并触发安全校验。任务连续失败四次后进入 `deadLetter`，管理端显示“物理清理失败”。

只读审计同时发现：3 个 `PrivateChatDirect` 版本使用 `private-chat/` 前缀，其中2个已申请物理删除；另有1个 `LegacyUnknown` 版本使用 `reviewed/` 前缀。

## 目标

- 新建的私聊直接入库版本使用满足对象键安全约束的命名空间。
- 物理清理只删除确实存在于公共对象存储中的文件。
- 历史伪对象键不阻断数据库记录和向量数据清理。
- 保留 OSS 的 `wechatrobot/` 前缀防护，不放宽对象存储安全边界。
- 部署后可以安全重投现有失败清理任务。

## 非目标

- 不批量改写历史 `ObjectKey`。
- 不为从未上传 OSS 的私聊知识创建或复制对象。
- 不修改数据库结构或新增迁移。
- 不改变物理删除 API 的 202/409 合同。

## 设计

### 新数据对象键

`PrivateKnowledgeIngestProcessor` 创建版本时，将对象键从：

```text
private-chat/{batchId}/{sequence}
```

改为：

```text
wechatrobot/private-chat/{batchId}/{sequence}
```

这使所有新版本满足统一对象键约束，但不会因此主动上传 OSS。

### 真实对象判定

`KnowledgeDocumentCleanupWorker` 加载待删除对象时，不再使用“`ObjectKey` 非空”作为上传成功的判断。只有版本具有非空 `PublicUrl` 时，才把其 `ObjectKey` 交给 `IObjectStorage.DeleteAsync`。

`PublicUrl` 是现有上传成功流程写入的持久化证据。私聊直接入库和历史审核数据通常只有 `StagedContent` 或索引数据，没有 `PublicUrl`，因此跳过 OSS 删除，但仍继续执行：

1. 等待知识索引任务排空；
2. 删除专属 Qdrant 集合或版本向量；
3. 验证向量清理结果；
4. 删除 MySQL 中的版本和文档记录；
5. 完成 Durable Job。

### 历史数据兼容

不修改 `private-chat/...`、`reviewed/...` 等历史伪对象键。因为这些键没有对应公共对象，批量添加前缀会制造虚假的 OSS 地址，并可能掩盖数据来源。

历史版本只要 `PublicUrl` 为空，就会自然跳过 OSS 删除。已有 `deadLetter` 任务通过现有“重新提交物理清理”入口重置为 `pending`，不直接操作任务表。

### 安全边界

- `AliyunOssStorage` 和 `LoopbackObjectStorage` 的 `wechatrobot/` 前缀校验保持不变。
- 若版本存在 `PublicUrl`，但 `ObjectKey` 不满足前缀约束，清理仍失败并进入重试；不能静默跳过真实公共对象，否则会造成 OSS 文件残留。
- Worker 继续使用现有四次失败后进入 `deadLetter` 的规则。

## 测试

采用 TDD 增加以下回归覆盖：

1. 私聊直接入库新版本的 `ObjectKey` 以 `wechatrobot/private-chat/` 开头。
2. 没有 `PublicUrl` 的历史 `private-chat/` 版本执行物理清理时，不调用对象存储删除，但会完成向量和数据库清理。
3. 有 `PublicUrl` 的普通上传版本仍调用对象存储删除。
4. 有 `PublicUrl` 且对象键非法时仍失败，证明安全校验没有被绕过。
5. 运行相关 IntegrationTests、完整 UnitTests、ContractTests 和后端 Release publish。

## 发布与恢复

本次不需要数据库迁移。API 和 Worker 都重新发布：API 包含重投入口相关持久化代码，Worker 包含清理行为修复。

部署新版本并确认 Worker 心跳后，对现有失败记录使用管理端“重新提交物理清理”。验收要求：任务不再回到 `deadLetter`，目标文档从 MySQL 消失，相关向量不存在，日志中没有对象键前缀异常。
