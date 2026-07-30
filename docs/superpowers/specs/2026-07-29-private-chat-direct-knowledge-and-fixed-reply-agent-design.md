# 私聊机器人、直接知识入库与群固定回复 Agent 设计

> 实施状态（2026-07-29）：核心合同、持久化、API、Worker、管理页面和灰度配置已实现；
> 上线与回退以 `docs/runbooks/agent-framework-private-chat-fixed-replies-rollout.md`
> 为准，生产启用仍需完成授权流量验收。

## 1. 背景

WechatRobot 当前主要处理已登记的 WorkTool 外部群消息，使用群规则决定是否
回复，再通过短期上下文、长期记忆、知识标签、Qdrant 检索、模型回答和回答
降级生成消息。现有 Worker、持久化任务、发送队列、限流、重试、死信和
WorkTool 命令结果回调已经形成可靠执行链。

本期增加两类能力：

1. WorkTool 私聊机器人：
   - 外部联系人和内部联系人均可进行私聊知识问答。
   - 内部联系人发送固定首行 `#知识入库` 时，可以直接整理并发布知识，不经过
     现有知识候选人工审核。
   - 私聊入库自动匹配现有标签、对比相似知识，并区分新增、重复、补充和纠正。
2. 群固定回复模板：
   - 后台配置多个固定回复意图和固定正文。
   - Microsoft Agent Framework 根据群成员问题选择模板 Function Tool。
   - 明确命中后原样发送模板正文；不明确或失败时继续现有知识库问答。
   - 支持全局模板、指定群模板，以及全局模板在单群停用的例外。

本设计是私聊知识入库和固定模板的功能级权威设计。
`2026-07-29-agent-framework-intelligent-reply-migration-design.md` 是统一 Agent
运行时、群消息意图判断和普通回答渐进迁移的架构级权威设计。

本功能首次交付只让 Microsoft Agent Framework 负责私聊理解和工具编排，以及
群固定模板意图路由。现有普通群知识问答、Qdrant、Worker、WorkTool、发送队列、
审计和重试链不在本功能阶段整体改写；后续普通回答模型执行层是否迁移，由架构
级设计的 Shadow、灰度和等价测试门槛控制。

## 2. 已验证的外部能力边界

### 2.1 WorkTool

WorkTool 消息回调的 `roomType` 可区分：

- `1`：外部群。
- `2`：外部联系人私聊。
- `3`：内部群。
- `4`：内部联系人私聊。

本期在现有已登记外部群链路上增加群固定模板路由，并增加 `roomType=2` 和
`roomType=4` 的私聊处理。`roomType=3` 不在本期扩展范围内。

公开回调仅提供 `receivedName` 等显示字段，没有可靠的企业微信 `userid` 或
`external_userid`。因此私聊上下文只能按机器人、房间类型和规范化显示名称
隔离。系统必须明确记录这是兼容身份，不得将其描述成稳定企业微信成员身份。
本期接受昵称变更导致上下文漂移的限制，不以此阻塞私聊功能。

发送仍复用 WorkTool `sendRawMessage` 和现有发送队列。私聊目标和群目标都
必须由已验证的 WorkTool 发送契约构造，不新增未经官方样例或真实回调验证的
字段。

### 2.2 Microsoft Agent Framework

Microsoft Agent Framework 提供 Agent、会话和 Function Tool 编排。项目使用
其 .NET 能力，并遵守以下边界：

- Agent 不能直接访问 EF Core、写 MySQL、写 Qdrant或创建发送命令。
- Agent 只能调用应用层提供的强类型工具。
- 工具参数必须经过后端重新验证。
- Agent 输出不能直接成为固定模板正文。
- 不启用 Agent Framework Durable Extension；可靠任务继续由现有 Worker
  和持久化任务管理。
- 依赖版本继续使用仓库的中央包管理，不引入第二套依赖版本管理方式。

2026-07-29 已核验 `Microsoft.Agents.AI` 和
`Microsoft.Agents.AI.OpenAI` 1.15.0 为稳定包。当前项目自定义
`IChatCompletionClient` 只支持普通文本和 Z.AI Web Search，不具备 Function
Tool 完整循环或 JSON Schema 输出。实施必须先增加
`Microsoft.Extensions.AI.IChatClient` 兼容层和真实模型能力探针，不能把
“普通聊天连接成功”当成 Agent 工具能力成功。

默认聊天模型必须支持 Function Tool 才能执行 Agent 路由。模型不支持、超时
或调用失败时，群固定模板路由必须降级到现有知识问答；私聊命令必须返回安全
失败结果，不得虚构成功。

## 3. 目标

- 接收 `roomType=2` 和 `roomType=4` 私聊文本消息。
- 为每个兼容私聊身份维护独立、多轮短期上下文。
- 私聊普通问题检索全部已发布知识，不受群知识标签绑定限制。
- 仅允许 `roomType=4` 通过固定首行 `#知识入库` 发起直接入库。
- 将一次私聊入库整理为最多 20 条问答知识。
- 优先匹配现有知识标签；无可靠标签时进入系统管理的全局知识标签。
- 自动对比相似知识并分类为新增、重复、补充或纠正。
- 补充和纠正创建新版本，保留旧版本，不原地覆盖。
- 为知识版本记录来源、来源消息、变更类型和被替代版本。
- 将现有来源统一为文档上传、消息审核入库和私聊直接入库。
- 在群消息进入现有 RAG 之前使用 Agent Function Tool 判断固定模板。
- 支持全局模板、指定群模板和单群全局模板排除。
- 在独立模板管理页和群详情页双向管理同一套模板与群关系。
- 固定模板回复继续使用现有会话记录、发送限流、FIFO、重试和死信。
- 对私聊入库、模板路由、模板修改和群作用域修改保留管理或业务审计。

## 4. 非目标

- 不实现可靠企业微信成员目录、账号映射或人工客服分配。
- 不恢复已经退役的人工转接运行功能或前端入口。
- 不根据昵称推断稳定企业微信身份。
- 不让外部联系人通过私聊直接入库。
- 不使用入库口令、管理员账号绑定或额外人工审批。
- 不查询签证业务系统、申请进度数据库或第三方业务 API。
- 固定回复正文第一版不支持业务变量、占位符或外部数据填充。
- 不让 Agent 自己生成、创建、编辑、启停或删除固定回复模板。
- 不在本功能交付阶段用 Agent Framework 重写普通群 RAG、长期记忆、联网搜索
  或发送 Worker；后续只允许按架构级迁移设计渐进替换模型执行边界。
- 不在 Agent 调用中直接执行不可恢复的数据修改。
- 不把 Agent 判断置信度当成唯一安全校验。
- 不把 Agent Framework 调用成功等同于知识发布或 WorkTool 发送成功。

## 5. 总体架构

### 5.1 群消息

```text
WorkTool 外部群回调
  -> API 快速确认、回调鉴权、幂等入站
  -> ProcessInboundMessage Durable Job
  -> 现有群状态与回复规则
  -> MessageIntentAgent（启用智能回复接管后）
       -> NoReply / Uncertain / Failure：终止，不调用模板或 RAG
       -> ReplySelected：继续
  -> 获取当前群有效固定模板
  -> Template Routing Agent
       -> match_fixed_template(templateId, expectedVersion)
            -> 后端验证模板、版本、启用状态和群作用域
            -> 原样读取固定正文
            -> 复用现有会话持久化与发送队列
       -> continue_knowledge_answer()
            -> 现有上下文、记忆、Qdrant、Web Search、模型知识降级
  -> 现有 WorkTool 发送 Worker、结果回调、重试和死信
```

固定模板路由始终在“消息已经允许回复”之后执行。智能回复接管前，“允许回复”
来自现有群技术策略；`MessageIntentAgent` 接管后，必须先取得
`ReplySelected`。被停用、归档、未登记、技术策略拒绝或意图 Agent 判断不回复
的消息不得调用模板 Agent 或普通回答链。

### 5.2 私聊消息

```text
WorkTool 私聊回调
  -> API 快速确认、回调鉴权、幂等入站
  -> 私聊 Durable Job
  -> 建立或续接私聊兼容身份会话
  -> Private Chat Agent
       -> 普通问题
            -> 检索全部已发布知识
            -> 生成有依据回答
            -> 现有发送队列
       -> roomType=4 且首行为 #知识入库
            -> 立即发送“已收到，正在整理”
            -> 拆分最多 20 条问答
            -> 标签匹配与相似知识比较
            -> 后端批次校验和暂存
            -> 现有索引任务
            -> 全部成功后批量激活
            -> 发送最终成功或失败汇总
```

私聊回调处理只做验证、持久化和快速确认。模型调用、比较、索引和通知必须在
Worker 中执行。

## 6. 私聊会话与兼容身份

### 6.1 会话作用域

私聊会话键由以下字段组成：

```text
RobotConfigId + RoomType + Normalize(receivedName)
```

生成不可逆 `ScopeHash` 用于唯一约束和查询。原始显示名称只用于后台可读展示
和 WorkTool 发送目标，不得被描述为稳定成员 ID。

现有会话模型应扩展为同时支持群和私聊，而不是为私聊复制一套完全独立的会话
历史：

- `ChannelType`: `Group` 或 `Private`。
- `RoomType`: WorkTool 原始房间类型。
- `RobotConfigId`。
- `GroupProfileId`: 群会话使用；私聊允许为空。
- `PeerDisplayName`: 私聊对端显示名称。
- `ScopeHash`: 会话唯一作用域摘要。

迁移必须保留现有群会话数据，并为群会话生成兼容默认值。所有读取代码必须
显式区分群会话和私聊会话，不能把空 `GroupProfileId` 当成异常。

### 6.2 私聊上下文

- 普通私聊问答进入多轮上下文。
- 机器人回复进入同一会话上下文。
- `#知识入库` 命令及整理中间数据不进入普通问答上下文。
- 入库确认和最终统计通知不进入普通问答上下文。
- 昵称变化产生新的兼容会话，不自动合并旧上下文。
- 私聊问答默认使用现有短期上下文上限和摘要机制；管理配置可后续独立扩展，
  本期不新增第二套上下文参数。

## 7. Private Chat Agent

### 7.1 普通私聊问答

`roomType=2` 和 `roomType=4` 均可使用。与群问答的差异是：

- 检索范围为全部启用且可检索的已发布知识。
- 不使用群知识标签绑定作为过滤条件。
- 仍应用敏感内容、输出防火墙、模型超时和安全失败文本。
- 仍记录检索证据、模型配置版本和回答来源。
- 不执行群匹配规则、群停用判断或群级 Web Search 配置。

### 7.2 直接入库触发

只有同时满足以下条件才进入直接入库：

- `roomType=4`。
- 文本消息。
- 规范化后的第一行严格等于 `#知识入库`。
- 第一行之后存在非空正文。

以下情况不得入库：

- `roomType=2` 使用相同首行。
- `#知识入库` 出现在正文中间。
- 首行包含额外文字。
- 没有正文。
- 非文本消息。

不满足入库条件的消息按普通私聊问答处理；`roomType=2` 使用入库标记时应
返回明确的不支持说明，避免误认为已发布。

### 7.3 Agent 工具

Private Chat Agent 可以调用只读或提议型工具，例如：

- `list_active_knowledge_tags`
- `find_similar_knowledge`
- `propose_knowledge_items`
- `propose_tag_matches`

Agent 只返回结构化提议：

- 问题。
- 答案。
- 显式标签词。
- 建议标签 ID。
- 相似知识 ID。
- 建议变更类型。

应用服务负责重新检查长度、数量、标签状态、知识状态、相似目标、版本关系和
批次幂等键，然后才允许暂存。

## 8. 私聊直接知识入库

### 8.1 批次

新增 `PrivateKnowledgeIngestBatch`：

- `Id`
- `RobotConfigId`
- `SourceConversationMessageId`
- `RoomType`
- `SourceActorDisplayName`
- `Status`
- `ModelConfigurationId`
- `ModelConfigurationVersion`
- `TotalCount`
- `NewCount`
- `DuplicateCount`
- `SupplementCount`
- `CorrectionCount`
- `FailureCode`
- `ReceivedNotificationState`
- `FinalNotificationState`
- `CreatedAtUtc`
- `UpdatedAtUtc`
- `Version`

`SourceConversationMessageId` 必须唯一，保证回调重投和 Worker 重试不会生成
第二批知识。

批次状态：

```text
Received
  -> Extracting
  -> Comparing
  -> Staged
  -> Indexing
  -> Activated
```

失败进入可诊断的 `Failed` 或可重试状态，不得伪装成成功。

### 8.2 条目

新增 `PrivateKnowledgeIngestItem`：

- `Id`
- `BatchId`
- `Sequence`
- `Question`
- `Answer`
- `ChangeKind`: `New`、`Duplicate`、`Supplement`、`Correction`
- `MatchedDocumentId`
- `MatchedVersionId`
- `StagedDocumentId`
- `StagedVersionId`
- `QuestionFingerprint`
- `AnswerFingerprint`
- `ProposedTagsJson`
- `ResolvedTagIdsJson`
- `FailureCode`
- `CreatedAtUtc`

一次批次最多 20 条。问题和答案必须使用现有知识内容长度上限或更严格上限。
所有自由文本在日志和审计中使用摘要或截断值，不记录模型凭据和原始请求体。

### 8.3 标签处理

处理顺序：

1. 对 Agent 识别到的显式标签名称做规范化精确匹配。
2. 在现有启用标签中做语义匹配。
3. 显式标签没有可靠近似标签时自动创建新标签。
4. 新标签默认启用，但不设为全局公开标签。
5. 没有显式标签时，Agent 从现有标签中选择最合适标签。
6. 无可靠匹配时绑定系统管理的“全局知识”标签。

“全局知识”标签必须是系统记录，使用稳定标识或系统标志查找，不能依赖可编辑
显示名称硬编码。迁移或启动修复必须幂等创建该标签。

### 8.4 相似知识比较

Agent 根据只读相似知识结果提出：

- `New`：没有可靠相似知识。
- `Duplicate`：语义和答案没有实质差异。
- `Supplement`：在现有事实基础上增加兼容信息。
- `Correction`：新内容替代或纠正现有事实。

后端必须验证目标知识仍为当前有效版本。目标在 Agent 判断后发生变化时，批次
重新比较或安全失败，不能覆盖新版本。

重复条目跳过发布，但计入最终统计。新增、补充和纠正进入暂存与索引。

### 8.5 批量激活

“批次全部成功”指对外可见和激活状态原子一致，不表示失败时完全不保留暂存
记录：

- 所有可发布条目先创建暂存文档或版本。
- 每个版本完成分段和临时索引。
- 只有全部可发布条目索引成功后，才在一个数据库事务中激活全部新版本。
- 补充和纠正激活新版本后，旧版本退出当前有效状态但继续留档。
- 任一索引失败时，旧版本保持有效，所有暂存记录保留供重试和审计。
- 不允许部分批次对检索可见。

## 9. 知识来源与版本沿革

来源记录在 `KnowledgeDocumentVersion`，而不是只记录在文档上，因为同一文档
可以由上传创建，之后被私聊纠正。

新增或扩展字段：

- `SourceType`
- `SourceConversationMessageId`
- `SourceActorDisplayName`
- `SourceBatchId`
- `ChangeKind`
- `SupersedesVersionId`

来源枚举：

- `DocumentUpload`：文档上传。
- `ConversationReview`：消息审核、知识候选批准、历史候选或记忆候选进入知识库。
- `PrivateChatDirect`：内部联系人私聊直接入库。

补充和纠正必须创建新版本并设置 `SupersedesVersionId`。不得修改旧版本正文。
来源字段进入知识文档详情、版本历史、检索审计和管理审计展示。

## 10. 群固定回复模板

### 10.1 模板

新增 `FixedReplyTemplate`：

- `Id`
- `Name`
- `IntentDescription`
- `ReplyText`
- `ScopeType`: `Global` 或 `SelectedGroups`
- `Priority`
- `IsEnabled`
- `Version`
- `CreatedByUserId`
- `UpdatedByUserId`
- `CreatedAtUtc`
- `UpdatedAtUtc`

约束：

- 名称、意图说明和回复正文不能为空。
- 回复正文第一版为纯固定文本，命中后原样发送。
- 优先级使用有界整数。
- 每个模板至少一条示例问法。
- 修改使用 `Version` 乐观并发。
- 删除采用受控删除；已经被历史审计引用的模板不得物理删除，可停用或软删除。

### 10.2 示例问法

新增 `FixedReplyTemplateExample`：

- `Id`
- `TemplateId`
- `ExampleText`
- `NormalizedText`
- `CreatedAtUtc`

同一模板内规范化示例唯一。示例用于帮助 Agent 理解意图，不等同于纯字符串
精确匹配。

### 10.3 群作用域

新增 `FixedReplyTemplateGroupRule`：

- `TemplateId`
- `GroupProfileId`
- `Effect`: `Include` 或 `Exclude`
- `CreatedByUserId`
- `CreatedAtUtc`

`TemplateId + GroupProfileId` 唯一。

规则：

- `Global` 模板默认对所有启用且未归档群生效，只允许 `Exclude` 关系。
- `SelectedGroups` 模板只对 `Include` 关系中的群生效。
- 切换作用域时应用服务必须删除或转换不合法关系，不能保留含义相反的脏数据。
- 全局模板被当前群排除后，其他群继续生效。
- 群详情和独立模板页面操作同一关系表。

### 10.4 有效模板顺序

1. 当前群的 `SelectedGroups + Include` 模板优先。
2. 未被当前群 `Exclude` 的 `Global` 模板其次。
3. 同一层级按 `Priority` 降序。
4. 再按稳定字段排序，保证候选顺序可重现。

指定群模板可以覆盖相同意图的全局模板。Agent 不确定时不得因为优先级强制
命中。

## 11. Template Routing Agent

### 11.1 输入

Agent 只接收：

- 当前群消息。
- 当前群有效模板的 ID。
- 模板版本。
- 模板名称。
- 意图说明。
- 有界数量的示例问法。
- 作用域层级和优先级。

Agent 不接收模板回复正文，避免模型改写、泄漏或把正文作为自由回答输出。
候选数量和每个模板示例数量必须有上限；超出时采用稳定的优先级和相关性候选
选择，不允许构造无界提示词。

### 11.2 互斥工具

Agent 每次只能选择一个终态工具：

```text
match_fixed_template(templateId, expectedVersion)
continue_knowledge_answer()
```

严格匹配原则：

- 只有用户问题明确属于模板意图时才能选择模板。
- 只共享宽泛主题不构成匹配。
- 多个模板意图冲突或问题包含多个业务意图时继续知识库问答。
- Agent 不得返回自定义回复正文。
- Agent 不得调用多个模板工具。

示例：

| 用户问题 | 路由 |
|---|---|
| 签证还有多久出来？ | 签证进度模板 |
| 签证什么时候能下来？ | 签证进度模板 |
| 办签证需要哪些材料？ | 知识库问答 |
| 美国签证被拒了怎么办？ | 知识库问答 |
| 我的签证怎么还没出，是不是要补材料？ | 存在复合意图，知识库问答 |

### 11.3 后端校验

`match_fixed_template` 工具必须重新读取并检查：

- 模板存在且未删除。
- 模板已启用。
- `expectedVersion` 与当前版本一致。
- 当前群仍启用且未归档。
- 当前群仍在模板有效作用域。
- 模板正文仍满足长度和内容约束。

任一检查失败都不返回正文，转入现有知识库问答并记录稳定失败代码。

### 11.4 会话与审计

固定模板的用户问题和机器人固定回复继续进入现有群会话序列，以支持后续
“为什么”等追问。

扩展现有检索/回答审计，避免创建平行回复审计体系：

- `AnswerSource = fixed_template`
- `FixedReplyTemplateId`
- `FixedReplyTemplateVersion`
- `ModelConfigurationId`
- 路由失败代码
- 脱敏后的候选和工具选择摘要

未命中模板后进入现有链路，继续使用 `knowledge`、`web_search`、
`model_knowledge` 等现有回答来源。

## 12. 管理 API

所有写操作使用现有管理授权策略，并写入统一管理审计。

建议的模板 API：

- `GET /api/admin/fixed-reply-templates`
- `GET /api/admin/fixed-reply-templates/{id}`
- `POST /api/admin/fixed-reply-templates`
- `PUT /api/admin/fixed-reply-templates/{id}`
- `POST /api/admin/fixed-reply-templates/{id}/enable`
- `POST /api/admin/fixed-reply-templates/{id}/disable`
- `DELETE /api/admin/fixed-reply-templates/{id}`
- `PUT /api/admin/fixed-reply-templates/{id}/group-rules`
- `POST /api/admin/fixed-reply-templates/preview-route`

建议的群视角 API：

- `GET /api/admin/groups/{groupId}/fixed-reply-templates`
- `POST /api/admin/groups/{groupId}/fixed-reply-templates/{templateId}/include`
- `DELETE /api/admin/groups/{groupId}/fixed-reply-templates/{templateId}/include`
- `POST /api/admin/groups/{groupId}/fixed-reply-templates/{templateId}/exclude`
- `DELETE /api/admin/groups/{groupId}/fixed-reply-templates/{templateId}/exclude`

群视角 API 和模板视角 API 必须调用同一应用服务。不得在端点内各自实现一套
作用域规则。

路由预览使用真实 Agent 和当前有效模板，但不创建发送命令、不修改会话、
不增加生产命中统计。响应返回：

- 是否匹配。
- 模板 ID、名称、版本。
- 来源层级。
- 是否覆盖全局模板。
- 调用的终态工具。
- 未匹配或降级失败代码。

响应不得返回模型原始响应、提示词、凭据或内部异常。

私聊入库运营查询建议包括：

- 批次分页。
- 批次脱敏详情。
- 条目分类和目标版本。
- 失败批次受控重试。

第一版不提供人工修改 Agent 拆分结果后再发布的编辑流程，因为用户已选择
直接自动入库。

## 13. 管理后台

### 13.1 独立模板管理

新增菜单“固定回复模板”：

- 按名称或意图、作用域、群名称和状态筛选。
- 列表显示名称、意图、示例摘要、作用域、优先级、状态和更新时间。
- 使用 Element Plus 弹框新增和编辑，不在列表页面直接铺开表单。
- 模板表单支持示例问法逐条增删、回复正文、作用域、群名称多选、优先级和
  启用状态。
- 选择指定群后至少绑定一个群。
- 全局模板可管理排除群。
- 删除、停用和影响多个群的编辑使用 `ElMessageBox` 明确确认。
- 保存携带并发版本。
- 提供“测试匹配”弹框，选择群并输入问题后显示真实路由预览。

### 13.2 群详情

群详情“知识与回答”区域新增“固定回复模板”功能：

- 查看当前群所有生效模板。
- 区分全局模板、指定群模板和当前群排除的全局模板。
- 新建当前群专属模板。
- 绑定或解除已有指定群模板。
- 为当前群排除或恢复全局模板。
- 编辑模板、启停模板和测试匹配。
- 模板绑定多个群时，编辑前提示会影响其他群。
- 提供进入独立模板管理页的链接，并自动带当前群筛选。

两个入口使用相同 API、类型和缓存刷新逻辑。任一入口修改后，另一入口重新
加载必须显示一致结果。

### 13.3 私聊与知识来源

- 会话审计增加群聊/私聊类型筛选。
- 私聊会话显示兼容身份说明，不显示或声称存在稳定成员 ID。
- 知识文档详情和版本历史显示来源分类、来源时间和变更类型。
- 私聊入库批次页面显示状态、数量统计、失败代码和重试入口。
- 不在页面展示原始模型提示词、WorkTool 凭据或完整上游响应。

### 13.4 前端质量

- 所有异步页面提供加载、空、成功和失败状态。
- 群选择统一使用现有群名称选择器，不允许手填 UUID。
- Element Plus 组件必须同时检查逻辑导入、样式导入和浏览器视觉效果。
- 弹框具有可见标题、字段标签、就地验证、键盘焦点和关闭行为。
- 表格和弹框在常用桌面宽度及窄屏下不得横向溢出。

## 14. 失败与降级

群固定模板以下情况全部继续原知识库问答：

- 没有有效模板。
- 默认模型不支持 Function Tool。
- Agent Framework 超时或不可用。
- Agent 未调用规定工具。
- Agent 调用多个终态工具。
- 模板 ID 不存在。
- 模板已停用或删除。
- 模板版本冲突。
- 当前群不在有效作用域。
- 意图不明确。

模板路由失败不得造成消息丢失、重复回复、发送队列绕过或 Worker 阻塞。

私聊普通问答失败使用安全失败文本。私聊入库失败必须区分：

- 提取失败。
- 超过条目上限。
- 标签解析失败。
- 相似知识比较失败。
- 暂存失败。
- 索引失败。
- 激活失败。
- 最终通知发送失败。

入库失败不得发送“已入库”或“已发布”。发送通知失败不回滚已经成功激活的
知识，但必须通过现有发送命令状态和死信可观察。

## 15. 幂等、并发与可靠性

- WorkTool 回调继续使用消息 ID 或现有回退去重键。
- 私聊入库批次以来源会话消息唯一。
- 模板固定回复发送命令使用消息 ID 构造稳定幂等键。
- Agent 重试不得创建第二条回复或第二个批次。
- 模板选择携带版本，执行工具时重新校验。
- 会话仍使用现有租约和序列分配，固定回复不能绕过会话所有权。
- 批次激活使用事务和当前版本检查。
- Qdrant 索引和 MySQL 激活继续作为两个状态边界处理。
- 发送仍保持现有机器人级 FIFO、限流、租约、重试和死信语义。

## 16. 安全与审计

- 不记录或返回模型 API Key、机器人标识明文、回调令牌和连接字符串。
- Agent 工具参数和结果使用有界、脱敏结构。
- 后台模板写操作需要现有管理授权。
- 私聊直接入库记录来源显示名称、消息引用、模型配置版本和最终变更。
- 所有模板新增、编辑、启停、删除、作用域和例外群修改写管理审计。
- 审计记录模板 ID 和版本，但不重复保存无界模板正文。
- 直接入库不因用户选择跳过审核而跳过输入验证、幂等、版本和索引校验。

## 17. 迁移与兼容

- 为会话增加私聊兼容字段时保留全部现有群会话。
- 新增私聊批次、条目、模板、示例和群规则表。
- 为知识版本增加来源和沿革字段；现有版本按可证明来源回填，无法精确证明时
  使用兼容来源值，不伪造具体来源消息。
- 为现有检索审计增加可空模板引用和版本。
- 创建或确认系统“全局知识”标签。
- 迁移必须兼容生产 MySQL 5.7，不依赖该版本不会强制执行的 `CHECK` 约束。
- EF 实体、映射、迁移和模型快照保持一致。
- 不重写已应用迁移。

## 18. 测试与验收

### 18.1 单元测试

- 私聊入库触发首行和房间类型边界。
- 私聊作用域规范化和哈希稳定性。
- 最多 20 条和批次分类。
- 标签精确、语义、新建和全局知识回退。
- New、Duplicate、Supplement、Correction 验证。
- 全局模板默认生效。
- 全局模板当前群排除。
- 指定群模板绑定和解除。
- 指定群模板优先于全局模板。
- 同层优先级稳定排序。
- 无效模板、停用模板和版本冲突拒绝。
- 路由失败继续知识库问答。

### 18.2 Agent 和外部合约测试

- 明确模板问题调用 `match_fixed_template`。
- 非模板和模糊问题调用 `continue_knowledge_answer`。
- Agent 不能用自由文本替代固定模板工具。
- 多工具、未知工具和未知模板 ID 被拒绝。
- 不支持 Function Tool 的模型安全降级。
- WorkTool `roomType=2/4` 真实回调样例保持合约兼容。
- 私聊发送请求符合已验证 WorkTool 契约。

### 18.3 集成测试

- MySQL 5.7 迁移通过。
- 私聊回调幂等创建会话和任务。
- 私聊问答检索全部已发布知识。
- 外部联系人不能直接入库。
- 内部联系人入库批次幂等。
- 全批索引成功后一次激活。
- 任一索引失败时旧版本保持有效。
- 来源、变更类型和被替代版本正确。
- 模板 CRUD、乐观并发和管理审计。
- 独立模板页面和群详情操作同一群规则。
- 固定回复只生成一条发送命令。
- 固定回复进入会话上下文。
- 检索/回答审计记录模板 ID、版本和回答来源。
- WorkTool 发送失败继续使用现有重试和死信。

### 18.4 前端与端到端测试

- 模板列表加载、分页、筛选、空状态和失败状态。
- 新增和编辑弹框验证。
- 群名称多选和全局例外群管理。
- 群详情绑定、解除、排除、恢复和测试匹配。
- 独立页面与群详情数据一致。
- 知识来源和私聊批次页面。
- 删除和高影响修改使用 Element Plus 确认框。
- Element Plus 逻辑和样式完整加载。
- 关键流程浏览器视觉验收无溢出、无无样式组件。

## 19. 实施顺序

1. 固化现有模型和回答特征测试。
2. 引入稳定 Agent Framework 包，新增 `IChatClient` 兼容层和真实模型的
   Function Tool、工具结果循环及结构化输出探针。
3. 扩展会话模型以支持私聊，并接入 `roomType=2/4` Durable Job。
4. 实现私聊普通知识问答。
5. 增加知识来源和版本沿革字段。
6. 实现私聊直接入库批次、标签匹配、相似比较、索引和激活。
7. 实现固定模板、示例、群作用域和管理 API。
8. 在现有允许回复策略之后增加 Template Routing Agent 和安全降级。
9. 实现独立模板管理页面。
10. 实现群详情固定回复模板功能。
11. 实现私聊批次、知识来源和审计展示。
12. 完成迁移、单元、合约、集成、前端和端到端验证。
13. MessageIntentAgent 接管后，将模板路由移动到 `ReplySelected` 之后并执行
    架构级设计规定的回归验收；不重复实现第二套路由。

## 20. 已批准决策

- 使用 Microsoft Agent Framework，而不是 Semantic Kernel。
- 本功能首次交付中，Agent Framework 只负责私聊 Agent 和群固定模板路由。
- 本功能不整体重写现有群知识问答链；后续模型执行迁移遵循架构级渐进迁移
  设计，不改变 RAG、记忆、权限和可靠性业务真相。
- 私聊普通问答支持 `roomType=2` 和 `roomType=4`。
- 私聊直接入库仅支持 `roomType=4`。
- 直接入库不使用口令或人工审核。
- 入库首行使用严格标记 `#知识入库`。
- 一次最多整理 20 条问答。
- 没有可靠标签时进入全局知识。
- 补充和纠正自动创建并激活新版本。
- 知识来源按文档版本记录。
- 固定模板第一版只返回固定正文，不查询真实签证进度。
- Agent 通过 Function Tool 区分固定模板与知识库问答。
- 固定模板采用严格意图匹配，拿不准就进入知识库。
- 支持全局模板和指定群模板。
- 指定群模板优先于全局模板。
- 全局模板允许单群停用例外。
- 独立模板页面和群详情都能管理模板生效群。
