# WorkTool 群导入与唯一昵称客服映射设计

## 1. 文档状态

- 状态：已批准
- 日期：2026-07-27
- 适用系统：WechatRobot 管理后台、API、Worker
- 适用阶段：尚未配置企业微信客户联系 API 时的可运行降级方案
- 外部系统：WorkTool

本设计是
`2026-07-27-group-management-wecom-sync-design.md`
的前置降级阶段。企业微信官方客户群 API 仍是未来稳定群身份和成员身份的升级路径；
本阶段不虚构 `chat_id`、`userid` 或 `external_userid`。

## 2. 目标

在暂不提供企业微信 CorpId 和客户联系 Secret 的条件下，实现以下闭环：

1. 管理员选择一个已配置的 WorkTool 机器人。
2. 系统调用 WorkTool 群列表接口显示该机器人可见的群。
3. 管理员单选或批量勾选群，一键导入本地群配置。
4. 系统调用 WorkTool `type=512` 请求刷新指定群的成员昵称列表。
5. 管理员将公司范围内唯一的企业微信昵称绑定到后台账号。
6. 群配置只允许选择昵称已绑定、角色合格且当前群内可验证的人工客服。
7. 人工转接继续保存后台用户 ID，通知时转换成 WorkTool 昵称并通过 `atList` 提醒客服。

本阶段同时消除以下手工输入：

- 登记群时手填机器人配置 ID。
- 重复手填 WorkTool 群名称。
- 群配置中手填人工客服用户 ID。
- 人工通知时猜测后台显示名与企业微信昵称是否相同。

## 3. 平台事实与边界

### 3.1 WorkTool 群列表

当前接口：

```http
GET /robot/wework/group/list
    ?robotId={robotId}
    &groupName={optional}
    &page={page}
    &size={size}
```

公开结果包含群名称、群主名称、机器人 ID、成员数量、群公告和时间字段，但没有稳定
企业微信群 `chat_id`。官方已将该接口标记为“将废弃”。

因此：

- 它可以作为管理员触发的群发现和批量导入来源。
- 它不能成为不可替换的领域接口。
- 它不能证明同名群是同一个稳定群。
- 接口不可用时必须保留已有本地群，不能清空数据。
- 前端必须显示“数据来自 WorkTool，接口已被官方标记将废弃”。

### 3.2 WorkTool 群成员昵称

`type=512` 请求：

```json
{
  "socketType": 2,
  "list": [
    {
      "type": 512,
      "groupName": "群名称或群备注"
    }
  ]
}
```

该能力读取昵称列表，不提供稳定成员 ID。因此：

- 昵称只能作为本阶段由管理员确认的业务身份。
- 不得将昵称包装成企业微信 `userid`。
- 不得从昵称自动创建后台账号或授予角色。
- 重名、空昵称或未验证昵称不能成为可选客服。

### 3.3 昵称唯一性约束

本阶段采用公司级唯一昵称，不采用群内唯一昵称。

管理员对绑定行为负责，并保证：

- 一个规范化昵称最多绑定一个后台账号。
- 一个后台账号最多绑定一个当前有效昵称。
- 客服改名时必须更新后台绑定并重新执行群内验证。
- 昵称冲突时系统阻止绑定和通知，不自动选择其中一个人。

昵称比较规则：

- 保存前移除首尾空白。
- 空字符串无效。
- 使用数据库 `utf8mb4_bin` 的精确值唯一约束。
- 系统不擅自忽略大小写、全角半角或内部空格。
- 前端展示原始规范化昵称。

## 4. 方案选择

已选择“远程群列表加勾选导入”：

- 不自动把 WorkTool 返回的所有群写入数据库。
- 管理员可以单个或批量选择需要机器人管理的群。
- 已导入记录显示为“已登记”，默认不可重复选择。
- 同名冲突进入显式冲突状态，由管理员选择已有记录或以独立记录导入。

未选择：

- 全量静默同步：会导入无关群，并放大废弃接口失效风险。
- 继续完全手工登记：无法达到降低配置成本的目标。

## 5. 数据模型

### 5.1 后台账号昵称绑定

扩展 `ApplicationUser`：

- `WorkToolDisplayName`：可空，最大 128 字符。
- `WorkToolDisplayNameUpdatedAtUtc`：最近一次绑定、改名或解绑时间，可空。

数据库约束：

- `WorkToolDisplayName` 建立唯一索引。
- 空字符串在进入数据库前转换为 `null`。
- 解绑保留管理审计，但清空当前字段。

`DisplayName` 继续表示后台界面显示名；它与 `WorkToolDisplayName` 是两个不同字段。

### 5.2 远程群发现结果

远程群列表不直接持久化为完整同步目录。API 将 WorkTool 响应转换为只读 DTO：

- `RobotConfigId`
- `GroupName`
- `MasterName`
- `MembersCount`
- `GroupAnnouncement`
- `WorkToolCreatedAt`
- `WorkToolUpdatedAt`
- `ImportState`：`Available`、`Imported`、`Conflict`
- `MatchedLocalGroupId`

WorkTool 返回的原始 `robotId` 不返回前端。

### 5.3 本地群来源

扩展 `GroupProfileEntity`：

- `RegistrationSource`：`Manual`、`WorkToolImport`、未来可增加 `WeComSync`。
- `WorkToolImportedAtUtc`：可空。
- `WorkToolLastSeenAtUtc`：可空。

本阶段继续使用：

- 本地 `GroupProfileEntity.Id` 作为系统内部稳定关联。
- `RobotConfigId + WorkToolGroupRemark/Name` 作为 WorkTool 名称型操作定位信息。

该组合不是企业微信稳定群 ID。出现同名时不得静默合并。

### 5.4 群级人工客服

在获得并验证 `type=512` 真实昵称结果契约后，新增
`WorkToolGroupMemberSnapshotEntity`：

- `GroupProfileId`
- `DisplayName`
- `LastSeenAtUtc`
- `RefreshCommandMessageId`
- `IsPresent`

约束：

- `GroupProfileId + DisplayName` 唯一。
- 每次完整刷新成功后，将本次未出现的旧昵称标记为 `IsPresent = false`。
- 未完成、失败或无法解析的刷新不能清空上一份成功快照。
- `RefreshCommandMessageId` 只用于追踪异步命令，不作为成员身份。

新增 `GroupHumanAgentEntity`：

- `GroupProfileId`
- `ApplicationUserId`
- `WorkToolDisplayNameSnapshot`
- `LastVerifiedAtUtc`
- `VerificationStatus`：`Verified`、`Missing`、`Conflict`、`Stale`
- `IsDefault`
- `IsEnabled`
- `CreatedAtUtc`
- `UpdatedAtUtc`

约束：

- `GroupProfileId + ApplicationUserId` 唯一。
- 同一个群最多一个默认客服。
- 只有启用的 `HumanAgent` 或 `Admin` 后台账号可以配置。
- 账号必须已经绑定唯一 `WorkToolDisplayName`。
- 绑定昵称必须出现在该群最近一次成功获取的昵称列表中。
- 机器人自身昵称如果无法可靠识别，本阶段不自动排除；管理员界面必须提示不要选择机器人账号。

## 6. 后端组件

### 6.1 WorkTool 客户端扩展

在 `IWorkToolClient` 增加两个独立能力：

```csharp
Task<WorkToolGroupPage> ListGroupsAsync(
    Guid robotConfigId,
    string? groupName,
    int page,
    int pageSize,
    CancellationToken cancellationToken);

Task<WorkToolCommandSubmission> RequestGroupMembersAsync(
    Guid robotConfigId,
    string groupIdentifier,
    CancellationToken cancellationToken);
```

`ListGroupsAsync` 映射官方分页结果，不泄漏 `robotId`。

`RequestGroupMembersAsync` 发送单条 `type=512` 指令。成员昵称结果必须来自经核实的
WorkTool 异步结果载荷；在未获得真实结果样例前，不实现猜测式解析。若当前公开回调
只提供执行状态而不提供昵称数据，功能状态显示为“等待真实结果契约”，不得生成伪成员。

因此实施拆成两个可独立验收的切片：

1. 群列表导入、账号唯一昵称绑定和群客服数据结构。
2. 获得真实 `type=512` 结果样例后，启用成员快照和群内资格验证。

第二个切片未完成前，群客服配置入口保持禁用，不能以管理员手填“已验证”绕过。

### 6.2 群导入服务

新增 `IWorkToolGroupImportService`，职责为：

- 读取远程群页。
- 将远程群与当前机器人下的本地群做名称匹配。
- 计算 `Available`、`Imported`、`Conflict`。
- 执行单个或批量导入。
- 保留既有本地 AI、知识库、上下文和审计关联。
- 对每个导入结果独立返回成功、已存在或冲突，避免整批全部回滚。

导入幂等键为：

```text
RobotConfigId + normalized exact GroupName
```

该键只用于避免重复导入，不声称是企业微信稳定身份。

### 6.3 昵称绑定服务

新增 `IWorkToolAgentBindingService`，职责为：

- 绑定或解绑后台账号昵称。
- 检查公司级唯一性。
- 检查账号是否启用且拥有合格角色。
- 对群内昵称列表执行精确匹配。
- 更新群客服验证状态。
- 生成脱敏管理审计。

并发绑定依赖数据库唯一索引兜底。发生竞争时返回 HTTP 409，而不是覆盖已有绑定。

### 6.4 人工通知解析

现有 `HandoffCase.AssigneeUserId` 保持不变。通知解析链路为：

```text
HandoffCase.AssigneeUserId
  -> ApplicationUser
  -> WorkToolDisplayName
  -> GroupHumanAgent.VerificationStatus
  -> WorkTool atList
```

通知前重新检查：

- 用户启用。
- 用户仍有 `HumanAgent` 或 `Admin` 角色。
- 群客服绑定启用。
- 昵称状态是 `Verified`。

任何检查失败时：

- 案件保持等待人工或当前人工处理状态。
- 不发送不带目标的群通知。
- 记录可运营的失败原因。
- 不把案件自动分配给其他同名人员。

## 7. API

### 7.1 远程群发现与导入

```http
GET /api/admin/worktool/robots/{robotConfigId}/groups
    ?query={optional}
    &page=1
    &pageSize=50

POST /api/admin/worktool/robots/{robotConfigId}/groups/import
```

导入请求：

```json
{
  "groups": [
    {
      "groupName": "客户服务群",
      "expectedImportState": "Available"
    }
  ]
}
```

后端重新读取或重新校验远程数据，不盲信前端传入的群主、公告和成员数量。

### 7.2 用户昵称绑定

```http
PUT /api/admin/users/{userId}/worktool-display-name
DELETE /api/admin/users/{userId}/worktool-display-name
```

绑定请求：

```json
{
  "workToolDisplayName": "客服-王小明"
}
```

### 7.3 群成员昵称与客服

```http
POST /api/admin/groups/{groupId}/worktool-members/refresh
GET  /api/admin/groups/{groupId}/eligible-human-agents
PUT  /api/admin/groups/{groupId}/human-agents
```

刷新接口返回任务状态，不假装同步调用已经获得成员结果。

客服配置请求只提交后台用户 ID、是否默认和预期配置版本。后端根据当前绑定解析昵称，
不接受前端直接提交昵称覆盖绑定。

## 8. WorkTool 创建和改名后的本地协调

### 8.1 新建群

1. 提交 `type=206`。
2. 等待指令结果回调确认成功。
3. 使用相同机器人和目标群名调用远程群列表。
4. 唯一匹配时自动创建本地 `GroupProfile`，来源为 `WorkToolImport`。
5. 无匹配或多个匹配时创建可见的“待导入确认”结果，不静默登记。

### 8.2 修改群名

1. 提交 `type=207`。
2. 等待指令结果回调确认成功。
3. 查询新名称。
4. 唯一匹配时更新本地 `Name`，递增 `ConfigurationVersion` 并写审计。
5. 查询失败或冲突时保留原名称并显示“远程已执行，本地待确认”。

WorkTool 命令被 API 接受不等于客户端执行成功；客户端执行成功也不等于远程列表已经
立即可见。协调任务必须允许短期退避重试。

## 9. 前端信息架构

### 9.1 群管理首页

页面标题使用“群管理”，包含两个明确区域：

1. `从 WorkTool 导入`
2. `已登记群`

导入流程：

- 机器人下拉框。
- “读取远程群”按钮。
- 名称搜索。
- 带复选框的远程群表格。
- 每行显示群名称、群主、成员数、状态。
- “导入所选群”主按钮。
- 已导入行不可重复选择。
- 冲突行提供“查看冲突”，不直接导入。

页面不显示机器人 GUID、WorkTool `robotId` 或手填群 ID。

### 9.2 用户与角色

用户行增加“WorkTool 客服昵称”：

- 未绑定时显示“未绑定”。
- 管理员点击“绑定昵称”后输入公司级唯一昵称。
- 保存前明确提示昵称必须与企业微信显示完全一致。
- 冲突时显示已经被占用，但不泄漏无关账号敏感信息。
- 只有 `HumanAgent` 或 `Admin` 角色用户显示为客服绑定候选。

### 9.3 群配置

增加“人工客服”区域：

- 显示最近一次群成员昵称验证时间。
- “刷新群成员”按钮。
- 默认客服选择器。
- 可选客服多选器。
- 候选项显示后台姓名、邮箱和 WorkTool 昵称。
- 不在群内、冲突或过期的客服禁用并显示原因。

当前没有真实 `type=512` 结果契约时，页面明确显示“成员结果尚未完成验证”，不展示
伪造候选列表。管理员仍可维护账号昵称，但不能把未验证昵称配置为群客服。

## 10. 权限与审计

| 操作 | Admin | HumanAgent |
|---|---:|---:|
| 查看远程群并导入 | 是 | 否 |
| 绑定或解绑客服昵称 | 是 | 否 |
| 刷新群成员昵称 | 是 | 否 |
| 配置群人工客服 | 是 | 否 |
| 查看自己的当前昵称绑定 | 是 | 是 |
| 处理人工案件 | 是 | 是 |

以下动作写入 `AdministrationAudits`：

- 群导入。
- 群导入冲突处理。
- 用户昵称绑定、改名和解绑。
- 群成员刷新请求及结果。
- 群客服配置变更。
- 新建群自动登记。
- 改群名后的本地协调。

审计保存本地 ID、规范化昵称、动作和结果，不保存 WorkTool `robotId` 或回调密钥。

## 11. 错误处理

- WorkTool 群列表不可用：返回 502 和安全错误码，保留已有本地群。
- WorkTool 接口被正式下线：导入入口显示不可用，手工兼容模式继续存在。
- 分页中途失败：不把不完整结果标记为完整同步。
- 导入重复：返回幂等成功并指向已有群。
- 同名冲突：返回 409，要求管理员处理。
- 昵称已经被其他账号绑定：返回 409。
- 账号不具备客服角色：返回 400。
- 群成员刷新超时：保持旧快照并标记 `Stale`。
- 客服昵称改名：所有相关群绑定标记 `Stale`，重新验证后才能发送定向通知。
- 发送通知时昵称失效：案件不丢失，记录运营告警。

## 12. 迁移与兼容

1. 新增用户昵称字段和唯一索引。
2. 新增 `GroupHumanAgent` 表。
3. 在真实结果契约验收后新增 `WorkToolGroupMemberSnapshot` 表。
4. 扩展本地群来源和 WorkTool 最近发现时间。
5. 现有用户保持未绑定，不从 `DisplayName` 自动回填昵称。
6. 现有本地群默认 `Manual`，既有规则和会话关联保持不变。
7. 现有人工转接案件继续使用 `AssigneeUserId`。
8. 企业微信官方同步上线后，将 `WorkToolImport` 群通过人工确认绑定到 `chat_id`；
   不按名称静默迁移。

## 13. 测试要求

### 13.1 合约测试

- 群列表使用官方 GET 路径、查询参数和分页响应字段。
- `type=512` 请求只包含官方已记录字段。
- 未获得真实成员结果样例前，不增加猜测字段的成功测试。
- WorkTool 原始 `robotId` 不进入管理 API 响应。

### 13.2 服务端测试

- 公司级昵称唯一约束和并发冲突。
- 昵称 trim、空值、精确大小写和内部空格规则。
- 角色和启用状态验证。
- 远程群分页与 `Available/Imported/Conflict` 状态。
- 单个和批量导入幂等。
- 导入失败不清空本地群。
- 群客服必须在当前群验证。
- 昵称改名使群客服绑定变成 `Stale`。
- 新建群成功后的唯一匹配自动登记。
- 改名成功后的本地更新和冲突保留。
- 所有管理端点授权和审计。

### 13.3 前端测试

- 机器人使用下拉框，不出现 ID 输入框。
- 远程群列表支持单选、批量选择和状态禁用。
- 已导入群不能重复导入。
- 冲突群不能直接导入。
- 用户昵称绑定显示唯一性错误。
- 群客服选择器只展示合格候选。
- 未验证和过期候选显示明确原因。
- 窄屏布局无横向溢出，复选框与对应行保持同一视觉区域。

## 14. 验收标准

- 管理员无需手填机器人配置 ID 即可读取远程群。
- 管理员可以单个或批量导入 WorkTool 群。
- 系统不会静默导入所有远程群。
- 已有本地群和配置不会因 WorkTool 不可用被清空。
- 后台账号可以绑定公司级唯一 WorkTool 昵称。
- 昵称冲突、改名或群内缺失时不能成为有效客服。
- 群配置可以选择已验证的后台客服，而不是手填用户 ID或昵称。
- 人工案件继续保存后台用户 ID。
- 人工通知通过已验证昵称生成 `atList`。
- 新建群执行成功后可以自动登记或进入待确认状态。
- 改群名执行成功后可以同步本地名称或进入待确认状态。
- 页面明确说明 WorkTool 群列表接口已被官方标记将废弃。
- 系统不声称拥有企业微信稳定群 ID或成员 ID。
- 相关服务端和前端自动化测试全部通过。

## 15. 官方资料

1. [WorkTool 群列表查询](https://doc.worktool.ymdyes.cn/api-21488853)
2. [WorkTool 获取指定群成员信息](https://doc.worktool.ymdyes.cn/api-401901379)
3. [WorkTool 创建外部群](https://doc.worktool.ymdyes.cn/api-23520350)
4. [WorkTool 修改群信息](https://doc.worktool.ymdyes.cn/api-23520590)
5. [WorkTool 发送消息](https://doc.worktool.ymdyes.cn/api-23520034)
