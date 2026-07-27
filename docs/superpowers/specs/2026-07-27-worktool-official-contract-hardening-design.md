# WorkTool 官方契约加固设计

## 1. 文档状态

- 状态：待书面复核
- 日期：2026-07-27
- 适用系统：WechatRobot API、Worker、管理后台
- 官方基线：2026-07-27 可访问的 WorkTool 官方文档与真实机器人探测结果

本设计只修正已经接入的 WorkTool 契约，不增加新的业务功能。群列表导入、唯一昵称
客服映射和 `type=512` 群成员昵称快照分别由独立规格和实施计划负责。

## 2. 目标

完成以下四项契约加固：

1. 修正在线状态接口，删除官网没有定义的 `data.online` 和 `data.status` 解析。
2. 增加 API 与 Worker 共用的出口 IP 级 WorkTool 60 QPM 限流。
3. 对合法但本产品暂不支持的消息回调返回成功确认并忽略。
4. WorkTool 请求 JSON 省略 `null` 字段和空 `atList`。

## 3. 在线状态契约

### 3.1 已确认事实

WorkTool 官方“查询机器人是否在线”接口公开成功样例为：

```json
{}
```

官方没有定义以下字段：

```json
{
  "data": {
    "online": true,
    "status": 1
  }
}
```

本地真实机器人探测已经出现：

```json
{
  "reachable": true,
  "online": null,
  "failureCode": "worktool_invalid_response"
}
```

机器人信息查询成功，失败码来自在线状态响应被当前猜测模型错误解析。

### 3.2 修正行为

`GetOnlineAsync` 不再反序列化 `OnlineData`，也不根据未定义数字推导在线或离线。

兼容期返回规则：

- HTTP 2xx：`Online = null`、`FailureCode = null`。
- HTTP 非 2xx：`Online = null`、返回脱敏 HTTP 失败码。
- 超时或网络错误：沿用安全传输失败行为。
- 响应正文无论是 `{}`、空正文或其他未定义成功正文，都不推导布尔状态。

管理 API 暂时保留可空 `online` 字段，避免前端破坏性变更，但其值始终为 `null`。
前端显示：

```text
机器人接口可达
在线状态：WorkTool 官方未提供可靠结果
```

“可达”只来自获取机器人信息接口成功，不来自在线状态接口。

### 3.3 删除内容

- 删除 `OnlineData` 响应模型。
- 删除 `status=0/1` 到离线/在线的转换。
- 删除把官方成功正文判为 `worktool_invalid_response` 的行为。
- 删除任何自动化测试中对 `data.online` 或 `data.status` 的成功期待。

## 4. 全局出口 IP 级 60 QPM

### 4.1 范围

WorkTool 官方说明所有接口按请求 IP 限制 60 QPM。限流必须覆盖每一次实际 HTTP 请求：

- 发送消息 `type=203`。
- 创建和修改群 `type=206/207`。
- 获取机器人信息。
- 查询在线状态。
- 配置消息回调。
- 查询、绑定、删除指令结果回调。
- 后续群列表查询。
- 后续 `type=512` 请求。
- 自动重试产生的第二次 HTTP 请求。

一次重试是一次新的 WorkTool 请求，必须再次消耗配额。

### 4.2 协调边界

API 和 Worker 使用同一个 MySQL 数据库，因此使用数据库持久化的平滑限流槽协调多进程，
不能使用单进程内存 `RateLimiter`。

新增 `WorkToolRateLimitBucketEntity`：

- `ScopeKey`：主键，默认值 `default-egress`。
- `NextPermitAtUtc`
- `Version`

配置：

```text
WorkTool__RateLimit__ScopeKey=default-egress
WorkTool__RateLimit__RequestsPerMinute=60
WorkTool__RateLimit__MaxWaitSeconds=15
```

约束：

- `RequestsPerMinute` 生产环境不得大于 60。
- API 与 Worker 在同一出口 IP 下必须使用相同 `ScopeKey`。
- 如果未来部署到不同出口 IP，可为每个出口配置不同 `ScopeKey`。
- 配置错误时启动失败，不能静默退回每机器人限流。

### 4.3 获取请求许可

新增 `IWorkToolGlobalRateLimiter`：

```csharp
Task<WorkToolRateLimitLease> AcquireAsync(
    string operation,
    CancellationToken cancellationToken);
```

为避免容量 60 的令牌桶在窗口边界产生超过 60 次的瞬时突发，本系统采用保守的平滑
间隔：60 QPM 对应同一 ScopeKey 的两次实际 HTTP 请求至少间隔 1 秒。

请求许可使用数据库事务和行级锁：

1. 按 `ScopeKey` 读取桶记录并加锁。
2. 通过同一数据库连接取得 UTC 当前时间。
3. 计算本请求的许可时间：`max(databaseUtcNow, NextPermitAtUtc)`。
4. 许可等待时间不超过 `MaxWaitSeconds` 时，把 `NextPermitAtUtc` 推进 1 秒并提交。
5. 事务提交后异步等待至已保留的许可时间。
6. 等待超过最大值时回滚并返回未获得租约，不向 WorkTool 发出 HTTP 请求。

不使用应用服务器本地时间作为多个进程间唯一时钟来源。实现应通过同一个数据库连接
取得 UTC 当前时间，避免 API 与 Worker 时钟偏差绕过限流。

### 4.4 接入点

限流器接入所有 WorkTool HTTP 请求的公共传输层：

```text
WorkToolClient
  -> WorkToolHttpTransport
  -> IWorkToolGlobalRateLimiter
  -> HttpClient.SendAsync
```

每次 `HttpClient.SendAsync` 前获取一次令牌。业务服务、Worker 和管理端点不得各自
实现另一套 WorkTool 全局限流。

现有机器人级 `SendRateLimitPerMinute` 继续保留，表示单个机器人业务发送节奏。执行顺序：

```text
机器人级业务限流
  -> 全局出口 IP 限流
  -> WorkTool HTTP 请求
```

群操作和管理探测没有机器人级业务限流，但必须经过全局限流。

### 4.5 失败语义

- 等待配额期间取消：不发送请求。
- 等待超过最大时间：返回 `worktool_global_rate_limited`。
- 数据库不可用：不绕过限流，返回安全失败。
- 已保留许可但 HTTP 发送前进程崩溃：允许损失一个许可，不能补回造成超发。
- HTTP 请求完成或失败后不归还许可。

日志可以记录操作名、等待时长和 ScopeKey，不记录 WorkTool `robotId`、回调 URL 或正文。

## 5. 合法但不支持的消息回调

### 5.1 合法协议范围

WorkTool 官方消息回调可能包含：

- `roomType=1` 外部群。
- `roomType=2` 外部联系人。
- `roomType=3` 内部群。
- `roomType=4` 内部联系人。
- 文本、图片、语音、视频、小程序、链接、文件、合并记录和带回复文本等消息类型。

当前产品只处理：

```text
roomType=1
textType=1
```

“业务暂不处理”不等于“WorkTool 请求非法”。

### 5.2 分类顺序

回调端点按以下顺序处理：

1. 检查请求大小。
2. 解析 JSON。
3. 验证机器人路由和回调密钥。
4. 校验官方公共字段的长度和基本类型。
5. 分类为 `Process`、`Ignore` 或 `Reject`。

分类：

- `Process`：外部群文本且具备系统处理所需字段。
- `Ignore`：官方合法房间类型或消息类型，但当前产品不处理。
- `Reject`：JSON 损坏、鉴权失败、字段超限、缺少无法识别请求来源所需字段。

### 5.3 HTTP 响应

`Process`：

```http
HTTP 200
Content-Type: application/json

{"code":0,"message":"accepted"}
```

`Ignore`：

```http
HTTP 200
Content-Type: application/json

{"code":0,"message":"ignored"}
```

`Reject`：

- 鉴权失败返回 401。
- JSON 损坏、字段超限或协议无法识别返回 400。
- 持久化超时或内部故障返回 500。

被忽略消息不得：

- 创建入站消息。
- 创建 Durable Job。
- 触发 AI。
- 触发人工转接。

被忽略消息记录低基数指标：

```text
worktool_callback_ignored_total{room_type,text_type,reason}
```

日志只记录类型和原因，不记录消息正文、人员昵称、群名或文件 Base64。

### 5.4 DTO

DTO 增加官方 `fileBase64` 字段仅用于大小限制和分类，不持久化。

分类逻辑从当前布尔方法拆分成明确结果：

```csharp
public enum WorkToolCallbackDisposition
{
    Process,
    Ignore,
    Reject
}
```

分类结果包含稳定的低基数原因码，端点不再把所有非外部群文本都视为 Bad Request。

## 6. 请求 JSON 精确序列化

### 6.1 全局规则

WorkTool 请求专用 `JsonSerializerOptions` 使用：

```csharp
DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
```

该配置仅用于发送 WorkTool 请求，不改变管理 API 或数据库 JSON 的全局行为。

### 6.2 `atList`

发送文本时：

- `AtList == null`：省略 `atList`。
- `AtList.Count == 0`：转换成 `null` 并省略 `atList`。
- 有值：保留 `atList`，顺序不变。

不删除 `type=207` 官方样例中的空 `selectList` 和 `removeList`。用户要求只省略空
`atList`，其他空数组是否具有指令语义必须按各自官方样例处理。

### 6.3 可选群操作字段

未使用的 `newGroupName`、`newGroupAnnouncement` 等可选字段为 `null` 时省略。
布尔值 `false` 和空数组不是 `null`，必须保留。

## 7. API 与前端兼容

- 机器人探测 API 保留 `online: null`。
- 新增可读状态字段或前端派生文案，明确“官方未提供可靠在线值”。
- `reachable` 继续表示机器人信息接口可达。
- 全局限流失败通过现有 ProblemDetails 返回安全错误码。
- 回调成功忽略是服务端协议行为，不新增管理页面。
- 管理工作台可以在后续运营计划中增加全局 WorkTool 配额统计，本计划不扩展仪表盘。

## 8. 数据库迁移

只新增全局限流桶表。迁移必须兼容 MySQL 5.7：

- 不使用 MySQL 8 专属语法。
- 使用现有 EF Core 提供程序支持的列类型。
- 桶记录可以在迁移中创建，也可以由启动初始化服务幂等创建。
- 多实例并发首次启动只能产生一条相同 `ScopeKey` 记录。

在线状态、回调分类和 JSON 序列化不需要数据库迁移。

## 9. 测试要求

### 9.1 在线状态

- 官方 `{}` 样例返回未知且无失败码。
- 空成功正文返回未知且无失败码。
- 未定义 JSON 成功正文不推导布尔值。
- 非 2xx 返回安全失败。
- 代码和测试中不存在 `OnlineData`、`data.online`、`data.status`。

### 9.2 全局限流

- 同一 ScopeKey 的连续请求至少间隔 1 秒。
- API 和 Worker 两个服务实例共享一个桶。
- 不同机器人共享一个桶。
- 发送、群操作、探测和回调配置共享一个桶。
- 每次重试再次扣减。
- 任意滚动 60 秒窗口内的并发竞争不超过 60 次实际发送。
- 配额等待取消不发送 HTTP。
- 数据库失败不绕过限流。
- MySQL 5.7 集成测试验证事务和行锁行为。

### 9.3 回调分类

- 外部群文本返回 200 并入队。
- 外部联系人文本返回 200 `ignored` 且不入队。
- 内部群文本返回 200 `ignored` 且不入队。
- 图片、语音、文件等合法消息返回 200 `ignored` 且不入队。
- 错误密钥仍返回 401。
- 损坏 JSON、超限字段和超限 `fileBase64` 返回 400。
- 日志和指标不包含消息内容。

### 9.4 JSON

- 无 @ 人员时请求中不存在 `atList`。
- 有 @ 人员时 `atList` 与输入完全一致。
- 未使用的可选字段不输出 `null`。
- `false` 和具有官方指令语义的空数组继续输出。
- 合约测试比较完整 JSON 结构，不只搜索字符串片段。

## 10. 验收标准

- 代码中不存在官网无依据的在线布尔字段解析。
- 真实机器人探测不再因在线成功正文返回 `worktool_invalid_response`。
- 管理后台不会把未知状态显示为离线。
- API 与 Worker 的所有 WorkTool HTTP 请求共享同一出口限流桶。
- 任意机器人组合不会使同一 ScopeKey 超过配置的 60 QPM。
- 合法但未支持的 WorkTool 消息获得 HTTP 200 成功确认且不进入业务队列。
- `null` 字段和空 `atList` 不出现在 WorkTool 请求 JSON 中。
- 现有发送、群操作、回调配置和结果回调行为不回归。
- 服务端构建、单元测试、合约测试和相关集成测试通过。

## 11. 官方资料

1. [WorkTool 查询机器人是否在线](https://doc.worktool.ymdyes.cn/api-39271192)
2. [WorkTool 发送消息](https://doc.worktool.ymdyes.cn/api-23520034)
3. [WorkTool 消息回调接口规范](https://doc.worktool.ymdyes.cn/doc-861677)
4. [WorkTool QA 回调不回复示例](https://doc.worktool.ymdyes.cn/api-58780619)
5. [WorkTool 创建外部群](https://doc.worktool.ymdyes.cn/api-23520350)
6. [WorkTool 修改群信息](https://doc.worktool.ymdyes.cn/api-23520590)
