# WorkTool 契约纠偏与前后端一致性设计规格

**日期：** 2026-07-24
**状态：** 已完成方案确认，等待书面规格复核
**适用工作树：** `H:\Codex\WechatRobot\.worktrees\codex-wechatrobot-mvp`
**目标分支：** `codex/wechatrobot-mvp`

## 1. 背景与核心决策

当前前端没有调用不存在后端路由的悬空请求，但仍存在三类缺口：

1. 前端已有页面，后端没有支撑页面工作的 API。
2. 后端已有接口或能力，前端没有操作入口。
3. 后端已有持久化和内部处理能力，但缺少可审计的运营 API。

在补这些缺口之前，必须先修正 WorkTool 接入层的真实性问题。现有实现中，发送消息和群操作的基础请求体大体匹配 WorkTool 文档，但连接测试、消息回调配置、指令结果确认、群身份语义和真实验收存在实质偏差。如果先补页面，错误契约会被固化到更多前后端接口中。

本设计采用以下边界：

- WorkTool 官方文档是机器人收发消息、机器人状态、回调配置和群操作的唯一外部契约来源。
- 知识库、审核、后台账号、系统设置、队列和运营审计属于本系统内部能力，不要求复制 WorkTool 控制台。
- 内部能力不得伪装成 WorkTool 官方能力；所有本地增强必须在命名、接口和文档中明确标记为本系统行为。
- 人工转接、企业微信成员目录、企业成员与系统账号映射、主动转接和转接暂停策略延期到独立阶段。
- 用户与角色的后台管理仍属于本轮范围，但不接入人工转接成员映射。
- WorkTool 文档未公开明确字段名的能力不得通过猜测字段实现。

## 2. 方案选择

### 2.1 未采用：先补前端缺口

该方案可以较快让标签、文档和工作台页面出现数据，但会保留错误的机器人连接测试、错误的回调绑定语义和“接口接受即执行成功”的状态模型。后续修复会再次修改机器人页面、操作审计、工作台统计和测试。

### 2.2 采用：先纠正外部契约，再逐个完成垂直闭环

先建立可信的 WorkTool 接入基础，再按标签、文档、系统设置、工作台、机器人管理、用户角色和剩余操作入口逐项交付。每个阶段都包含数据库、后端 API、前端页面、测试、文档和独立验收。

该方案变更范围可控，能够在每个阶段结束时得到可运行、可回滚、可审查的闭环，因此作为正式实施路线。

### 2.3 未采用：一次性重做全部后台

该方案可以统一清理旧字段和旧页面，但数据库迁移、WorkTool 外部副作用、运行时设置和运营 API 会同时变化，回归面过大，也不利于区分协议错误与普通页面缺口。

## 3. 真实性审计结论

### 3.1 已与 WorkTool 对齐的部分

| 能力 | 当前实现 | 官方依据 | 结论 |
|---|---|---|---|
| 消息回调三秒内响应 | 回调只做验证和持久化，后续异步处理 | WorkTool 消息回调接口规范 | 方向正确 |
| 消息回调 URL 携带自定义参数 | 使用随机路由码和查询参数密钥 | 官方允许 URL 携带参数区分机器人 | 正确的本地安全增强 |
| 群文本识别 | 使用 `roomType=1`、`textType=1` | WorkTool 回调字段定义 | 符合当前外部群文本范围 |
| 文本发送 | `socketType=2`、`type=203`、`titleList`、`receivedContent`、`atList` | WorkTool 发送消息 | 基础请求体对齐 |
| 创建外部群 | `type=206`、`groupName`、`selectList`、`groupAnnouncement` | WorkTool 创建外部群 | 已公开字段对齐 |
| 修改群 | `type=207`、`newGroupName`、`newGroupAnnouncement`、`selectList`、`removeList` | WorkTool 修改群信息 | 已公开字段对齐 |
| 请求限流 | 系统限制不超过每分钟 60 次 | WorkTool 文档声明所有接口 60 QPM | 上限对齐 |

### 3.2 必须在 P0 修正的契约问题

#### 3.2.1 机器人连接测试调用错误接口

当前 `TestConnectionAsync` 调用 `GET /wework/robot`。WorkTool 官方机器人信息接口是：

```text
GET /robot/robotInfo/get?robotId={robotId}
```

官方示例的业务成功码为 `200`，而消息发送和群操作示例通常使用 `0`。因此不能使用一个只认 `code=0` 的通用解析器处理全部 WorkTool 接口。

修正后必须为不同端点定义明确响应契约：

- 指令提交：接受 `code=0`，读取并保存 `data` 中的 WorkTool `messageId`。
- 机器人信息：接受文档定义的 `code=200`，验证返回的 `robotId` 和必要状态字段。
- 回调配置：按对应文档验证 `code`，不得仅依赖 HTTP 200。

连接测试返回本系统定义的结构化状态，不返回机器人 ID：

```text
configured
reachable
online
messageCallbackEnabled
replyAllEnabled
checkedAtUtc
failureCode
```

其中 `reachable` 表示 WorkTool API 可访问且凭据有效，`online` 必须来自官方在线查询或机器人信息中的明确字段，不能由 `reachable` 推断。

#### 3.2.2 当前“回调绑定”不是消息回调配置

当前后台只调用：

```text
POST /robot/robotInfo/callBack/bind
```

该接口用于群二维码、指令结果、上线和下线等配置事件回调。真正的消息回调应调用：

```text
POST /robot/robotInfo/update?robotId={robotId}
```

请求体至少包含官方公开的：

```json
{
  "openCallback": 1,
  "replyAll": 1,
  "callbackUrl": "https://example/api/worktool/callback/{routeCode}?token={secret}"
}
```

新的机器人管理流程必须明确拆分：

- “配置消息回调”：控制新消息接收和是否回复所有消息。
- “配置指令结果回调”：绑定类型 `1`，用于确认 203、206、207 等指令的机器人端执行结果。
- “查询回调状态”：读取 WorkTool 已配置回调，展示消息回调和事件回调是否齐全。
- “删除事件回调”：仅删除明确类型，不得把消息回调与事件回调混为一谈。

#### 3.2.3 回调密钥无法重建

现有数据库只保存消息回调密钥的哈希。系统内部要自动调用 WorkTool 配置消息回调，就必须能够构造完整回调 URL。

修正方案：

- 新增加密保存的回调密钥字段，使用现有 `ISecretProtector` 和主密钥保护。
- 继续保存哈希，用于入站请求的常量时间比较。
- API 和前端永不返回明文密钥。
- 新机器人创建时生成高熵随机密钥并同时保存密文和哈希。
- 旧机器人首次配置消息回调时执行一次受审计的密钥轮换。
- 回调 URL、日志、审计详情和异常消息必须隐藏查询参数密钥。

该设计与机器人 ID、模型 API Key 的加密保存方式一致。密钥仍不得写入配置文件、日志、测试快照或 Git。

#### 3.2.4 指令接受不等于执行成功

WorkTool 指令接口成功响应中的 `data` 是 `messageId`。机器人端稍后执行指令，并通过类型 `1` 回调返回：

```text
messageId
errorCode
errorReason
runTime
timeCost
type
successList
failList
```

当前系统丢弃 `data`，并在 HTTP 接受后直接把群操作标记为 `Succeeded`。这会把“排队成功”虚报为“企微端执行成功”。

新的统一状态机为：

```text
Queued
  -> Dispatching
  -> DispatchFailed
  -> Accepted
  -> ExecutedSucceeded
  -> ExecutedPartially
  -> ExecutedFailed

Dispatching
  -> Rejected

Dispatching
  -> DeliveryUnknown

Accepted
  -> ResultTimeout
```

规则如下：

- `Accepted` 只表示 WorkTool API 接受并返回 `messageId`。
- `Rejected` 表示 WorkTool 返回明确的业务拒绝且没有接受指令。
- `DispatchFailed` 表示请求在发送前或明确未提交时失败。
- `ExecutedSucceeded` 必须来自类型 `1` 回调或官方结果查询。
- `successList` 和 `failList` 同时非空时为 `ExecutedPartially`。
- 非零 `errorCode` 为 `ExecutedFailed`，保存稳定错误码和脱敏原因。
- HTTP 超时或无法判断是否已提交时为 `DeliveryUnknown`，不得自动重发非幂等群操作。
- 已接受但超过配置时限未收到结果时为 `ResultTimeout`，进入运营核对，不冒充失败或成功。
- 普通消息发送也保存 WorkTool `messageId`，但其运营展示可以与群操作分开。

#### 3.2.5 配置回调端点只返回成功但不处理数据

当前 `/api/worktool/config-callback/{robotCode}` 只检查机器人存在并返回 HTTP 200。新的设计拆成语义明确的端点：

```text
POST /api/worktool/callback/{routeCode}
POST /api/worktool/command-results/{routeCode}
POST /api/worktool/lifecycle/{routeCode}
```

其中：

- 消息回调验证 token、校验消息 DTO、去重并持久化任务。
- 指令结果回调验证 token、严格校验结果 DTO、通过 `messageId` 更新发送命令或群操作状态。
- 生命周期回调仅在本轮确有页面消费时实现；否则不绑定类型 `5/6`。
- 群二维码回调不在本轮需求中，不绑定类型 `0`。
- 所有回调都允许 WorkTool 绑定时的验证请求，但不得因为支持验证请求而忽略真实业务载荷。

#### 3.2.6 `ExternalGroupId` 是无可靠来源的本地语义

WorkTool 消息回调提供 `groupName` 和 `groupRemark`，不提供稳定群 ID。已标记将废弃的 WorkTool 群列表也不提供可用于当前流程的稳定群成员身份。

本轮不引入企业微信官方客户群同步，因此群身份按 WorkTool 可观察字段处理：

- `GroupProfile.Name` 保存 WorkTool `groupName`。
- 新增 `GroupProfile.WorkToolGroupRemark`，可空。
- `ExternalGroupId` 标记为旧字段，不再让用户填写，也不再作为 WorkTool 稳定 ID 展示。
- 数据迁移保留旧值，但只有经过管理员确认其实际是群备注时才迁入 `WorkToolGroupRemark`。
- 入站匹配同时使用 `groupName` 和 `groupRemark`。
- 发送和 207 操作优先使用非空群备注，否则使用群名。
- 同一机器人下出现无法唯一匹配的重名群时，不自动回复并记录 `group_identity_ambiguous`。
- 页面必须明确说明群名和群备注都不是跨系统稳定 ID。

#### 3.2.7 `MemberIds` 命名错误

WorkTool `selectList` 和 `removeList` 在公开文档和现有 UI 中使用成员显示名，不是 `userid`、`external_userid` 或其他稳定 ID。

本轮统一改名为：

```text
MemberDisplayNames
```

该字段只代表 WorkTool 按名称执行操作。页面必须提示重名风险，不得声称成员已完成稳定身份同步。

#### 3.2.8 回调脚本与当前模型漂移

现有 `update-worktool-callback.ps1`：

- 使用机器人 ID 作为本系统回调路由。
- 未使用随机 `CallbackRouteCode`。
- 请求体不是 WorkTool 官方消息回调配置结构。
- 期待 `{ success: true }`，而不是 WorkTool 官方响应包络。

脚本必须改为调用本系统受鉴权的机器人消息回调配置 API，由后端解密机器人 ID、构造安全回调 URL并调用 WorkTool。脚本不得直接接收或打印真实机器人 ID与回调密钥。

#### 3.2.9 “真实验收”证据不足

现有 WorkTool 契约测试全部使用假 HTTP Handler。测试通过只证明代码与测试中的假契约一致，不能证明契约与官方接口一致。

新的验证分层为：

1. 官方契约测试：使用官方文档中的路径、请求和响应样本。
2. 本地集成测试：验证数据库状态机、回调鉴权、结果关联和脱敏。
3. 可选真实连接测试：只读调用机器人信息、在线状态和回调查询。
4. 明确授权的真实消息测试：发送唯一测试文本，并等待对应 `messageId` 的执行结果回调。
5. 单独授权的真实群变更测试：使用专用测试群，验证 206/207 的结果回调和企微端状态。

没有收到对应 `messageId` 的成功结果回调时，不得把真实验收记录为通过。

### 3.3 不允许猜测实现的 WorkTool 能力

WorkTool 页面文字提到群备注和群模板，但当前公开请求示例没有给出字段名。错误码中存在修改群备注失败码，也不能单独证明请求字段名称和格式。

因此：

- 本轮不猜测群备注修改字段。
- 本轮不猜测群模板字段。
- 获得新版官方明确字段表、官方支持答复或脱敏真实请求样本后，再单独设计。
- 当前 206/207 只实现公开示例可证实的字段。

## 4. 总体架构

### 4.1 外部连接层

`IWorkToolClient` 只负责官方 WorkTool HTTP 契约：

- 机器人信息和在线状态查询。
- 消息回调配置。
- 事件回调查询、绑定和按类型删除。
- 203、206、207 指令提交。
- 指令执行结果查询，作为回调丢失后的受控核对手段。

每个方法返回自己的强类型结果，不再使用一个含糊的 `WorkToolSendResult` 表示所有接口。

### 4.2 应用编排层

应用层负责本系统行为：

- 机器人凭据和回调密钥轮换。
- WorkTool `messageId` 与本地命令关联。
- 指令状态机和超时核对。
- 审计、限流、重试和脱敏。
- 群名与群备注的匹配策略。

WorkTool 客户端不得直接修改数据库；应用服务不得自行拼接未定义的 WorkTool JSON。

### 4.3 管理 API

管理 API 返回运营语义，不暴露外部秘密：

- 机器人“已配置”不等于“在线”。
- 群操作“已接受”不等于“已执行”。
- 回调“已绑定”必须区分消息回调与指令结果回调。
- 所有外部调用失败使用稳定 `failureCode`，原始响应只允许进入受控、脱敏日志。

### 4.4 前端

前端只消费后端强类型状态：

- 不自行推断在线状态。
- 不把 HTTP 202 显示为执行成功。
- 不要求管理员手填无法从真实系统获得的 ID。
- 对有外部副作用的操作继续使用预览和二次确认。
- 对 `Accepted`、`ExecutedSucceeded`、`ExecutedFailed`、`DeliveryUnknown` 等状态使用不同文案。

## 5. 分阶段交付

### 5.1 P0：WorkTool 契约纠偏

交付内容：

- 强类型 WorkTool 请求、响应和端点级成功码处理。
- 正确的机器人信息与在线查询。
- 正确的消息回调配置和指令结果回调绑定。
- 加密回调密钥、哈希验证和旧数据轮换。
- 保存 WorkTool `messageId`。
- 指令结果回调处理和新状态机。
- 群名/群备注匹配，停用虚构的 `ExternalGroupId` 输入。
- `MemberDisplayNames` 命名和 UI 风险提示。
- 修正回调配置脚本。
- 修正真实验收门槛。

验收标准：

- 所有 WorkTool 路径、请求字段和响应码均可追溯到官方文档。
- 假 Handler 契约测试覆盖官方样本。
- HTTP 接受后状态为 `Accepted`，不是 `Succeeded`。
- 对应成功回调到达后才进入 `ExecutedSucceeded`。
- 无结果回调时进入 `ResultTimeout`，不虚报成功。
- 机器人信息查询不再调用 `/wework/robot`。
- 后台可以分别看到消息回调和指令结果回调状态。
- 普通自动化测试不触发真实发送或群变更。

### 5.2 P1：知识标签闭环

交付内容：

- 标签分页列表、创建、编辑、启停和删除 API。
- 规范化名称唯一性和乐观并发版本。
- 全局公开标记。
- 被群、文档分段、候选审核或历史记录引用时的删除约束。
- 管理审计。
- 标签管理页面。
- 文档索引、群配置和知识审核中的标签选择器。

删除规则：

- 已被引用的标签不能物理删除，可以停用。
- 未被引用的标签允许管理员二次确认后删除。
- 停用标签不能用于新绑定或新索引，但历史审计仍能显示名称。

验收标准：

- 页面不再要求手填标签 UUID。
- 并发修改返回 409 和当前版本。
- 标签变更写入 `AdministrationAudits`。

### 5.3 P1：知识文档管理闭环

交付内容：

- 文档分页列表。
- 文档详情。
- 版本历史。
- 上传、解析、OCR、分段、索引和删除状态。
- 失败上传列表及重试入口。
- 停用和受控物理删除入口。
- 从列表进入现有分段详情。

API 不返回不必要的暂存内容、OCR 原始秘密、OSS 凭据或签名参数。公共读 URL 风险提示继续保留。

验收标准：

- 上传后可以重新找到文档。
- 每个版本的失败原因和当前处理阶段可见。
- 重试只对可重试状态开放。
- 删除使用二次确认并保留审计。

### 5.4 P1：系统设置闭环

`system_setting` 不作为任意 JSON 编辑器。新增显式的设置注册表，每个设置必须声明：

- 稳定键名。
- 强类型 DTO。
- 默认值。
- 校验范围。
- 是否支持运行时生效。
- 读取该设置的运行时服务。
- 是否属于秘密。

第一批只开放已经有明确运行时消费方的非秘密设置。保存但不读取的设置不得出现在页面。

交付内容：

- 设置读取和更新 API。
- 乐观并发版本。
- 变更审计。
- 历史版本和回滚。
- 运行时读取服务。
- 设置页面。

验收标准：

- 保存后通过真实运行时服务读取验证生效。
- 不支持热更新的设置明确显示“重启后生效”。
- 秘密配置不进入通用系统设置表。
- 回滚产生新的版本和审计，不删除历史。

### 5.5 P1：工作台和运营汇总

新增一个面向工作台的聚合 API，避免前端自行调用多个数据库语义接口拼装指标。

汇总内容：

- 机器人总数、启用数、可达数和在线数。
- WorkTool 消息回调与指令结果回调配置状态。
- 知识文档、版本、待审核候选和失败任务数量。
- Durable Job 和发送命令的状态统计。
- 死信数量。
- MySQL、Qdrant、OCR、OSS 和 Worker readiness。
- 人工转接统计本轮不纳入工作台。

验收标准：

- 所有数字来自数据库或健康探针。
- 健康失败不导致整个工作台无法显示其他数据。
- readiness 的必需组件失败仍保留 HTTP 503 语义。
- 前端显示最后检查时间和降级原因。

### 5.6 P1：机器人完整管理

在 P0 正确契约之上提供：

- 新建机器人配置。
- 更新名称、启停和限流。
- 设置或轮换 WorkTool 机器人 ID。
- 只读连接测试。
- 消息回调配置。
- 指令结果回调配置。
- 回调查询和状态展示。
- 敏感字段元数据和审计。

页面和 API 永不返回机器人 ID 明文，只显示“已配置”和不可逆指纹或尾部元数据。

机器人停用继续阻止新发送并安全处理已排队任务。重新启用前必须通过明确的凭据和连接检查。

### 5.7 P2：用户与角色管理

本轮只实现后台 Identity 管理：

- 用户分页列表。
- 创建或邀请后台用户。
- 启用、禁用。
- 分配和移除已有系统角色。
- 防止删除或禁用最后一个可用管理员。
- 审计。

不实现：

- 企业微信成员同步。
- `EnterpriseMember`。
- 用户与企业微信成员映射。
- 人工转接客服选择器。
- WorkTool `atList` 的客服映射。

### 5.8 P2：剩余知识和审计入口

交付内容：

- 分段预览删除按钮和确认。
- Smart、Separator、Regex、QA 分段策略。
- 长度、重叠和策略特有参数。
- 文档重试、停用和删除入口。
- 会话审计按群和 UTC 时间过滤。
- 群操作审计范围由 API 返回，不再前端硬编码。
- 统一管理审计查询页面。

群配置中的人工转接暂停策略延期。普通群配置的 `ConfigurationVersion` 仍必须在本轮接入前端，所有配置更新都带 `ExpectedConfigurationVersion`，避免后写覆盖。

## 6. 人工转接延期范围

以下能力不在本轮实施：

- 企业微信 `CorpId` 和客户联系 Secret 配置。
- 客户群列表与详情同步。
- `EnterpriseMember` 和 `GroupMember`。
- `ApplicationUser` 与企业成员绑定。
- 人工客服选择器。
- 主动发起转人工。
- 群级和发送人级暂停策略页面。
- WorkTool 按客服企业微信显示名发送 `@` 通知。

现有人工转接功能保持可运行，但本轮不扩大其身份能力，也不宣称已经完成稳定企业成员同步。

## 7. 运营 API 约束

### 7.1 Durable Job、发送命令和死信

后续工作台和运营 API 只暴露：

- 分页和状态统计。
- 脱敏错误摘要。
- 创建时间、尝试次数、下次尝试时间和租约状态。
- 受控重试资格。

不得返回完整消息正文、模型输入、机器人 ID、回调密钥、Authorization Header 或原始外部响应。

非幂等 WorkTool 群操作处于 `DeliveryUnknown` 时不得一键重放。只有确认未执行或使用全新人工确认后才能再次提交。

### 7.2 管理审计

标签、文档删除、系统设置、用户角色、机器人凭据、回调配置和群操作统一写入 `AdministrationAudits` 或专用 WorkTool 审计，并提供统一查询视图。

审计记录包含：

- 操作者。
- 动作。
- 目标类型和 ID。
- 脱敏变更摘要。
- 时间。
- 外部操作的本地审计 ID 和 WorkTool `messageId` 指纹。

## 8. 错误处理

- 输入错误返回 400 和字段级错误。
- 乐观并发冲突返回 409 和当前版本。
- 外部凭据无效返回稳定的 502 业务错误，不返回原始机器人 ID。
- WorkTool 限流返回可识别的 `worktool_rate_limited`，按接口幂等性决定是否重试。
- WorkTool HTTP 超时在可能已提交时进入 `DeliveryUnknown`。
- WorkTool 业务拒绝且确认未提交时进入 `Rejected` 或 `DispatchFailed`。
- 回调载荷无法关联时保存脱敏孤立事件供运营核对，不静默丢弃。
- 回调重复时幂等返回成功，不重复改变最终状态。
- 数据库暂时失败时回调返回非 200，使 WorkTool 绑定验证或调用明确失败；指令结果回调官方不重试，因此同时保留结果查询核对路径。

## 9. 测试策略

每个阶段按测试驱动方式实施：

1. 先写失败的单元、契约或集成测试。
2. 运行目标测试确认失败原因符合预期。
3. 实现最小功能。
4. 运行目标测试。
5. 运行受影响项目测试。
6. 运行完整解决方案构建、前端类型检查、前端测试和生产构建。

WorkTool P0 额外要求：

- 官方路径和请求体快照测试。
- `code=0` 与 `code=200` 的端点级解析测试。
- `data=messageId` 持久化测试。
- 指令结果回调的成功、部分成功、失败、重复、未知消息和乱序测试。
- 回调密钥加密、哈希比较和脱敏测试。
- 群重名、备注匹配和歧义拒绝测试。
- `Accepted` 不得被前端显示为执行成功。
- 真实测试默认跳过，并要求显式环境开关和专用目标确认。

标准验证命令必须适配当前 Microsoft Testing Platform。测试过滤使用测试宿主支持的参数，例如：

```powershell
dotnet test tests\server\WechatRobot.ContractTests\WechatRobot.ContractTests.csproj --no-restore -- --filter-namespace 'WechatRobot.ContractTests.WorkTool' --minimum-expected-tests 1
```

不得使用当前宿主不支持的传统 `--filter` 形式。

## 10. 数据迁移原则

- 不删除现有业务数据。
- 新字段先允许空值，完成回填后再收紧约束。
- `ExternalGroupId` 在确认迁移语义前保留但停止新增依赖。
- 旧机器人缺少可恢复回调密钥时，在管理员首次配置消息回调时轮换。
- 指令审计新增状态和 WorkTool `messageId` 字段时保留现有记录。
- 旧 `Succeeded` 记录标记为“旧版本仅确认接口接受”，不得批量改写成 `ExecutedSucceeded`。
- 每个迁移都有升级和回滚说明，并在 MySQL 集成测试中验证。

## 11. 实施文档结构

由于本规格包含多个可独立验收的子系统，实施阶段采用一个总路线和多个详细计划：

1. WorkTool 契约纠偏计划。
2. 知识标签闭环计划。
3. 知识文档管理计划。
4. 系统设置闭环计划。
5. 工作台与运营汇总计划。
6. 机器人完整管理计划。
7. 用户与角色管理计划。
8. 剩余知识、群配置并发与审计入口计划。

总路线记录跨阶段依赖和完成状态。每个详细计划列出准确文件、接口、测试、命令、预期结果和提交边界。只有前一阶段的验收门槛通过后，才进入依赖它的下一阶段。

## 12. 完成定义

本轮完成必须同时满足：

- WorkTool 外部契约无已知虚构路径、字段或成功语义。
- 所有已实现 WorkTool 能力都有官方依据或明确标记为本系统增强。
- 前端不存在静态占位但声称可用的页面。
- 后端运营能力有对应入口，或在文档中明确列为暂不提供。
- 前端 API 类型完整保存后端并发版本和真实状态。
- 标签、文档、系统设置、工作台、机器人、用户角色和剩余 P2 入口分别通过验收。
- 人工转接延期项没有被混入或伪装完成。
- 解决方案构建零错误。
- 服务端目标测试、合约测试和相关集成测试通过。
- 前端类型检查、测试和生产构建通过。
- 真实 WorkTool 验收只在收到匹配 `messageId` 的执行结果后声明成功。

## 13. 官方资料

1. [WorkTool 消息回调接口规范](https://worktool.apifox.cn/doc-861677)
2. [WorkTool 发送消息](https://worktool.apifox.cn/api-23520034)
3. [WorkTool 创建外部群](https://worktool.apifox.cn/api-23520350)
4. [WorkTool 修改群信息](https://worktool.apifox.cn/api-23520590)
5. [WorkTool 获取机器人信息](https://worktool.apifox.cn/api-26343758)
6. [WorkTool 查询机器人是否在线](https://worktool.apifox.cn/api-39271192)
7. [WorkTool 机器人消息回调配置](https://worktool.apifox.cn/api-22587884)
8. [WorkTool 机器人配置回调](https://worktool.apifox.cn/api-43942595)
9. [WorkTool 查询机器人回调](https://worktool.apifox.cn/api-44588019)
10. [WorkTool 删除机器人回调](https://worktool.apifox.cn/api-193710173)
11. [WorkTool 机器人回调接口标准](https://worktool.apifox.cn/api-44952776)
12. [WorkTool 指令消息 API 调用查询](https://worktool.apifox.cn/api-32976490)
13. [WorkTool 指令执行结果查询](https://worktool.apifox.cn/api-43575628)
14. [WorkTool 群列表查询](https://worktool.apifox.cn/api-21488853)
15. [WorkTool 历史消息列表查询](https://worktool.apifox.cn/api-21488859)
16. [WorkTool 错误码](https://worktool.apifox.cn/doc-1997270)
