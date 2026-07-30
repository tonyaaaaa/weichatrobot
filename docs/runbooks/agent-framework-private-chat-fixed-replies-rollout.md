# Agent Framework、私聊知识入库与固定回复上线手册

## 适用范围

本手册用于上线群消息意图判断、固定回复模板、WorkTool 私聊问答、私聊直接知识
入库，以及按回答来源逐步切换到 Microsoft Agent Framework。

API 与 Worker 必须使用同一份 `AgentRuntime` 配置。修改后必须同时重启 API 和
Worker；运行中的进程不会自动重新加载。

## 上线前置条件

1. 备份 MySQL，并确认目标数据库。
2. 应用 `20260729052252` 至 `20260729071906` 的新增迁移。
3. 默认聊天模型已启用、连接测试成功，并通过 Function Tool 与工具结果循环探针。
4. readiness 中 MySQL、Qdrant、OSS 和 Worker 满足必需状态；OCR 按部署配置判断。
5. WorkTool 消息回调和指令结果回调分别验证；公网回调地址必须为 HTTPS。

## 运行模式

| 配置 | 合法值 | 初始建议 | 作用 |
| --- | --- | --- | --- |
| `IntentRuntimeMode` | `Legacy`、`Shadow`、`AgentFramework`、`Paused` | `Legacy` | 控制群消息是否进入正式回答链路 |
| `AnswerRuntimeMode` | `Legacy`、`Shadow`、`AgentFramework` | `Legacy` | 控制回答生成实现 |
| `PrivateChatRuntimeMode` | `Disabled`、`AgentFramework` | `AgentFramework` | 控制私聊问答和入库 |
| `TemplateRoutingRuntimeMode` | `Disabled`、`Shadow`、`AgentFramework` | `AgentFramework` | 控制固定模板识别 |

推荐上线顺序：

1. 首次部署：Intent=`Legacy`、Answer=`Legacy`、PrivateChat=`Disabled`、
   TemplateRouting=`Disabled`。
2. 模板验收：只将 TemplateRouting 切换为 `AgentFramework`。
3. 私聊验收：只将 PrivateChat 切换为 `AgentFramework`。
4. 意图观察：Intent=`Shadow`，正式回复仍走旧逻辑；在“智能回复诊断”查看结果。
5. 授权测试群验证完成后，才将 Intent 切换为 `AgentFramework`。
6. 各回答来源完成等价验证后，再将 Answer 切换为 `AgentFramework`。

`Paused` 是意图正式接管后的安全停止模式：保存入站消息和诊断记录，但不调用模板、
知识检索或发送回复。它不会删除会话、知识或队列数据。

## 固定回复模板

管理员可在“固定回复模板”页面维护模板，也可在群详情中管理该群的包含或排除关系。
指定群模板优先于全局模板；已排除的群不会使用该模板。模板命中后仍重新校验启用
状态、版本和群作用域，不允许 Agent 直接读取数据库或绕过服务层。

上线验收：

1. 创建一个只作用于测试群的模板并启用。
2. 使用“测试匹配”确认明确问法命中。
3. 使用相近但不满足模板语义的问题，确认继续进入知识库问答。
4. 在会话审计确认回答来源为“固定回复模板”。
5. 停用模板并重复问题，确认不再命中。

模板采用停用留档，不物理删除；历史审计保留模板 ID 和版本。

## 私聊问答与直接入库

- WorkTool `roomType=2` 和 `roomType=4` 进入私聊处理。
- 普通私聊问题默认检索所有启用知识标签。
- 只有以单独一行 `#知识入库` 开头的消息进入直接入库，其他消息按普通问答处理。
- Agent 只能从服务端提供的候选标签中选择；没有合适标签时进入全局知识。
- 拆分、相似比较、索引和激活由 Worker 完成，回调请求只做持久化和入队。

在“私聊知识入库”页面查看批次。失败批次可受控重试；旧知识版本只有在新向量全部
写入成功后才失活，因此索引失败不会覆盖当前有效知识。重复回调必须只生成一个私聊
消息、一个处理任务和一个入库批次。

## 诊断、灰度和回退

“智能回复诊断”只展示分类、置信度、原因码、失败码、运行模式、模型配置版本和耗时，
不展示提示词、模型原始响应或凭据。

Intent 从 `Shadow` 切换到 `AgentFramework` 前，至少满足：

- 测试群人工标注样本中无明显扩大回复范围；
- 不确定、超时、无效工具结果均安全地不回复；
- 固定模板与 RAG 降级路径均通过；
- Durable Job 与发送队列没有异常积压；
- 运维人员已验证 `Paused` 生效。

异常回退：

1. Intent 已正式接管：改为 `Paused`，同时重启 API 与 Worker。
2. 模板异常：改为 `Disabled`，或停用具体模板。
3. 私聊异常：改为 `Disabled`；已创建批次保留，不删除。
4. Answer 异常：改回 `Legacy`；不得清空会话、Qdrant 或发送队列。

## 数据保留

回退不删除固定回复、私聊批次、来源沿革、意图审计、检索审计和历史人工转接表。
人工转接运行功能已经移除时，其历史表仍按只读留档处理。

## 验收清单

- API、Worker 构建和后端单元、合约、集成测试通过；
- 前端类型检查、Vitest、生产构建和关键 E2E 通过；
- API liveness、readiness、Worker 心跳和前端首页正常；
- 明确模板命中、模糊问题走 RAG；
- 私聊普通问答、直接入库、重复回调幂等、索引失败保护通过；
- Intent Shadow 不改变正式发送，AgentFramework 失败不回复，Paused 停止新回复；
- `git diff --check` 和敏感信息检查通过。
