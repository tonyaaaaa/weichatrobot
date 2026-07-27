# Embedding 模型维度与索引契约设计

## 目标

向量维度归属于 Embedding 模型配置。聊天模型不配置维度；Embedding 模型必须配置正整数维度，连接测试必须用实际返回向量长度校验该值。

## 数据与接口

- `ModelConfigEntity` 增加可空 `EmbeddingDimension`：聊天配置必须为空，Embedding 配置必填。
- 模型配置创建、更新、列表和详情契约增加 `embeddingDimension`。
- 模型配置指纹包含维度，修改维度后原连接测试失效。
- Embedding 连接测试返回长度不一致时保存稳定摘要 `dimension_mismatch_expected_{n}_actual_{m}`，且配置不能启用或设为默认。

## 索引一致性

- 删除 `KnowledgeIndex.Dimension`，保留距离、批量大小、重试次数和搜索集合上限。
- 建立索引时选择当前启用且优先默认的 Embedding 配置，将 `ModelConfigurationId`、配置版本和维度快照到索引任务。
- Worker 使用任务绑定的模型配置，不在任务执行过程中重新选择默认模型。
- 模型配置已变更、停用或删除时，旧任务失败并要求重新排队。
- Qdrant collection 使用任务维度命名和创建，搜索继续支持不同维度 collection 的并存与迁移。

## 迁移与现有数据

- 新列保持可空，避免把未知旧模型错误迁移成 1536。
- 当前本地 `qwen3.7-text-embedding` 在部署后显式配置为 1024 并重新测试。
- 旧失败任务不直接重试；重新建立索引时以新的模型 ID、版本和 1024 维重写任务快照。

## 非目标

- 不把向量距离算法放入模型配置。
- 不自动覆盖管理员填写的维度。
- 不向所有 OpenAI 兼容供应商发送非通用的 `dimensions` 请求字段。
