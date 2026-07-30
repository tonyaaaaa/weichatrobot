# 群生命周期、单群详情、上下文、长期记忆与回答降级管理设计

## 1. 背景

当前群管理页面能够从 WorkTool 读取群列表、导入群并进入群配置，但状态列
仅用于展示。前后端都没有群停用、启用、归档或恢复接口，也没有按群查看
会话历史和当前有效上下文的独立页面。

WorkTool 导入群已经使用“机器人配置 + 群名称 + 群备注”作为当前可用的
群身份。对于准确导入的群，包含和排除规则不是日常必填项，只用于手工登记、
旧数据兼容或身份无法准确匹配的情况。

本设计增加群生命周期管理、会话直达、短期上下文检查、群级长期记忆入口和
知识库未命中后的可配置回答降级能力，同时保留历史消息、检索审计和管理审计。
单群详情页以“知识与回答”作为默认主界面；匹配规则只作为手工登记和旧数据
兼容能力保留在高级设置中。

短期会话上下文与长期记忆是两套独立状态：

- 短期上下文由会话消息、摘要、清空位置、空闲超时和轮数限制组成。
- 长期记忆由独立记忆系统管理，包含用户偏好、群规则、机器人经验和全局规则。
- 清空短期上下文不得删除或停用长期记忆。
- 忘记长期记忆不得删除会话消息、摘要或检索审计。

## 2. 已验证的 WorkTool 边界

项目已在取得成员同意的专用测试群执行一次真实 `type=512` 证据采集。
验证结果如下：

- WorkTool 接受并成功执行了指令。
- 原始结果能够按 `type=512` 和本次 `messageId` 精确匹配。
- `successList` 和 `failList` 均为空数组。
- 原始消息仅包含请求回显字段，没有成员昵称、成员 ID、成员数量或成员对象。
- 原始证据已删除，仅保留不含成员值的脱敏结构。

因此本期不得把 WorkTool `type=512` 描述为群成员同步能力，不得解析或推测
不存在的成员字段，也不得将显示名称描述为稳定的企业微信成员身份。

如需稳定的群成员同步，应另立项目接入企业微信官方客户群和客户联系接口。

## 3. 目标

- 在群列表管理启用、停用、归档和恢复。
- 停用后阻止新的 AI 回复处理，同时保留配置、消息和审计。
- 将“删除登记”实现为归档，而不是物理删除。
- WorkTool 再次导入同一归档群时恢复原记录，避免产生重复群。
- 从群列表直接进入已筛选的会话审计和上下文详情。
- 从群列表和群配置直接进入已按群筛选的长期记忆中心。
- 按群共享会话或成员隔离会话查看历史、摘要、清空位置和实际上下文预览。
- 明确停用、归档、清空上下文和忘记长期记忆之间的状态边界。
- 将准确导入群的匹配规则收纳为默认折叠的高级能力。
- 将单群详情页重新组织为“知识与回答、上下文与记忆、运行记录、高级设置”。
- 将消息级回复选择交由独立智能回复架构管理，避免成员互聊时机器人逐条插话。
- 按群配置“知识库、模型 Web Search、模型自身知识”的显式三级回答链。
- 区分知识库、联网搜索和模型自身知识回答，并在后台保留可核验审计。
- 在回答、摘要和自动记忆整理的模型输入中保留实际发言成员的显示名称。

## 4. 非目标

- 不实现 WorkTool 群成员自动同步。
- 不恢复或重新设计已经退役的人工转接和群客服功能。
- 不物理删除群、会话、消息、检索审计、长期记忆或历史人工转接数据。
- 不调用模型生成上下文预览。
- 不在群上下文页面重复实现记忆审核、晋升、忘记或恢复 API。
- 不新增独立 Web Search API 或由本系统主动抓取搜索结果网页。
- 不把普通模型回答或无搜索来源的响应标记为 Web Search 成功。
- 不显示 WorkTool 机器人 ID、稳定成员身份原值、凭据或原始 WorkTool 响应。
- 不改变企业微信中的真实群状态，也不调用退群或解散群操作。
- 不根据已经观察到的发言者显示名推导完整群成员目录、账号绑定或可靠身份。
- 不把 WorkTool `replyAll` 状态等同于“回复所有消息”，也不依赖群名称匹配规则
  判断一条消息是否在向机器人提问。

## 5. 群状态模型

继续保留 `GroupProfileEntity.IsEnabled`，新增：

- `ArchivedAtUtc: DateTime?`
- `StateVersion: int`

状态解释如下：

| 状态 | 条件 | 行为 |
|------|------|------|
| 启用 | `IsEnabled=true` 且 `ArchivedAtUtc=null` | 正常匹配并处理后续消息 |
| 停用 | `IsEnabled=false` 且 `ArchivedAtUtc=null` | 保留登记，但不生成新的 AI 回复 |
| 已归档 | `IsEnabled=false` 且 `ArchivedAtUtc!=null` | 默认列表隐藏，只允许查看和恢复 |

应用服务和 `WechatRobotDbContext` 保存前校验保证归档记录不能同时启用。
生产使用 MySQL 5.7，因此不得依赖该版本不会强制执行的 `CHECK` 约束。
`StateVersion` 是独立并发令牌，群配置的 `ConfigurationVersion` 继续只管理
规则、知识标签、短期上下文策略和回答降级配置。

### 5.1 停用

停用操作必须：

- 原子更新 `IsEnabled=false` 和 `StateVersion`。
- 取消尚未进入 WorkTool 外部调用的待发送、重试和阻塞回复。
- 取消尚未开始的该群记忆整理任务；已经租约的任务在写入边界重新检查群状态
  并安全终止。
- 让已经租约但尚未发送的任务在发送边界重新检查群状态并停止发送。
- 保留群规则、知识标签、上下文会话、消息、摘要、长期记忆和审计。
- 将停用后收到的消息关联到准确群身份，记录终态原因 `group_disabled`，
  但不进入 AI 检索、模型调用、上下文序列或记忆整理。

已经进入 WorkTool 外部请求的消息无法撤回。确认弹窗必须明确这一边界。

### 5.2 启用

启用操作必须：

- 只允许未归档群。
- 更新 `IsEnabled=true` 和 `StateVersion`。
- 不恢复或补发停用前取消的发送任务。
- 不自动恢复已清空的上下文。
- 不重放停用期间的消息，也不使用这些消息补做长期记忆整理。
- 重新启用后，原有 `active` 长期记忆恢复正常召回资格。

### 5.3 归档登记

归档是“删除登记”的产品语义。归档前必须满足：

- 群已经停用。
- 不存在待发送、重试或租约中的群发送任务。
- 不存在待处理、重试或租约中的群入站消息。
- 不存在等待、重试或租约中的该群记忆整理任务。

归档成功后设置 `ArchivedAtUtc`，增加 `StateVersion`，但不删除会话、摘要、
审计、知识候选、记忆候选或长期记忆。归档群不参与新消息处理和记忆召回。

### 5.4 恢复登记与重新导入

手工恢复和 WorkTool 重新导入都清除 `ArchivedAtUtc`，但保持
`IsEnabled=false`。管理员必须显式启用，系统不得因远程重新出现而自动恢复
AI 处理。

WorkTool 导入服务应先按当前群身份规则查找包括归档记录在内的候选项。命中
唯一归档记录时恢复原记录并更新导入时间；不得创建新的群 ID。

### 5.5 幂等和版本

目标状态已经满足时，停用、启用、归档和恢复返回当前状态，不增加
`StateVersion`，也不重复写入状态变化审计。目标状态尚未满足时必须校验
`expectedStateVersion`，成功变更后只增加一次版本。

## 6. 消息级回复触发策略

本节已由
`2026-07-29-agent-framework-intelligent-reply-migration-design.md` 取代。

不再实施 `MentionOnly`、`MentionOrWakeWord`、`QuestionDetection`、
`AllMessages`、群唤醒词或回复正则等产品级触发配置。WorkTool `replyAll` 仍只
表示消息回调接收能力，不表示系统应回复每条消息。

新的权威边界是：

- 技术过滤、准确群身份、群停用和归档判断继续由本设计负责。
- 所有通过技术过滤的群文本由 `MessageIntentAgent` 判断是否进入正式回答链。
- Agent 只读取当前消息、`atMe`、显示名称别名和有限最近群消息。
- WorkTool 没有引用消息 ID 或稳定成员 ID，不得虚构引用关系输入。
- 不确定、超时、无效输出和异常均失败关闭，不进入模板、RAG、记忆或发送。
- 智能回复的运行模式、Shadow、灰度、审计、成本和测试由新设计管理。
- 本设计后续章节中的上下文、记忆、知识、Web Search 和生命周期规则继续有效。

## 7. 知识库未命中与回答降级

### 7.1 群级配置

`GroupProfileEntity` 新增以下配置：

- `WebSearchEnabled: bool`：是否在知识库未命中时尝试模型原生 Web Search。
- `ModelKnowledgeFallbackEnabled: bool`：Web Search 关闭、不可用或失败时，是否
  允许模型基于自身知识回答。
- `WebSearchShowSources: bool`：是否在群消息末尾展示网页来源。
- `WebSearchResultCount: int`：搜索结果数量，允许 1 至 20，默认 5。
- `WebSearchRecency: string`：`NoLimit`、`OneDay`、`OneWeek`、`OneMonth` 或
  `OneYear`。
- `WebSearchDomainFilter: string?`：可选域名白名单。
- `WebSearchContentSize: string`：`Medium` 或 `High`。
- `FinalNoEvidencePolicy: string`：`InsufficientEvidence` 或
  `Clarification`，默认 `InsufficientEvidence`。

数据库迁移为所有现有群设置 `WebSearchEnabled=false` 和
`ModelKnowledgeFallbackEnabled=false`，保证升级后不改变现有行为。管理员在
页面首次打开 Web Search 时，前端默认同时打开“允许模型自身知识回答”，保存后
才成为该群的持久配置。来源展示默认关闭，搜索结果数默认 5，时间范围默认不限，
摘要长度默认 `Medium`。

模型配置新增 `WebSearchMode`：

- `None`
- `ZaiChatCompletions`

群配置保存时不要求当前默认聊天模型支持 Web Search。运行时模型不支持时按群
配置降级，并记录 `web_search_unsupported`。模型配置页面提供显式
“测试 Web Search”，测试结果不写入群会话。

### 7.2 三级回答链

每条允许 AI 处理的入站消息按以下顺序执行：

1. 先执行群停用、归档、敏感问题和其他现有前置策略。前置策略命中时不调用
   长期记忆、知识检索或 Web Search。
2. 读取短期会话上下文，并从独立记忆集合召回相关的用户、群、机器人和全局
   长期记忆。记忆召回失败时跳过记忆并继续，不得阻塞回答。
3. 执行知识库检索。存在证据且最高相似度达到阈值时，只使用知识库证据回答；
   长期记忆只能影响表达方式和群规则，不能作为业务事实证据。
4. 零结果或最高相似度低于阈值，且群启用了 Web Search 时，调用模型原生
   Web Search。
5. Web Search 返回非空答案和至少一个合法搜索来源时，标记为
   `web_search` 回答。
6. Web Search 关闭、不支持、超时、失败、无结果或响应不合法时，如允许模型
   自身知识回答，则执行一次不带 Web Search 工具的模型调用。
7. 模型自身知识调用失败或未启用时，执行该群的最终无证据策略。

知识库已命中但生成结果被输出安全检查拦截时，不得改走 Web Search 或模型自身
知识，避免绕过知识库回答约束。

同一入站任务在模型调用前后继续续租会话。任务重试必须复用已有检索审计和
幂等发送边界，不得重复发送回复。

### 7.3 模型调用合同

`ChatCompletionRequest` 新增可选 `WebSearchOptions`，包含：

- 是否启用。
- 搜索结果数量。
- 时间范围。
- 域名过滤。
- 摘要长度。
- 是否要求返回结构化搜索结果。

`ChatCompletionResponse` 新增：

- `WebSearchExecuted`
- `WebSearchSources`

每个来源只包含标题、URL、站点、发布时间、结果序号和经过长度限制的摘要。
只有配置为 `ZaiChatCompletions` 的模型才发送 Z.AI
`tools[type=web_search]` 请求结构。普通 OpenAI 兼容模型不得收到
Z.AI 私有参数。

只有响应包含非空答案和至少一个合法来源时，才能将
`WebSearchExecuted=true`。模型返回普通答案但没有搜索来源时，应视为搜索
未执行并进入配置的降级链。

### 7.4 输出和来源

Web Search 和模型自身知识使用独立于知识库证据防火墙的输出检查，不复用
“答案必须由知识库证据支持”的规则。检查至少拒绝空答案、工具调用残留、控制
字符、异常超长输出和明显的内部提示泄露。

`WebSearchShowSources=false` 时，群消息只包含模型答案。
`WebSearchShowSources=true` 时，在答案末尾最多追加 3 条经过净化的
HTTP 或 HTTPS 来源链接。后台不主动请求、展开或跟随这些 URL。

模型自身知识回答不得伪造来源，也不得继承失败 Web Search 的来源列表。
该调用复用当前会话上下文、相关行为记忆和用户问题，但不携带未达到阈值的
知识库片段，也不携带 Web Search 失败响应。Web Search 调用同样可以使用行为
记忆调整表达方式，但不得把行为记忆作为搜索事实或网页来源。

### 7.5 审计

每次处理记录实际回答来源：

- `knowledge`
- `web_search`
- `model_knowledge`
- `insufficient`
- `clarification`
- `system_failure`

长期记忆命中不单独成为回答来源。审计通过记忆召回摘要记录本轮实际使用的
记忆 ID、作用域、类型和版本，但不得保存完整记忆正文。业务事实来源仍只能是
知识库、Web Search 或模型自身知识。

`RetrievalAuditEntity` 新增：

- `AnswerSource`
- `MemoryRecallJson`
- `WebSearchFailureCode`
- `WebSearchSourcesJson`

`MemoryRecallJson` 只保存实际使用的记忆 ID、作用域、类型和版本，不保存完整
记忆正文或用户昵称原值。

Web Search 审计仅保存净化后的标题、URL、站点、发布时间、结果序号和摘要
哈希；不保存网页 HTML、图标、完整摘要、完整上游响应、认证头或 API Key。
模型自身知识降级成功时记录 `model_knowledge_fallback_used`，以便管理员区分
联网结果和模型已有知识。

### 7.6 LLM 成员归属

WorkTool 消息回调提供 `receivedName`。该值保存为入站消息的
`SenderDisplayName`，只表示消息到达时的成员显示名称，不表示稳定的
企业微信成员身份。

生产消息处理加载历史上下文时必须同时读取每条消息的
`SenderDisplayName`。发送给模型的当前问题和历史消息使用以下语义结构：

```text
成员“张三”：退款需要多久？
机器人：通常需要 3—5 个工作日。
成员“李四”：银行卡填错了怎么办？
```

规则如下：

- 用户消息使用该条消息自己的 `SenderDisplayName`。
- 机器人消息始终标记为“机器人”，不得复用原问题成员的显示名称。
- 当前问题必须显式携带当前回调的成员显示名称。
- 知识库回答、Web Search 和模型自身知识降级使用同一套成员归属格式。
- 成员显示名称和消息正文都位于明确的不可信数据边界内，并使用现有转义规则。
- 对话 Token 估算必须包含成员名称、角色标签和分隔格式开销。
- 只有实际产生消息回调的名称可以进入上下文，不构造完整群成员列表。
- 同名和改名无法可靠区分；成员名称不得用于权限、账号映射、人工转接或审计
  主体身份。

短期上下文摘要在事实归属有意义时保留成员名称，例如“张三询问退款时间”；
不得将不同成员的陈述合并为同一人的偏好。自动记忆整理的消息合同同样增加
`SenderDisplayName`，使整理模型能够区分不同发言者。机器人消息仍统一标记为
机器人。

用户级长期记忆的当前昵称作用域保持现有降级设计，但不得将该作用域描述为稳定
身份。成员显示名称变化后产生的跨名称合并不属于本期。

## 8. 后端接口

### 8.1 群列表

`GET /api/admin/worktool/groups`

新增可选参数 `status=active|disabled|archived|all`。默认返回 `active` 和
`disabled`，不返回归档记录。

响应增加：

- `status`
- `archivedAtUtc`
- `stateVersion`
- `registrationSource`
- `workToolImportedAtUtc`
- `workToolLastSeenAtUtc`

### 8.2 启用和停用

`PUT /api/groups/{id}/enabled`

请求：

- `enabled`
- `expectedStateVersion`

版本不一致返回：

- HTTP `409`
- `error=group-state-conflict`
- `currentStateVersion`

归档群启用返回 `409 group-is-archived`。

### 8.3 归档

`DELETE /api/groups/{id}?expectedStateVersion={version}`

失败契约：

- 群仍启用：`409 group-must-be-disabled`
- 版本冲突：`409 group-state-conflict`
- 存在活动引用：`409 group-archive-blocked`

活动引用响应只返回分类计数：

- `activeSendCommands`
- `activeInboundMessages`
- `activeMemoryJobs`

不得返回消息正文、成员名称或上游响应。

### 8.4 恢复

`POST /api/groups/{id}/restore`

请求携带 `expectedStateVersion`。恢复后状态固定为停用。

### 8.5 会话列表

`GET /api/groups/{id}/conversation-sessions`

支持 `page` 和 `pageSize`，默认每页 20 条并限制最大页大小。返回：

- 会话 ID
- 范围类型：群共享或成员隔离
- 成员显示名快照
- 最后活动时间
- 摘要是否存在
- 清空时间和清空序号
- 消息数量

成员隔离会话不得返回 `SenderScopeKey` 原值。成员显示名从该会话最近一条
入站消息读取，仅作为显示快照，不作为身份绑定依据。

### 8.6 会话详情

`GET /api/groups/{id}/conversation-sessions/{sessionId}`

消息按会话序号稳定分页，返回：

- 消息 ID
- 入站或出站方向
- 用户或机器人角色
- 成员显示名
- 消息文本
- 接收和创建时间
- 处理状态与终态原因
- 是否位于当前清空位置之前

接口必须同时验证 `sessionId` 属于路由中的群。

### 8.7 上下文预览

`GET /api/groups/{id}/conversation-sessions/{sessionId}/context-preview`

预览使用服务器当前时间和群当前有效策略，不接受客户端伪造时间。返回：

- 当前有效上下文策略
- 当前摘要及是否会进入模型
- 将进入模型的消息 ID 和顺序
- 未进入模型的消息 ID 与稳定原因码
- 是否发生空闲重置
- 是否触发 Token 限制
- 估算 Token 数量

原因码至少包括：

- `cleared`
- `idle-reset`
- `history-turn-limit`
- `token-limit`
- `bot-history-disabled`
- `not-completed`

扩展 `ConversationContextService` 的检查结果，使生产消息处理和后台预览使用
同一个选择算法。不得在 API 层复制上下文规则。

上下文预览只解释短期会话上下文，不执行语义记忆检索。响应增加长期记忆摘要：

- 当前群有效的群级记忆数量。
- 当前会话昵称作用域有效的用户级记忆数量。
- 是否存在等待整理的记忆候选。
- 记忆中心的群筛选链接参数。

不得把长期记忆正文混入短期上下文消息列表。实际回答使用哪些记忆，只能从
对应会话审计查看。

### 8.8 群配置

现有 `GET /api/groups/{id}/configuration` 和
`PUT /api/groups/{id}/configuration` 的响应与请求增加
`messageTrigger` 和 `answerFallback`。

`messageTrigger` 包含：

- `mode`
- `wakeWords`
- `mergeWindowSeconds`

`answerFallback` 包含：

- `webSearchEnabled`
- `modelKnowledgeFallbackEnabled`
- `showWebSources`
- `resultCount`
- `recency`
- `domainFilter`
- `contentSize`
- `finalNoEvidencePolicy`

更新继续使用 `expectedConfigurationVersion`。字段越界、非法域名或未知枚举
返回 `400 ValidationProblem`，版本冲突继续返回稳定的
`409 group-configuration-conflict`。

机器人 `replyAll` 能力继续读取现有
`GET /api/admin/worktool/robots/{robotId}/callbacks`，不复制到群配置持久化合同。
该能力查询失败时前端显示“当前无法确认是否接收未 @ 消息”，不得阻止管理员
保存群内策略，也不得把未知状态显示为已开启。

上下文详情和检索审计接口增加 `answerSource`、安全失败代码和经过净化的 Web
Search 来源。只有具备会话审计权限的用户可读取这些来源。

## 9. 权限和审计

- 群状态变更仅允许管理员。
- 会话历史和上下文预览沿用会话审计权限边界，仅允许管理员和知识运营人员。
- 群长期记忆摘要和记忆中心入口沿用记忆查看权限。
- 所有状态变更写入管理审计。
- 群回答降级配置变更写入管理审计，但不记录模型密钥或完整搜索提示。
- 群消息触发配置变更写入管理审计；意图分类审计使用稳定原因码，不记录正文、
  成员名称或模型原始输出。
- 审计记录包含群内部 ID、旧状态、新状态、操作者、版本和阻塞计数。
- 审计不得包含消息正文、摘要、成员名称、机器人凭据或 WorkTool 原始响应。
- 搜索结果始终按不可信外部输入处理。URL 仅允许 HTTP 和 HTTPS，文本字段进行
  长度限制、控制字符清理和安全序列化。
- 所有列表和消息查询必须分页并限制最大页大小。

## 10. 前端设计

### 10.1 群列表

增加状态筛选：

- 全部
- 启用
- 停用
- 已归档

每行根据状态显示：

- 配置
- 查看会话
- 查看记忆
- 查看审计
- 停用或启用
- 删除登记
- 恢复登记

已归档群只允许查看会话、查看记忆、查看审计和恢复登记。

停用确认说明：

- 后续消息不再触发 AI 回复。
- 尚未发送的回复会取消。
- 已进入 WorkTool 外部请求的消息无法撤回。

删除登记确认说明：

- 群必须先停用。
- 操作不会删除历史消息、短期上下文、长期记忆和审计。
- 可在“已归档”筛选中恢复。

### 10.2 会话和审计直达

“查看会话”进入群上下文页面。“查看审计”进入
`/audit?groupId={groupId}`。会话审计页面初始化时读取查询参数并自动选择该群。
“查看记忆”进入 `/memory?groupId={groupId}`，记忆中心初始化时读取查询参数
并筛选该群的候选、正式记忆和整理任务。

### 10.3 群配置

群详情继续使用现有路由，页头只保留一组面包屑和标题，避免重复显示“返回群
列表”和群名称。页头只读展示群名称、机器人、群备注、WorkTool 导入来源和
启用状态。

详情页使用以下一级标签：

1. `知识与回答`：默认标签，配置知识权限和知识库未命中后的回答链。
2. `上下文与记忆`：配置短期上下文，展示长期记忆摘要和相关入口。
3. `运行记录`：只读进入或展示现有会话、上下文、回答审计和发送记录。
4. `高级设置`：保留低频群匹配规则和保存前预览。

`知识与回答` 包含：

- “Agent 自动判断是否回复”只读运行说明。
- 固定回复模板入口和当前群生效模板摘要；详细行为由
  `2026-07-29-private-chat-direct-knowledge-and-fixed-reply-agent-design.md`
  管理。
- 可检索的知识标签；全局公开标签只读说明，无需重复选择。
- 知识库未命中处理。
- 当前默认聊天模型的 Web Search 能力提示。
- 最终无可靠答案策略。

页面不再提供 `@`、唤醒词、问题识别或回复全部模式。只读说明解释 Agent 会
结合 `atMe`、发送者别名、上一轮机器人消息、时间间隔和有限最近消息判断；
不确定或失败时不回复。

当机器人 `replyAll=false` 时，页面显示智能回复能力不完整和“打开机器人设置”
入口，但不得自动声称 WorkTool 已配置成功。

“知识库未命中处理”包含：

- 启用模型 Web Search。
- 允许模型自身知识回答。
- 群消息展示网页来源。
- 搜索结果数量。
- 搜索时间范围。
- 限定搜索域名。
- 搜索摘要长度。
- 最终失败策略。

关闭 Web Search 时隐藏搜索参数，但仍允许单独开启模型自身知识回答。首次打开
Web Search 时默认同时开启模型自身知识降级，来源展示保持关闭。页面显示当前
默认聊天模型的 Web Search 能力；不支持时显示降级提示，但不阻止保存。

`上下文与记忆` 包含：

- 短期上下文范围、轮数、空闲超时、Token 上限、摘要和机器人历史。
- 群级有效记忆、当前昵称作用域用户记忆和待整理候选数量的只读摘要。
- “查看当前上下文”和“打开记忆中心”入口。
- 独立的“清空短期上下文”危险操作。

`运行记录` 不新增统计接口。没有现成列表合同的内容使用明确入口跳转到现有按群
筛选的会话、上下文、会话审计和发送记录，不显示伪造数量或成功状态。

`高级设置` 中的“匹配规则”和“保存前预览”默认折叠。对于
`WorkToolImport` 群显示：

> 当前群已通过 WorkTool 准确登记，无需配置匹配规则。

系统不得为 WorkTool 导入群自动生成包含、正则或排除规则。手工登记、旧数据
兼容或身份无法准确匹配时，管理员仍可显式展开并维护高级群匹配。

三个可编辑标签共享同一份群配置草稿。切换标签不得丢失未保存内容。仅当草稿
发生变化时显示固定保存栏；保存中禁用重复提交。保存成功使用 `ElMessage`，
离开存在未保存内容的页面使用 `ElMessageBox` 确认，不使用浏览器原生
`alert`、`confirm` 或 `prompt`。

普通配置保存与“清空短期上下文”分离。配置版本冲突不得覆盖新数据；前端重新
加载最新配置并提示管理员复核后重新保存。

### 10.4 上下文详情

页面左侧或顶部展示会话列表，主区域展示：

- 会话范围和最近成员显示名。
- 最后活动、摘要和清空位置。
- 分页历史消息。
- “当前会进入模型”和“当前不会进入模型”分组。
- 每条被排除消息的原因。
- 每条机器人回答的实际来源：知识库、Web Search 或模型自身知识。
- Web Search 回答的净化来源列表和搜索失败代码。
- 本群长期记忆摘要和“查看长期记忆”入口。

上下文预览只读，不调用模型、不写数据库。已有“清空本群上下文”操作继续
保留在群配置页面，并明确说明：

- 只清空短期摘要和后续上下文选择位置。
- 不删除历史消息和审计。
- 不删除、忘记或停用长期记忆。
- 如需删除长期偏好，必须进入记忆中心执行“忘记”操作。

### 10.5 模型配置

模型配置页面增加“Web Search 能力”选择：

- 不支持。
- Z.AI Chat Completions。

“测试 Web Search”只验证当前配置是否接受 Web Search 工具并返回非空答案和
至少一个合法来源。测试失败显示稳定失败代码，不展示上游响应正文。

## 11. 错误处理

- 所有异步页面提供加载、空数据、失败和重试状态。
- `404` 显示群或会话不存在。
- `group-state-conflict` 提示数据已变化并刷新当前行。
- `group-archive-blocked` 展示分类计数，不展示敏感详情。
- `group-configuration-conflict` 重新加载最新群配置并提示复核，不自动重放
  本地草稿。
- 智能回复运行配置非法时返回 `400 ValidationProblem`；意图 Agent 超时、
  失败、响应非法或输出不确定时安全不回复，并记录稳定原因码，不得因此改走
  知识检索、Web Search 或模型自身知识回答。
- WorkTool 重新导入发生身份冲突时继续返回稳定冲突契约，不自动合并多条记录。
- 上下文详情遇到不合法 JSON 或历史坏数据时显示安全占位符，不返回异常详情。
- Web Search 使用以下稳定失败代码：
  - `web_search_disabled`
  - `web_search_unsupported`
  - `web_search_timeout`
  - `web_search_provider_failure`
  - `web_search_invalid_response`
  - `web_search_zero_results`
  - `model_knowledge_fallback_used`
  - `model_knowledge_fallback_failed`
- 失败代码只进入后台审计。群消息不得包含上游异常、响应正文、内部提示或
  认证信息。

## 12. 数据迁移

新增 EF Core 迁移：

- `group_profile.ArchivedAtUtc`
- `group_profile.StateVersion`
- `group_profile.WebSearchEnabled`
- `group_profile.ModelKnowledgeFallbackEnabled`
- `group_profile.WebSearchShowSources`
- `group_profile.WebSearchResultCount`
- `group_profile.WebSearchRecency`
- `group_profile.WebSearchDomainFilter`
- `group_profile.WebSearchContentSize`
- `group_profile.FinalNoEvidencePolicy`
- `model_config.WebSearchMode`
- `retrieval_audit.AnswerSource`
- `retrieval_audit.MemoryRecallJson`
- `retrieval_audit.WebSearchFailureCode`
- `retrieval_audit.WebSearchSourcesJson`
- 支持状态筛选的索引

归档和启用互斥由应用服务与 `WechatRobotDbContext` 保存前校验双重保护，不依赖
MySQL 5.7 的 `CHECK` 约束行为。

迁移默认所有现有群：

- `ArchivedAtUtc=null`
- `StateVersion=0`
- `WebSearchEnabled=false`
- `ModelKnowledgeFallbackEnabled=false`
- `WebSearchShowSources=false`
- 搜索结果数、时间范围、摘要长度和最终策略使用本文定义的默认值
- 保留现有 `IsEnabled`

不得新增或回填 `MessageTriggerMode`、唤醒词或回复正则字段。智能回复的运行
配置和历史兼容策略由
`2026-07-29-agent-framework-intelligent-reply-migration-design.md` 管理。

迁移必须兼容生产 MySQL 5.7，不使用仅 MySQL 8 支持的 SQL 特性。

## 13. 测试策略

### 13.1 后端

- 群状态接口的启用、停用、归档、恢复和幂等测试。
- `StateVersion` 并发冲突测试。
- 发送任务、入站任务和记忆整理任务的归档阻塞测试。
- 停用群不进入检索和模型调用的消息流水线测试。
- 停用群不产生新记忆整理任务，已租约任务在写入边界停止的测试。
- 停用后未发送回复取消且重新启用不补发的测试。
- 群名称匹配规则只解析准确群身份，不决定一条消息是否面向机器人的测试。
- `WasMentioned` 作为智能意图信号传递但不强制回复的测试。
- Intent Agent 的 Reply、NoReply、Uncertain、超时、异常和非法输出测试；
  除 Reply 外均不得进入回答链。
- Intent Agent 不读取知识库、长期记忆或 Web Search，且分类审计不包含正文和
  原始模型输出的测试。
- `replyAll=false` 时现有机器人回调状态接口准确返回能力降级的测试。
- 归档后历史消息、会话、长期记忆和检索审计仍可查询的测试。
- WorkTool 重新导入恢复原群 ID 且保持停用的测试。
- 会话所属群校验、分页和权限测试。
- 上下文预览与生产上下文算法一致性测试。
- 清空短期上下文不修改长期记忆的测试。
- 长期记忆召回失败不阻塞知识库、Web Search 或模型自身知识回答的测试。
- 行为记忆不得成为业务事实来源的提示词和审计测试。
- 生产历史查询携带每条消息 `SenderDisplayName` 的测试。
- 当前问题、知识库回答、Web Search 和模型知识降级提示词均保留成员归属的
  测试。
- 机器人历史始终标记为机器人，不复用提问成员名称的测试。
- 成员名称和消息正文无法逃逸不可信数据边界的测试。
- Token 预算包含成员名称和格式开销的测试。
- 对话摘要和自动记忆整理区分不同成员发言的测试。
- 三级回答链的完整决策矩阵测试。
- 敏感问题、停用群和归档群不触发 Web Search 的测试。
- 知识库命中时不调用 Web Search 的测试。
- Z.AI Web Search 请求和响应的合同测试。
- 无来源普通回答不得记为 Web Search 成功的测试。
- Web Search 超时、失败、无结果和不支持时的模型自身知识降级测试。
- 模型自身知识失败后执行最终无证据策略的测试。
- Web Search 来源净化、默认不展示和最多展示 3 条的测试。
- 任务重试不重复调用持久化后的完成路径、不重复发送回复的测试。
- 保存脱敏真实响应形状并据此固化合同测试，不根据文档猜测生产响应。
- MySQL 5.7 迁移和查询兼容测试。

### 13.2 前端

- 状态筛选和状态按钮显示测试。
- 停用、删除登记和恢复确认测试。
- `409` 冲突刷新和归档阻塞计数测试。
- 查看会话和带群筛选的审计直达测试。
- 查看记忆和带群筛选的记忆中心直达测试。
- 高级匹配默认折叠和 WorkTool 导入提示测试。
- 群详情默认打开“知识与回答”，一级标签顺序和内容正确的测试。
- “Agent 自动判断是否回复”的只读说明和旧触发模式不再显示的测试。
- `replyAll=false` 能力警告和机器人设置入口测试。
- 机器人回调能力查询失败时显示未知状态且不阻止保存群配置的测试。
- 智能回复成本、失败关闭和显示名称非稳定身份说明测试。
- 标签切换保留未保存草稿、发生修改才显示保存栏的测试。
- 离开未保存页面使用 `ElMessageBox`、保存结果使用 `ElMessage` 的测试。
- 运行记录只使用现有按群筛选入口，不展示伪造统计的测试。
- 会话分页、上下文选入状态和排除原因测试。
- Web Search 开关、字段联动、默认隐藏来源和验证错误测试。
- 模型不支持 Web Search 时的降级提示和保存行为测试。
- 上下文详情回答来源与净化网页来源显示测试。
- 群配置和上下文详情不再出现人工客服或暂停策略测试。
- 清空上下文确认文案明确不影响长期记忆的测试。

### 13.3 完成验证

- 运行相关后端单元、契约和 MySQL 集成测试。
- 运行前端 `npm run typecheck`。
- 运行前端 `npm test -- --run`。
- 运行前端 `npm run build`。
- 运行 `git diff --check`。
- 使用 `.local` 启动 API、Worker 和前端。
- 验证 API 存活、依赖就绪、Worker 心跳和前端 HTTP 200。
- 在浏览器中验收群列表、群配置、会话直达和上下文详情。
- 使用授权测试群验证成员互聊不回复、未 `@` 的机器人连续追问可回复、意图
  不确定时不回复，并验证 WorkTool 不存在的引用字段没有被伪造使用。
- 使用经过授权的测试模型验证知识库命中、Web Search 成功、Web Search 失败后
  模型自身知识回答三条主链。
- 验证群回复默认不包含网页来源，后台审计能够区分实际回答来源。

## 14. 交付边界

本期完成后，管理员可以可靠管理群登记生命周期，检查实际短期上下文，并按群
进入独立长期记忆中心。人工转接和群客服运行功能保持退役，群成员同步不属于
本规格。
消息是否进入回答链由智能回复架构的 `MessageIntentAgent` 统一判断，不再由
每个群配置 `@`、唤醒词或回复所有消息模式。WorkTool `replyAll` 只表示能否
接收未 `@` 消息，不再被解释为系统会回复所有消息。
知识库未命中后的 Web Search 与模型自身知识回答只在群显式配置后启用，升级
不会自动改变现有群的回答范围或外部调用成本。
模型能够区分回调中实际出现的成员发言，但该能力不构成完整群成员同步或稳定
身份映射。
