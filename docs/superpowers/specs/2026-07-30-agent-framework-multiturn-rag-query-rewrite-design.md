# Agent Framework 私聊与群聊多轮 RAG 查询改写设计

## 1. 背景

WechatRobot 已经具备群聊和私聊短期会话、会话摘要、知识范围解析、Embedding、
Qdrant 检索、相似度阈值、证据防火墙、回答审计和 Agent Framework
`AnswerAgent` 接入点。

当前多轮检索行为并不一致：

- 群聊通过 `RetrievalQueryBuilder` 将当前问题、会话摘要和最近消息按文本拼接后
  执行向量检索。
- 私聊会构建短期会话上下文，但普通知识检索没有生成上下文化的
  `RetrievalQuery`，检索可能只收到当前问题。
- Agent Framework 当前用于模型执行边界，不会自动把“需要什么材料”改写为
  “办理日本三年签证需要准备什么材料”。

因此，模型最终回答虽然可能看见历史消息，知识检索本身仍可能丢失上一轮主题。
本设计为私聊和群聊增加统一的、可审计的多轮 RAG 查询改写能力。

本设计延续以下既有设计，不改变其中的数据和权限真相：

- `2026-07-29-agent-framework-intelligent-reply-migration-design.md`
- `2026-07-29-private-chat-direct-knowledge-and-fixed-reply-agent-design.md`
- `2026-07-28-group-lifecycle-and-context-management-design.md`

## 2. 已确认决策

1. MySQL 会话、消息和摘要继续作为唯一业务真相。
2. 不持久化另一套完整 Agent Framework 会话历史。
3. `AgentSession` 仅用于当前 Agent 调用的运行状态。
4. 私聊和群聊都支持多轮 RAG 查询改写。
5. 每次普通 RAG 问答都调用查询改写 Agent；完整独立问题允许原样返回。
6. 指代不清或存在多个可能主题时先澄清，不猜测、不执行 RAG。
7. 使用 Agent Framework 官方 Agent 和 Context Provider 扩展点，但保留现有
   Qdrant、知识权限、相似度阈值和审计编排。
8. 群聊查询改写完全复用现有上下文范围、历史轮数、空闲超时、Token 上限、
   摘要和机器人历史配置。
9. 第一轮 RAG 原始证据只在第一轮回答调用中有效，不进入后续普通会话上下文。
10. 每一轮根据改写后的问题重新执行 Embedding 和 Qdrant 检索。
11. 不新增或修改 `appsettings` 配置项。
12. 不增加查询改写专用运行模式、模型配置、超时或长度配置。

## 3. 目标

- 将依赖上下文的追问改写为可独立理解的完整检索问题。
- 让群聊和私聊共享同一个 Application 层改写合同和验证规则。
- 继续按频道使用不同会话作用域和知识授权范围。
- 在歧义场景中生成安全澄清问题，并阻止错误知识检索。
- 保持检索、权限、阈值、回答降级和发送流程由服务端确定性控制。
- 对改写决定、上下文来源、模型和失败原因提供可追踪审计。
- 保持现有群配置界面和系统 `appsettings` 不变。

## 4. 非目标

- 不让 Agent 自由决定是否绕过知识库、改用 Web Search 或模型自身知识。
- 不把 Qdrant 直接暴露为 AnswerAgent 可任意调用的 Function Tool。
- 不在本期迁移现有 Qdrant 索引到新的 VectorData 数据模型。
- 不将 Agent Framework `TextSearchProvider` 的历史拼接直接视为语义改写。
- 不把上一轮检索证据永久写入 AgentSession 或普通会话上下文。
- 不新增群级或私聊级查询改写开关。
- 不新增查询改写管理页面。
- 不改变知识文档、版本、切片、激活、删除或标签授权规则。
- 不改变固定回复、私聊知识入库、长期记忆或 WorkTool 发送合同。

## 5. 官方 Agent Framework RAG 模式与选型

Agent Framework 官方通过 `AIContextProvider` 为 Agent 增加 RAG。内置
`TextSearchProvider` 支持：

- `BeforeAIInvoke`：每次 Agent 调用前自动搜索并注入结果。
- `OnDemandFunctionCalling`：将搜索暴露为 Function Tool，由模型决定调用。
- `RecentMessageMemoryLimit`：将有限最近消息加入搜索输入。
- 自定义 Search Adapter：搜索实现可连接任意检索技术。

官方 VectorData 抽象支持 Qdrant，但内置多轮行为主要是将最近消息加入搜索输入，
不保证生成一个可独立理解的语义查询。按需 Function Tool 可以由模型生成检索词，
但会同时把检索时机、次数和参数交给模型。

本系统采用框架原生、业务受控的组合方式：

- 使用 Agent Framework `ChatClientAgent` 实现 `QueryRewriteAgent`。
- 使用受控 `KnowledgeEvidenceProvider` 将服务端已经授权、裁剪和净化的证据注入
  `AnswerAgent`。
- 继续使用现有 `IRetrievalEvidenceProvider`、Embedding 客户端和 Qdrant
  检索服务。
- 检索范围、阈值、降级和审计仍由确定性 Application Orchestrator 决定。

## 6. 架构

### 6.1 组件

新增 Application 层合同：

```text
IQueryRewriteAgent
  RewriteAsync(QueryRewriteRequest, CancellationToken)
    -> QueryRewriteResult
```

新增统一编排服务：

```text
MultiTurnRetrievalService
  1. 接收当前问题和已经构建好的 ConversationContextResult
  2. 调用 IQueryRewriteAgent
  3. 校验结构化输出
  4. 决定 Search、Clarification 或 Failure
  5. Search 时调用现有 IRetrievalEvidenceProvider
  6. 返回改写结果、证据和审计草稿
```

新增 Agent Framework 适配器：

```text
QueryRewriteAgent
  - 位于 Infrastructure
  - 使用 IAgentChatClientFactory
  - 使用结构化输出
  - 不依赖 EF Core、Qdrant、Web Search 或业务写工具
```

新增受控上下文提供者：

```text
KnowledgeEvidenceProvider
  - 基于 Agent Framework AIContextProvider
  - 只接收服务端已授权的 RetrievalEvidence
  - 将净化证据注入当前 AnswerAgent 调用
  - 不直接搜索或修改知识
```

Domain 和 Application 层不得暴露 `ChatClientAgent`、`AgentSession`、
`AIContextProvider` 等框架类型。

### 6.2 会话真相

- `ConversationSession`、`ConversationMessage` 和会话摘要是唯一业务真相。
- 每次调用前通过现有 `ConversationContextService` 构建受控上下文。
- Agent Framework 不维护第二套不可解释的完整历史。
- `AgentSession` 不作为进程重启后的恢复来源。
- 私聊和群聊不得共享 AgentSession、摘要、上下文或改写状态。

## 7. 查询改写合同

### 7.1 输入

`QueryRewriteRequest` 至少包含：

- `MessageId`
- `ConversationSessionId`
- `ChannelType`: `Group` 或 `Private`
- `GroupProfileId`: 群聊存在，私聊为空
- `RobotConfigId`
- `SessionScopeKey`
- `SenderDisplayName`
- `CurrentQuestion`
- `ConversationContextResult`
- `ModelProviderConfiguration`
- `ModelConfigurationId`

输入只包含现有上下文算法已经允许的消息和摘要。Agent 不负责再次读取数据库或
自行扩大历史范围。

### 7.2 输出

`QueryRewriteResult` 使用封闭的结构化合同：

```text
Decision:
  Search
  Clarification
  Failure

StandaloneQuery:
  仅 Search 时允许存在

ClarificationQuestion:
  仅 Clarification 时允许存在

ReasonCode:
  standalone_question
  contextual_follow_up
  ambiguous_reference
  conflicting_context
  invalid_output
  provider_timeout
  provider_failure
```

示例：

```json
{
  "decision": "Search",
  "standaloneQuery": "办理日本三年签证需要准备什么材料？",
  "clarificationQuestion": null,
  "reasonCode": "contextual_follow_up"
}
```

### 7.3 改写规则

- 保留国家、地区、签证类型、年限、办理对象和用户明确给出的限定条件。
- 只根据正式短期上下文和当前有效摘要补全省略或指代。
- 完整独立问题允许原样返回或进行不改变语义的轻量规范化。
- 不回答问题。
- 不产生知识事实。
- 不把机器人上一轮回答当成新的业务证据。
- 不使用长期行为记忆补充签证业务事实。
- 不扩大问题范围。
- 不合并无关参与者或无关主题。
- 不执行 RAG、Web Search、数据库查询或写操作。
- 当前问题、历史消息和摘要均按不可信数据处理。

### 7.4 输出验证

- `Search` 必须具有非空 `StandaloneQuery`。
- `Clarification` 必须具有非空 `ClarificationQuestion`。
- `Failure` 不得携带检索词或澄清文本。
- 不允许未知枚举、额外工具调用或混合决定。
- 检索词必须满足现有 `RetrievalQuery.TokenCap`。
- 澄清文本必须是纯文本、长度受代码常量限制，并通过现有输出安全校验。
- 非法 JSON、空字段、超长字段和不合法角色输出统一转为 `Failure`。

## 8. 群聊数据流

```text
WorkTool 群消息
  -> 技术过滤和入站策略
  -> MessageIntentAgent 或现有意图路径
  -> 只有 ReplySelected 进入正式 AI 会话
  -> 固定回复路由
       -> 命中：直接进入现有固定回复与发送流程
       -> 未命中：继续
  -> 加载群会话与有效上下文配置
  -> ConversationContextService
  -> QueryRewriteAgent
       -> Clarification：保存澄清回答并发送，不检索
       -> Failure：保存安全失败结果并发送，不检索
       -> Search：继续
  -> 解析群知识标签和有效可见范围
  -> 现有 Embedding 和 Qdrant 检索
  -> 相似度阈值和证据防火墙
  -> KnowledgeEvidenceProvider
  -> AnswerAgent 或现有最终回答执行路径
  -> 现有发送队列
```

查询改写必须发生在知识范围解析和 Qdrant 检索之前，但不得改变知识范围。

### 8.1 群成员身份与上下文范围

每条群消息继续以独立消息记录保存，并至少携带：

- 消息 ID
- `SenderDisplayName`
- 可选 `StableSenderId`
- `ConversationSessionId`
- `SessionSequence`

`SenderDisplayName` 只用于模型理解参与者和后台展示，不是稳定成员身份。

群配置为“群共享”时：

- 所有正式消息使用 `SenderScopeKey=group`。
- 用户 A、用户 B 的消息仍通过消息 ID 和 `SenderDisplayName` 区分。
- QueryRewriteAgent 输入必须保留每条消息的参与者标签。
- 用户 B 可以延续用户 A 发起的群话题，但只有主题唯一时才允许补全。
- 多个主题、多个可能指代或参与者归属不清时必须返回澄清。

群配置为“成员隔离”时：

- 使用连接器提供的有效 `StableSenderId` 哈希生成成员会话范围。
- 用户 A、用户 B 使用不同 `SenderScopeKey`。
- 用户 B 不得读取或继承用户 A 的正式历史、摘要或改写主题。
- 用户 B 只有短追问但自己的上下文无法补全时，必须返回澄清。
- 不允许使用显示名称代替稳定 ID 合并成员会话。

成员隔离模式下如果连接器没有提供有效 `StableSenderId`，系统继续按现有规则为
每条消息生成 `stateless:{MessageId}` 作用域：

- 不会错误合并同名或不同名成员。
- 当前消息不继承任何上一轮成员历史。
- 本轮 QueryRewriteAgent 按无历史问题处理。
- 审计记录 `stable_sender_id_unavailable` 降级原因。

该降级优先保证不串话，不声称具备可靠成员多轮能力。

### 8.2 Intent 与查询改写边界

`MessageIntentAgent` 和 `QueryRewriteAgent` 是两个顺序执行、职责不同的阶段：

```text
同群原始消息窗口
  -> MessageIntentAgent：只判断是否对机器人说话
  -> ReplySelected
  -> 正式会话作用域和有效上下文
  -> QueryRewriteAgent：只补全 RAG 检索意图
```

Intent Agent 可以读取有限的同群原始消息窗口和参与者标签，用于识别“继续机器人
上一轮”“成员之间对话”等意图。该原始窗口不得直接传给 QueryRewriteAgent，
也不得绕过群共享或成员隔离策略成为正式 RAG 上下文。

运行模式语义保持现状：

- `Legacy`：不由 Intent Agent 正式拦截，符合现有入站策略的消息继续后续流程。
- `Shadow`：记录 Intent 判断差异，但不改变当前正式处理结果。
- `AgentFramework`：只有 `Reply` 进入正式会话、查询改写和 RAG；
  `NoReply`、`Uncertain` 和失败关闭结果不得进入。
- `Paused`：消息终止，不进入查询改写或 RAG。

成员隔离的交界场景必须按正式上下文处理：

```text
用户 A：日本三年签证能办理吗？
机器人：可以办理。
用户 B：那需要什么材料？
```

Intent Agent 可能根据同群原始窗口判断 B 正在继续机器人对话，但如果群配置为
成员隔离，QueryRewriteAgent 只能读取 B 自己的正式上下文。B 没有可用主题时必须
返回澄清，不能继承 A 的“日本三年签证”主题。

## 9. 私聊数据流

```text
WorkTool 私聊消息
  -> 回调鉴权、幂等入站和 Durable Job
  -> 建立或续接 ScopeHash 私聊会话
  -> 私聊命令解析
       -> 知识入库命令：进入现有入库流程，不改写
       -> 普通问题：继续
  -> 私聊固定回复路由
       -> 命中：直接回复，不改写
       -> 未命中：继续
  -> ConversationContextService
  -> QueryRewriteAgent
       -> Clarification：保存澄清回答并发送，不检索
       -> Failure：保存安全失败结果并发送，不检索
       -> Search：继续
  -> 解析全部启用且可检索的已发布知识范围
  -> 现有 Embedding 和 Qdrant 检索
  -> 相似度阈值和证据防火墙
  -> KnowledgeEvidenceProvider
  -> AnswerAgent
  -> 现有发送队列
```

私聊不得读取群会话、群知识绑定或其他 `ScopeHash` 的历史。

## 10. 现有上下文配置

本设计不新增第二套上下文参数。

群聊 QueryRewriteAgent 完全复用群配置中的有效值：

| 现有配置 | 查询改写语义 |
| --- | --- |
| 上下文范围 | 决定群共享或成员隔离 |
| 历史轮数 | 决定可进入改写输入的最近用户轮次 |
| 空闲超时 | 超时后旧历史和旧摘要不进入改写 |
| Token 上限 | 限制消息和摘要的受控输入 |
| 摘要 | 允许恢复被裁剪历史中的主题和指代 |
| 机器人历史 | 决定机器人最终回答是否进入改写上下文 |

`ConversationContextService` 继续作为生产回答、上下文预览和查询改写的共同选择
算法。不得在 QueryRewriteAgent 内复制历史轮数、空闲超时或 Token 裁剪逻辑。

私聊继续使用当前既有短期上下文规则和 `ScopeHash` 隔离，不新增私聊配置页面。

以下内容不计入历史轮数和上下文 Token 上限：

- RAG 原始证据正文
- Qdrant 向量或相似度内部数据
- 检索审计明细
- 固定回复候选列表
- Agent 内部提示词和结构化中间输出

## 11. 两轮检索示例

### 11.1 第一轮

```text
用户：日本三年签证你们能办吗？
```

QueryRewriteAgent：

```text
Decision: Search
StandaloneQuery: 日本三年签证是否可以办理？
ReasonCode: standalone_question
```

系统执行第一轮 Embedding 和 Qdrant 检索，使用第一轮证据生成：

```text
机器人：可以办理。
```

会话保存用户问题和机器人最终回答。第一轮原始证据只保存在检索审计中。

### 11.2 第二轮

在“机器人历史=纳入”、历史轮数和空闲超时均允许时，改写输入包含：

```text
用户：日本三年签证你们能办吗？
机器人：可以办理。
当前用户：需要什么材料？
```

QueryRewriteAgent：

```text
Decision: Search
StandaloneQuery: 办理日本三年签证需要准备什么材料？
ReasonCode: contextual_follow_up
```

系统对该独立问题重新执行 Embedding 和 Qdrant 检索。第二轮 AnswerAgent 只接收
第二轮检索得到的有效证据，不自动接收第一轮原始证据。

### 11.3 歧义追问

```text
用户：日本三年签证和五年签证都能办吗？
机器人：都可以。
用户：那个需要什么材料？
```

QueryRewriteAgent：

```text
Decision: Clarification
ClarificationQuestion: 请确认您咨询的是日本三年签证还是五年签证？
ReasonCode: ambiguous_reference
```

本轮不调用 Embedding 或 Qdrant。澄清问题作为机器人正式回答进入会话。用户确认
“三年签证”后，下一轮重新改写并检索。

## 12. 证据生命周期

RAG 证据只在产生它的当前回答调用中有效：

```text
本轮用户问题
  -> 本轮改写问题
  -> 本轮检索证据
  -> 本轮 AnswerAgent
  -> 本轮最终回答
```

本轮完成后：

- 用户问题和机器人最终回答进入正式会话。
- 检索证据 ID、文档版本、切片、来源和相似度进入检索审计。
- 原始证据正文不进入后续普通会话上下文。
- 下一轮必须重新应用当前知识权限、文档激活状态和相似度阈值。
- Qdrant 或知识依赖不可用时，不得静默使用上一轮旧证据。

该规则避免上下文持续膨胀、旧文档污染、权限变化后继续携带旧证据，以及把上一轮
检索命中误当成当前问题证据。

## 13. 配置

本功能不新增或修改 `appsettings`。

明确不新增：

```text
GroupQueryRewriteRuntimeMode
PrivateQueryRewriteRuntimeMode
QueryRewriteModelConfigurationId
QueryRewriteTimeoutSeconds
QueryRewriteMaximumInputCharacters
QueryRewriteMaximumOutputCharacters
```

复用规则：

- 改写使用本次回答已经解析出的聊天模型配置和 `ModelConfigurationId`。
- 超时使用该模型现有 `TimeoutSeconds`。
- 输入上下文使用现有群配置或当前私聊上下文规则。
- 改写查询长度使用现有 `RetrievalQuery.TokenCap`。
- 现有模型 API Key 解密和 typed client 边界保持不变。

现有 `AgentRuntime` 配置含义不改变：

- `IntentRuntimeMode` 只控制群消息意图判断。
- `AnswerRuntimeMode` 只控制最终回答执行使用 Legacy 或 Agent Framework。
- `PrivateChatRuntimeMode` 继续控制私聊能力是否启用。
- `TemplateRoutingRuntimeMode` 只控制固定回复路由。

查询改写是普通 RAG 的固定前置步骤，不增加独立关闭开关。即使群最终回答仍处于
`AnswerRuntimeMode=Legacy`，进入普通 RAG 的问题也先经过 QueryRewriteAgent。

## 14. 失败与降级

| 场景 | 行为 |
| --- | --- |
| 没有历史的第一轮改写失败 | 使用当前原始问题继续检索 |
| 存在有效历史时改写超时 | 不检索，返回安全失败提示 |
| 存在有效历史时模型不可用 | 不检索，返回安全失败提示 |
| 非法结构化输出 | 不检索，记录 `invalid_output` |
| 检索词为空或超长 | 不检索，记录 `invalid_output` |
| 指代不清 | 返回澄清问题，不检索 |
| 多个主题冲突 | 返回澄清问题，不检索 |
| 澄清文本不安全 | 使用服务端固定澄清文案 |
| 用户取消或 Worker 停止 | 传播 CancellationToken，不伪造结果 |

存在有效历史时不得降级为当前问题单独检索，也不得降级为历史文本机械拼接，因为
这会重新引入本设计要消除的错误意图风险。

改写失败不允许 Agent 擅自切换 Web Search、模型知识或扩大知识标签范围。

## 15. 幂等、并发与重试

- 群聊继续使用现有会话租约和 FIFO 顺序。
- 私聊继续按现有会话序列和入站消息幂等键处理。
- 同一入站消息只能产生一个最终回答或澄清回答。
- 改写调用前后继续续租会话，避免长模型调用期间并发处理后续消息。
- 改写本身没有外部业务副作用；Durable Job 在最终事务提交前失败时允许重新调用。
- 最终采用的改写决定与回答审计一起成为事实，不要求未提交尝试返回完全相同文本。
- 澄清结果重试不得重复创建机器人消息或发送命令。
- 固定回复已经命中时不得继续调用改写或 RAG。

## 16. 审计与安全

新增查询改写审计或在现有检索审计中增加等价结构，至少记录：

- 入站消息 ID
- 会话 ID
- 频道类型
- 群 ID 或机器人 ID
- 上下文消息 ID
- 改写决定
- 原因代码
- 模型配置 ID
- 调用耗时
- 是否采用原始问题
- 是否执行 RAG
- 原始问题哈希和长度
- 改写问题哈希和长度
- 安全失败代码
- 创建时间

完整原始问题和改写问题不写入普通日志。原始问题可通过已有受权会话记录查询；
改写审计默认只保存哈希、长度和关联消息 ID。

安全要求：

- 当前问题、历史、摘要和模型输出均为不可信数据。
- 改写 Agent 使用明确分隔符，忽略数据块中的指令。
- 改写 Agent 不注册业务 Function Tool。
- Agent 输出必须通过结构、长度、角色和纯文本校验。
- KnowledgeEvidenceProvider 只接受服务端已净化证据。
- 不在异常、日志或审计详情中输出 API Key、连接串、回调令牌或完整上游响应。
- 群知识标签、私聊知识范围、文档激活状态和相似度阈值必须在服务端重新校验。

## 17. 测试

### 17.1 单元测试

- 完整独立问题返回 `Search`。
- “需要什么材料”结合单一历史主题生成完整问题。
- 多主题“那个呢”返回 `Clarification`。
- 非法 JSON、未知枚举、空查询和超长查询返回 `Failure`。
- Prompt Injection 不改变输出合同或产生工具调用。
- 没有历史时改写失败回退当前原始问题。
- 有历史时改写失败不执行检索。
- 澄清文本安全验证和固定文案回退。

### 17.2 群聊集成测试

- 群共享上下文使用正确正式历史。
- 群共享上下文保留用户 A、用户 B 的参与者标签。
- 群共享模式下，用户 B 仅在主题唯一时允许延续用户 A 的群话题。
- 成员隔离上下文不读取其他成员会话。
- 成员隔离模式下，用户 B 的追问不能继承用户 A 的主题。
- 成员隔离缺少有效 `StableSenderId` 时按消息降级为无状态。
- 同名成员不得仅根据 `SenderDisplayName` 合并会话。
- 历史轮数为 0、1、6 时选择正确消息。
- 机器人历史启用和关闭时输入不同。
- 空闲超时后不使用旧主题。
- 清空上下文后不使用旧历史或摘要。
- 摘要可用于话题补全但不作为回答证据。
- Intent NoReply、Uncertain 和 Failed 消息不进入改写输入。
- Intent Agent 的同群原始窗口不得直接成为 QueryRewriteAgent 输入。
- Intent 判断为群追问但成员隔离上下文无主题时返回澄清，不继承其他成员历史。
- `Legacy`、`Shadow`、`AgentFramework` 和 `Paused` 保持各自现有流程语义。
- 固定回复命中时不调用 QueryRewriteAgent。
- 改写后仍只检索群有效可见知识标签。
- 群最终回答处于 Legacy 时仍使用改写后的检索问题。

### 17.3 私聊集成测试

- `roomType=2` 和 `roomType=4` 普通问答均支持追问改写。
- 不同 `RobotConfigId`、`RoomType` 和 `ScopeHash` 不串话。
- `#知识入库` 命令和入库通知不进入普通问答上下文。
- 固定回复命中时不调用 QueryRewriteAgent。
- 私聊改写后检索全部启用且可检索的已发布知识。
- 私聊不读取群会话或群知识标签绑定。

### 17.4 RAG 与证据测试

- 第一轮和第二轮分别执行 Embedding 和 Qdrant 检索。
- 第二轮检索使用完整改写问题。
- 第一轮原始证据不进入第二轮改写输入。
- 第一轮原始证据不进入第二轮 AnswerAgent。
- 文档失效或知识权限变化后第二轮重新应用当前范围。
- 澄清和改写失败场景不调用 Embedding 或 Qdrant。
- 检索阈值、证据防火墙和回答来源保持现有语义。

### 17.5 审计与安全测试

- 改写决定、上下文消息 ID、模型配置和失败原因正确持久化。
- 普通日志不包含问题全文、改写全文或证据正文。
- 不记录密钥、连接串、回调令牌或上游异常正文。
- 重试不产生重复回答、澄清消息或发送命令。
- QueryRewriteAgent 没有知识、Web Search、数据库或写操作工具。

### 17.6 验证命令

实现阶段至少执行：

```text
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj
git diff --check
```

真实模型验收必须使用显式测试入口，且不得打印模型密钥、完整会话或证据正文。

## 18. 验收标准

以下场景必须成立：

1. 用户第一轮询问“日本三年签证你们能办吗”，系统执行第一轮独立 RAG。
2. 在有效上下文内第二轮询问“需要什么材料”，实际检索问题语义等价于
   “办理日本三年签证需要准备什么材料”。
3. 私聊和群聊均满足该行为。
4. 群聊严格遵守当前有效上下文范围、历史轮数、空闲超时、Token 上限、摘要和
   机器人历史配置。
5. 多主题指代不清时返回澄清问题，且没有 Embedding 或 Qdrant 调用。
6. 第一轮原始证据仅用于第一轮回答和审计，不进入第二轮普通上下文。
7. 第二轮重新执行 Embedding、知识范围过滤、Qdrant 检索和阈值判断。
8. 群聊和私聊的会话及知识范围不串联。
9. 不新增或修改任何 `appsettings` 配置项。
10. 不改变现有知识权限、回答降级、发送可靠性和安全边界。
11. 群共享模式保留参与者标签并允许唯一话题的跨成员延续。
12. 成员隔离模式禁止用户 B 继承用户 A 的正式历史和改写主题。
13. 缺少稳定发送者 ID 时安全降级为无状态，不根据显示名称合并成员。
14. Intent 原始群窗口只决定是否回复，不作为查询改写的正式上下文。

## 19. 实施边界

本设计完成后才进入实施计划。实施必须保持以下顺序：

1. Application 合同和结构化输出验证。
2. QueryRewriteAgent 适配器。
3. 统一多轮检索编排。
4. 群聊接入。
5. 私聊接入。
6. KnowledgeEvidenceProvider 接入。
7. 审计持久化。
8. 单元、合同、集成和真实样本验证。

实施不得顺带修改当前正在进行的私聊固定回复功能，也不得重构无关知识管理、
长期记忆、WorkTool 或前端页面。
