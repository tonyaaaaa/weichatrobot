# 私聊匹配全部固定回复模板设计

## 1. 目标

让 WorkTool 外部联系人私聊（`roomType=2`）和内部联系人私聊
（`roomType=4`）在普通问答前使用 Agent Framework 匹配固定回复模板。

私聊候选范围为全部已启用、未删除的固定回复模板：

- 包含“全局”模板。
- 包含所有“指定群”模板。
- 私聊不应用指定群的包含、排除或群启用状态。
- 不要求私聊建立或绑定 `GroupProfileId`。

## 2. 非目标

- 不改变群聊固定回复模板的作用域、优先级、包含或排除语义。
- 不让停用或已删除模板参与私聊匹配。
- 不为私聊新增模板副本、联系人规则、后台开关或数据库迁移。
- 不使用 `Guid.Empty` 冒充群 ID。
- 不让固定回复模板处理 `#知识入库` 命令。

## 3. 处理顺序

私聊消息进入现有 Durable Job 后：

1. 建立或续接私聊会话。
2. 执行现有私聊命令解析。
3. `roomType=2` 的不支持入库提示和 `roomType=4` 的直接入库命令按现有逻辑
   立即处理，不进入模板路由。
4. 普通私聊调用 `TemplateRoutingAgent` 的私聊入口。
5. 路由 Agent 读取全部已启用、未删除模板及有界示例，返回匹配模板或继续知识
   回答。
6. 命中模板后，服务端重新校验模板 ID、版本、启用和删除状态，再读取固定正文。
7. 校验成功时，通过现有私聊会话和发送队列发送固定正文，并记录
   与群聊一致的 `fixed_template` 来源审计。
8. 未命中、Agent 不可用、超时、输出无效、未知模板、版本变化或模板失效时，
   继续现有私聊回答链：

```text
AnswerAgent -> 全部启用知识 -> Web Search -> 大模型知识 -> 安全失败文本
```

## 4. 组件边界

### 4.1 模板存储

固定模板存储增加明确的私聊候选和解析操作。私聊查询只检查：

- `IsEnabled = true`
- `DeletedAtUtc = null`
- 候选数量和示例数量上限
- 既有优先级和稳定排序

私聊查询不得调用群启用检查，也不得读取群包含或排除规则决定可见性。

### 4.2 Template Routing Agent

`ITemplateRoutingAgent` 增加私聊路由入口。群聊入口继续接收真实
`GroupProfileId` 并保持现有行为；私聊入口不接收伪造群 ID。

两个入口复用同一套 Agent Framework 工具约束和结构化终态：

- `match_fixed_template`
- `continue_knowledge_answer`

### 4.3 Private Chat Processor

`PrivateChatProcessor` 在普通问答前调用私聊模板路由。命中后不再调用
`IAnswerAgent`；未命中或安全降级时继续调用 `IAnswerAgent`。

固定回复继续使用现有 `ReplyAsync`、发送幂等键、重试和死信链路。

## 5. 安全与审计

- Agent 只能选择服务端提供的候选模板，不能生成或修改固定正文。
- 命中后必须重新校验版本和状态，防止候选读取后模板发生变化。
- 固定回复正文仍经过模板管理端既有长度和内容验证。
- 私聊检索审计记录：
  - `AnswerSource = fixed_template`
  - `FixedReplyTemplateId`
  - `FixedReplyTemplateVersion`
  - 模型配置 ID
- 不记录模型原始响应、隐藏提示词、API Key、机器人凭据或秘密 URL。

## 6. 测试与验收

先写失败测试并确认当前私聊不会调用模板路由，再实现最小代码。

至少覆盖：

- 私聊候选包含全局模板和所有指定群模板。
- 私聊候选排除停用和已删除模板。
- 私聊候选忽略群包含、排除和群启用状态。
- `roomType=2` 和 `roomType=4` 普通问答均先调用 Template Routing Agent。
- 模板命中时返回固定正文，不调用 `IAnswerAgent`。
- 模板命中写入 `fixed_template`、模板 ID 和模板版本审计。
- 模板未命中或解析失败时继续 `IAnswerAgent`。
- `#知识入库` 及外部联系人不支持入库提示不调用模板路由。
- 群聊固定模板回归测试保持通过。
- `git diff --check` 通过。

如果没有使用已授权的真实默认模型执行私聊模板匹配，只能声明自动化测试通过，
不能宣称真实 Agent 模型路由已完成外部验收。
