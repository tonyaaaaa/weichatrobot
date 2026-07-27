# 群管理与企业微信同步规格

## 1. 文档状态

- 状态：待产品批准
- 日期：2026-07-27
- 适用系统：WechatRobot 管理后台、API、Worker
- 适用群类型：企业微信客户群（外部群）
- 外部系统：企业微信客户联系 API、WorkTool

本规格整合并取代以下文档中的群管理业务边界：

- `2026-07-25-group-configuration-selection-design.md`
- `2026-07-25-group-management-list-and-configuration.md`

`2026-07-24-groups-compact-layout-design.md` 中关于规则编辑器紧凑布局的要求继续有效，
但页面信息架构以本规格为准。

## 2. 背景与现状

当前系统已经具备：

- 本地登记群列表。
- 按本地 `GroupProfileEntity.Id` 进入群配置。
- 群匹配规则、知识标签和上下文配置。
- WorkTool 新建群、增减成员、改群名和更新群公告。
- WorkTool 命令预览、二次确认、异步执行状态和操作审计。
- 人工转接案件的 `AssigneeUserId` 字段。

当前系统仍存在以下问题：

- “群列表”实际是本地登记数据，不是企业微信实时客户群目录。
- 登记群和远程操作需要手填机器人配置 ID。
- 群成员只能输入显示名，无法从真实客户群成员中选择。
- WorkTool 创建群后不会可靠返回企业微信稳定 `chat_id`，本地还需再次登记。
- WorkTool 改群名后，本地名称不会从企业微信自动校准。
- “登记群”“群配置”“远程群操作”“命令审计”集中在一个页面，操作步骤不清晰。
- `ApplicationUser` 只表示后台登录账号，不能代表企业微信员工身份。
- 系统没有企业微信客户群、内部员工和群成员的同步模型。
- 系统不能从真实群成员中筛选可接单的人工客服。

## 3. 核心结论

群管理采用双通道架构：

- 企业微信官方 API 是客户群、稳定群 ID、群主、群成员和成员身份的权威数据源。
- WorkTool 是消息收发和群操作执行器，不作为稳定群目录或成员目录。
- WechatRobot 数据库保存同步副本、机器人绑定、AI 配置、客服配置和审计记录。

系统不再要求管理员反复手填群名、机器人配置 ID或成员显示名。管理员只需：

1. 配置一次企业微信授权。
2. 将每个 WorkTool 机器人一次性绑定到对应企业微信内部员工。
3. 将企业微信员工一次性绑定到后台账号并授予 `HumanAgent` 角色。
4. 在群配置中选择默认客服或客服组。

## 4. 平台能力边界

### 4.1 企业微信负责

- 获取客户群列表和稳定 `chat_id`。
- 获取客户群名称、群主、管理员、创建时间和群成员。
- 区分企业内部员工 `userid` 与外部联系人 `external_userid`。
- 获取授权可见范围内的企业内部员工目录。
- 通过客户群变更事件通知成员、群主、群名和群公告变化。
- 为定时全量同步和事件后的增量校准提供权威数据。

### 4.2 WorkTool 负责

- 机器人接收和发送群消息。
- 创建外部群。
- 拉人、踢人、改群名、改群公告和其他已核实群操作。
- 返回命令接收状态和异步执行结果。
- 按当前显示名或群备注名执行只能按名称完成的操作。

WorkTool 的群列表接口已经标记为将废弃，不得作为生产同步数据源。WorkTool
命令被接受不代表已经执行成功，也不代表企业微信客户群数据已经完成同步。

### 4.3 本系统负责

- 保存企业微信群和成员的本地同步副本。
- 保存 WorkTool 机器人与企业微信员工的绑定关系。
- 保存企业微信员工与后台账号的绑定关系。
- 保存群级 AI、知识库、上下文、人工客服和暂停策略配置。
- 将稳定成员 ID 转换为 WorkTool 执行所需的当前显示名。
- 协调 WorkTool 命令结果与企业微信同步结果。
- 在数据无法唯一匹配时停止自动绑定并交由管理员处理。

### 4.4 不支持的范围

- 不将普通企业内部群伪装成客户群同步能力。
- 不从 WorkTool 回调中的显示名推导稳定员工身份。
- 不通过昵称自动创建后台账号或自动授予客服权限。
- 不将外部联系人配置为人工客服。
- 不声称 WorkTool 创建命令成功后立即获得企业微信 `chat_id`。
- 不使用已废弃的 WorkTool 群列表作为唯一或主要数据源。

## 5. 术语与身份模型

| 对象 | 稳定标识 | 含义 |
|---|---|---|
| `ApplicationUser` | 本地用户 GUID | 登录后台、授权和审计的系统账号 |
| `EnterpriseMember` | 企业微信 `userid` | 企业内部员工目录记录 |
| `ExternalContact` | 企业微信 `external_userid` | 微信客户或企业外部联系人 |
| `GroupProfile` | 本地群 GUID | AI、知识库、上下文和客服配置聚合 |
| `WeComChat` | 企业微信 `chat_id` | 客户群的权威外部身份 |
| `GroupMember` | 群 ID + 成员稳定 ID | 员工或客户在具体群中的成员关系 |
| `RobotConfig` | 本地机器人 GUID | WorkTool 机器人接入和限流配置 |
| `GroupHumanAgent` | 群 ID + 后台用户 ID | 可处理该群人工转接的客服配置 |

显示名只用于界面展示和转换 WorkTool 指令，不参与系统内部唯一性判断。

## 6. 数据模型

### 6.1 企业微信集成状态

新增 `WeComSyncStateEntity`：

- `Id`
- `TenantKey`
- `LastFullSyncStartedAtUtc`
- `LastFullSyncCompletedAtUtc`
- `LastSuccessfulSyncAtUtc`
- `NextCursor`
- `Status`
- `LastErrorCode`
- `LastErrorSummary`
- `ConfigurationVersion`

凭据不进入该表。凭据通过 `.env` 或系统环境变量提供：

- `WeCom__CorpId`
- `WeCom__CustomerContactSecret`
- `WeCom__DirectorySecret`
- `WeCom__CallbackToken`
- `WeCom__CallbackEncodingAesKey`

Secret、Token 和 EncodingAESKey 不返回前端、不写入审计详情、不进入普通日志。

### 6.2 企业内部员工

新增 `EnterpriseMemberEntity`：

- `Id`
- `WeComUserId`
- `DisplayName`
- `DepartmentSnapshotJson`
- `IsActive`
- `LastSyncedAtUtc`
- `CreatedAtUtc`
- `UpdatedAtUtc`

`WeComUserId` 在当前企业范围内唯一。员工改名只更新 `DisplayName`，不改变绑定关系。

### 6.3 群与成员

扩展 `GroupProfileEntity`：

- `WeComChatId`
- `DataSource`
- `WeComOwnerUserId`
- `WeComCreateTimeUtc`
- `LastMemberSyncAtUtc`
- `LastMetadataSyncAtUtc`
- `SyncStatus`
- `LastSyncErrorSummary`

`WeComChatId` 在当前企业范围内唯一。已有手工登记数据继续保留，本地迁移后标记为
`ManualUnlinked`，等待自动匹配或管理员绑定。

新增 `ExternalContactEntity`：

- `Id`
- `ExternalUserId`
- `DisplayName`
- `IsActive`
- `LastSyncedAtUtc`
- `CreatedAtUtc`
- `UpdatedAtUtc`

`ExternalUserId` 在当前企业和调用身份范围内唯一。系统只同步当前授权范围内、
且群管理确实需要的外部联系人。手机号、邮箱、头像原图和完整客户画像不进入本表。

新增 `GroupMemberEntity`：

- `Id`
- `GroupProfileId`
- `MemberType`：`Internal` 或 `External`
- `EnterpriseMemberId`，内部员工时填写
- `ExternalContactId`，外部联系人时填写
- `DisplayNameSnapshot`
- `JoinTimeUtc`
- `JoinScene`
- `IsGroupAdmin`
- `IsActive`
- `LastSyncedAtUtc`

系统只保存群管理和人工转接需要的最少字段，不保存无业务用途的客户敏感资料。

### 6.4 机器人和后台账号绑定

扩展 `RobotConfigEntity`：

- `EnterpriseMemberId`

该字段表示运行 WorkTool 的企业微信员工账号。一个启用的企业微信员工最多绑定一个
启用的 WorkTool 机器人。

扩展 `ApplicationUser`：

- `EnterpriseMemberId`

该字段可空。只有绑定企业员工、账号启用并拥有 `HumanAgent` 角色的后台用户，
才能成为群人工客服候选人。

### 6.5 群人工客服

新增 `GroupHumanAgentEntity`：

- `GroupProfileId`
- `ApplicationUserId`
- `Priority`
- `IsDefault`
- `IsEnabled`
- `CreatedAtUtc`
- `UpdatedAtUtc`

约束：

- 同一个群最多一个默认客服。
- 客服必须绑定有效 `EnterpriseMember`。
- 客服必须拥有 `HumanAgent` 角色。
- 客服必须是该客户群的有效内部成员。
- WorkTool 机器人绑定的员工不能作为该群人工客服。

## 7. 权威数据矩阵

| 数据 | 权威来源 | 本地行为 |
|---|---|---|
| 客户群 `chat_id` | 企业微信 | 只同步，不允许人工编辑 |
| 群名称、群主、群成员 | 企业微信 | 事件同步并定时校准 |
| WorkTool 群备注名 | WorkTool/管理员 | 本地保存，用于名称型指令 |
| WorkTool 机器人身份 | 管理员绑定 | 一次性绑定企业员工 |
| AI、知识标签、上下文 | WechatRobot | 本地配置并做并发控制 |
| 默认客服和客服组 | WechatRobot | 从合格内部员工中选择 |
| WorkTool 命令状态 | WorkTool 回调 | 本地审计和状态机 |
| 群操作后的最终群状态 | 企业微信 | 命令成功后再次同步确认 |

发生冲突时：

- 企业微信群名称、群主和成员覆盖本地同步副本。
- 本地 AI、知识库、上下文和客服配置不被同步任务覆盖。
- WorkTool 群备注名与企业微信群名分别保存，不互相伪装。

## 8. 同步架构

### 8.1 服务边界

新增以下独立服务：

- `IWeComAccessTokenProvider`：缓存和刷新企业微信访问令牌。
- `IWeComCustomerGroupClient`：调用客户群列表和详情接口。
- `IWeComDirectoryClient`：读取授权范围内的企业内部员工。
- `IWeComExternalContactClient`：读取群成员所需的外部联系人基本资料。
- `IWeComGroupSyncService`：全量或指定群同步。
- `IWeComCallbackVerifier`：验证并解密企业微信回调。
- `IWeComGroupEventHandler`：处理客户群变更事件并触发详情刷新。
- `IGroupOperationReconciler`：协调 WorkTool 命令与企业微信最终状态。

外部 HTTP 客户端、同步编排、数据库写入和 WorkTool 指令不得集中在同一个类中。

### 8.2 初次同步

1. 管理员保存环境配置并重启 API 和 Worker。
2. 管理后台显示“企业微信已配置”，但不返回任何 Secret。
3. 管理员执行连接测试。
4. 系统同步企业内部员工目录。
5. 系统分页获取客户群列表。
6. 系统逐群获取详情和成员。
7. 系统按需同步群内外部联系人的最小展示资料。
8. 系统使用 `chat_id` 创建或更新 `GroupProfile`。
9. 系统尝试把已有手工登记群迁移到企业微信群。
10. 无法唯一匹配的记录进入“待绑定”，不得按名称静默合并。

### 8.3 持续同步

- 企业微信客户群变更事件到达后，按 `chat_id` 拉取最新详情。
- Worker 定时执行全量校准，修复丢失事件和短暂失败。
- 管理员可点击“立即同步”。
- 同一个企业同一时刻只允许一个全量同步任务。
- 同一个 `chat_id` 的重复事件必须幂等。
- 删除或离职状态采用停用和保留历史关系，不直接物理删除。

## 9. WorkTool 群操作协调

### 9.1 新建客户群

新建流程改为向导：

1. 选择已绑定企业员工的 WorkTool 机器人。
2. 填写群名称、公告和可选群模板。
3. 从授权范围内已同步的内部员工和外部联系人目录中选择成员；最终发送给
   WorkTool 的仍是当前显示名。
4. 展示预览和重名风险。
5. 管理员二次确认后提交 WorkTool 命令。
6. 本地创建 `PendingGroupOperation`，不立即伪造 `chat_id`。
7. WorkTool 返回执行成功后，系统等待企业微信事件或主动轮询客户群列表。
8. 使用机器人企业员工、群名称和创建时间窗口进行候选匹配。
9. 唯一匹配时写入 `WeComChatId` 并自动创建本地群配置。
10. 多个候选或无候选时进入“待确认绑定”，由管理员选择真实群。

新建群成功后不再要求管理员重新填写一遍登记表。

### 9.2 修改群信息

所有远程操作从已同步群详情页发起。前端只提交本地 `GroupProfileId` 和操作参数，
后端负责解析：

- 绑定的 `RobotConfigId`
- 当前企业微信群名
- WorkTool 群备注名
- 当前成员显示名

WorkTool 改群名执行成功后，本地不立即把命令参数当作最终真相。系统触发指定群同步，
以企业微信返回的名称作为最终值。同步完成前显示“等待企业微信确认”。

### 9.3 成员选择

- 前端以稳定成员 ID 选择成员。
- 新建群和添加成员的候选来源是企业微信授权范围内已同步的员工与外部联系人，
  不是 WorkTool 昵称搜索结果。
- 后端在创建 WorkTool 指令前读取最新显示名。
- 重名、显示名为空、成员已退出或数据过期时阻止执行。
- 后端保存稳定成员 ID、显示名快照和指令哈希，但不记录不必要的个人资料。
- WorkTool 仍按名称执行的限制必须在预览页明确提示。

## 10. 人工客服选择

### 10.1 候选客服规则

群配置中的客服候选人必须同时满足：

- 是当前客户群的有效内部成员。
- 对应 `EnterpriseMember.IsActive = true`。
- 已绑定启用的 `ApplicationUser`。
- 拥有 `HumanAgent` 角色。
- 不是当前群的 WorkTool 机器人账号。

企业微信群主和群管理员可以排在推荐列表前面，但系统不得自动授予
`HumanAgent` 角色。

### 10.2 群级配置

群配置支持：

- 不启用人工客服。
- 选择一个默认客服。
- 选择多个客服组成客服组。
- 设置客服优先级。
- 选择无客服在线或无可用客服时的行为。

默认失败行为是保持案件在“等待人工”队列并提示管理员，不自动分配给无权限员工，
也不恢复 AI 生成可能不可靠的答复。

### 10.3 与现有转人工模型的关系

现有 `HandoffCaseEntity.AssigneeUserId` 继续保存后台用户 ID。通知链路为：

```text
HandoffCase.AssigneeUserId
  -> ApplicationUser
  -> EnterpriseMember
  -> WeComUserId + DisplayName
  -> WorkTool atList
```

本规格实现成员同步、账号绑定、群客服配置和稳定通知映射。现有人工转接状态机、
客服工作台和案件处理页面不在本阶段重构；后续实现只能消费本规格提供的稳定身份，
不能回退到昵称匹配。

## 11. 前端信息架构

### 11.1 导航

主导航保留一个“群管理”，其下使用清晰的页面职责：

- `客户群`：企业微信同步的客户群目录。
- `同步与绑定`：同步状态、机器人绑定和历史手工群迁移。
- `操作记录`：WorkTool 命令状态、失败原因和审计。

取消把“登记群”“已登记群”“远程操作”同时堆在一个页面的布局。

### 11.2 客户群列表

页面标题改为“客户群”，副标题明确“数据来自企业微信，AI 配置保存在本系统”。

顶部显示：

- 企业微信连接状态。
- 最近成功同步时间。
- 同步中的任务状态。
- “立即同步”按钮。
- 名称、群主、机器人、同步状态筛选。

每行显示：

- 群名称。
- 群主。
- 绑定机器人。
- 内部员工数和外部客户数。
- 默认客服或客服组摘要。
- AI 启用状态。
- 最后同步时间。
- 同步异常状态。

行操作：

- `配置`
- `群操作`
- `查看成员`
- `处理绑定`，仅异常或待绑定记录显示

### 11.3 群详情

群详情采用分区或页签：

1. `概览`：企业微信只读信息、同步状态和绑定机器人。
2. `AI 与知识库`：匹配规则、知识标签和上下文。
3. `人工客服`：合格客服选择、默认客服和客服组。
4. `群成员`：内部员工与外部客户分组展示。
5. `群操作`：改名、公告、成员和其他已支持操作。
6. `操作记录`：当前群的 WorkTool 命令状态。

内部 GUID、WorkTool `robotId`、企业微信 Secret 和原始回调内容不显示为可编辑字段。

### 11.4 同步与绑定

该页面显示：

- 企业微信配置是否完整。
- API 连接测试结果。
- 最近同步时间和错误摘要。
- WorkTool 机器人与企业员工的绑定列表。
- 已有手工登记群的自动匹配结果。
- 需要管理员确认的歧义记录。
- 员工与后台账号的绑定入口。

所有选择使用下拉或可搜索选择器，不允许手填 GUID。

## 12. 后端 API

### 12.1 企业微信状态与同步

- `GET /api/admin/wecom/status`
- `POST /api/admin/wecom/test`
- `POST /api/admin/wecom/sync`
- `GET /api/admin/wecom/sync-jobs/{id}`
- `GET /api/admin/wecom/unresolved-bindings`
- `POST /api/admin/wecom/unresolved-bindings/{id}/resolve`

### 12.2 客户群与成员

- `GET /api/admin/groups`
- `GET /api/admin/groups/{id}`
- `GET /api/admin/groups/{id}/members`
- `POST /api/admin/groups/{id}/sync`
- `GET /api/admin/groups/{id}/eligible-human-agents`
- `PUT /api/admin/groups/{id}/human-agents`

现有 `GET /api/admin/worktool/groups` 在兼容期保留，前端迁移完成后标记废弃。

### 12.3 绑定

- `GET /api/admin/enterprise-members`
- `PUT /api/admin/robots/{id}/enterprise-member`
- `PUT /api/admin/users/{id}/enterprise-member`

绑定、解绑、同步、手工解决歧义和客服配置都必须写入脱敏管理审计。

### 12.4 企业微信回调

- `GET /api/integrations/wecom/customer-contact/callback`
- `POST /api/integrations/wecom/customer-contact/callback`

回调端点必须验证签名、时间戳、随机数和加密消息，不接受未验证明文事件。

## 13. 权限

| 操作 | Admin | HumanAgent |
|---|---:|---:|
| 查看客户群目录 | 是 | 仅分配或授权范围 |
| 执行同步 | 是 | 否 |
| 绑定机器人和企业员工 | 是 | 否 |
| 绑定后台账号和企业员工 | 是 | 否 |
| 配置 AI、知识库和客服 | 是 | 否 |
| 执行远程群操作 | 是 | 否 |
| 查看自己可处理群的成员摘要 | 是 | 是 |
| 查看外部联系人非必要敏感信息 | 否 | 否 |

## 14. 一致性与错误处理

- 企业微信未配置：群目录显示明确配置指引，保留已有本地配置只读访问。
- 企业微信不可用：显示最后成功同步数据及过期时间，不清空本地群和成员。
- 访问令牌失败：刷新一次后退避重试，不在日志中输出 Secret。
- 单群同步失败：标记该群异常，不使全量同步全部回滚。
- 事件重复：按企业、事件类型、`chat_id` 和事件标识幂等处理。
- WorkTool 执行成功但企业微信未确认：状态为“执行成功，等待同步确认”。
- WorkTool 执行失败：不修改企业微信同步字段。
- 创建群无法唯一匹配：进入待确认绑定，不按名称自动选择。
- 群成员改名：更新显示名快照，稳定身份和后台绑定保持不变。
- 客服退出群或离职：自动停用该群客服绑定并产生管理告警。
- 默认客服失效：案件保留在等待队列，不静默转给其他无配置人员。

## 15. 安全与隐私

- CorpId 可以作为非秘密配置；所有 Secret、Token 和 EncodingAESKey 必须位于环境配置。
- 日志不得包含访问令牌、Secret、回调明文、完整客户资料或 WorkTool 机器人标识。
- 数据库只保存群管理所需的成员最小信息。
- API 不返回外部联系人的手机号、邮箱、头像原图或其他无关字段。
- 所有同步与绑定 API 需要管理员权限。
- WorkTool 高风险群操作继续使用预览、短期确认令牌和异步结果确认。
- 企业微信回调必须限制请求大小、验证签名并防止重复处理。
- 管理审计保存稳定本地 ID、动作、结果和脱敏摘要，不保存凭据。

## 16. 兼容与迁移

迁移按以下顺序执行：

1. 新增企业员工、群成员、客服绑定和同步状态表。
2. 为 `GroupProfile` 增加可空 `WeComChatId` 和同步字段。
3. 为 `RobotConfig` 和 `ApplicationUser` 增加可空 `EnterpriseMemberId`。
4. 将现有群标记为 `ManualUnlinked`，不删除既有 AI 配置和审计。
5. 首次同步后按管理员可复核的规则生成候选绑定。
6. 唯一候选也记录匹配证据；歧义候选必须人工确认。
7. 前端切换到新客户群 API。
8. 兼容期结束后停止新的手工群登记。

现有 `GroupProfileEntity.Id`、规则、标签、上下文、会话、审计和
`HandoffCase.GroupProfileId` 保持不变，避免破坏历史关联。

## 17. 实施阶段

本规格是统一业务规格，但实施时必须拆成以下五个独立计划。每个阶段完成数据库、
后端、前端、测试和验收后才能进入依赖它的下一阶段，不允许用一个超大提交一次完成。

### 阶段 A：企业微信基础连接

- 配置校验、访问令牌、客户群客户端和通讯录客户端。
- 回调验证和测试工具。
- 连接状态 API。

### 阶段 B：客户群和成员同步

- 数据库迁移。
- 全量同步、事件同步、定时校准和幂等。
- 现有手工群迁移与歧义处理。

### 阶段 C：群管理前端重构

- 客户群目录。
- 同步与绑定页面。
- 群详情分区。
- 从群详情发起远程操作。
- 操作记录独立页面。

### 阶段 D：机器人和客服映射

- 机器人绑定企业员工。
- 后台账号绑定企业员工。
- 合格客服筛选和群客服配置。
- 为现有转人工通知提供稳定显示名映射。

### 阶段 E：创建与改名协调

- WorkTool 创建群后的自动发现。
- WorkTool 改群名后的企业微信确认。
- 待确认绑定和异常恢复。

## 18. 测试要求

### 18.1 单元测试

- 企业微信响应到领域模型的映射。
- 内部员工和外部联系人分类。
- 客服候选资格规则。
- 机器人排除规则。
- 创建群候选匹配和歧义判定。
- Secret、令牌和个人资料脱敏。

### 18.2 集成测试

- 首次全量同步创建群和成员。
- 重复同步幂等。
- 群改名和成员变更更新现有记录。
- 离职或退群停用客服绑定。
- 手工群迁移不破坏规则、标签、会话和审计。
- 并发同步只运行一个实例。
- WorkTool 成功后触发企业微信校准。
- 企业微信失败不错误修改 WorkTool 操作状态。
- 所有管理接口的角色授权。

### 18.3 合约测试

- 企业微信客户端只使用官方路径和字段。
- WorkTool 操作继续符合已核实官方文档。
- 测试桩不得虚构企业微信或 WorkTool 返回的稳定标识。
- 普通自动化测试不得调用真实企业微信或 WorkTool。

### 18.4 前端测试

- 客户群列表明确显示数据来源和同步时间。
- 所有机器人、群和成员使用选择器，不出现 GUID 输入框。
- 客服候选列表只包含合格内部员工。
- 创建群向导完整展示预览、等待确认和歧义状态。
- 群改名后显示等待企业微信确认。
- 同步失败时保留最后一次成功数据。
- 窄屏无横向滚动，键盘操作和焦点状态完整。

## 19. 验收标准

本规格完成必须同时满足：

- “群列表”不再表示手工登记数据，而是企业微信客户群同步目录。
- 每个已同步群保存稳定 `WeComChatId`。
- 管理员无需手填机器人配置 ID、群配置 ID或成员显示名。
- WorkTool 创建群成功后自动进入发现和绑定流程。
- WorkTool 改群名后由企业微信同步结果校准本地名称。
- 群成员可以按内部员工和外部客户查看。
- 机器人可以一次性绑定企业微信员工。
- 后台账号可以一次性绑定企业微信员工。
- 群配置只能从合格内部员工中选择人工客服。
- 外部联系人不能被配置为客服。
- 转人工通知不再依赖后台账号显示名猜测企业微信身份。
- 无法唯一匹配的数据进入可见异常状态，不静默猜测。
- 企业微信和 WorkTool 凭据不出现在前端、日志或审计详情。
- 现有群规则、知识标签、上下文、会话和审计关联保持有效。
- 服务端构建、单元测试、合约测试和相关集成测试通过。
- 前端类型检查、测试和生产构建通过。
- 真实外部验收只有在显式测试开关和专用测试群下执行。

## 20. 外部前置条件

生产启用自动同步前必须具备：

- 企业微信 CorpId。
- 具有客户联系和客户群读取权限的 Secret。
- 具有所需可见范围的通讯录读取权限及对应 Secret。
- 企业微信可信 IP 和回调域名配置。
- 公网 HTTPS 回调地址。
- 回调 Token 与 EncodingAESKey。
- WorkTool 机器人对应的企业微信内部员工账号。

缺少这些条件时，系统只能保留现有手工登记兼容模式，不能显示“已自动同步”。

## 21. 官方资料

1. [企业微信获取客户群列表](https://developer.work.weixin.qq.com/document/path/92120)
2. [企业微信获取客户群详情](https://developer.work.weixin.qq.com/document/path/92122)
3. [WorkTool 群列表查询](https://doc.worktool.ymdyes.cn/api-21488853)
4. [WorkTool 创建外部群](https://doc.worktool.ymdyes.cn/api-23520350)
5. [WorkTool 修改群信息](https://doc.worktool.ymdyes.cn/api-23520590)
6. [WorkTool 发送消息](https://doc.worktool.ymdyes.cn/api-23520034)
