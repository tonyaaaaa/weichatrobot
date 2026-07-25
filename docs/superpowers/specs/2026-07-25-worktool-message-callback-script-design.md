# WorkTool 消息回调配置脚本设计

日期：2026-07-25

## 目标

提供一个可重复运行的 PowerShell 脚本，通过 `https://wxrobot.aavisa.com`
登录 WechatRobot 管理 API、选择机器人并配置 WorkTool 消息回调。脚本和终端输出
不得暴露 WorkTool Robot ID、回调 Token 或管理员密码。

本功能配置的是 WorkTool“机器人消息回调”，不是群二维码、指令执行结果、上线或
下线事件回调。

## 已确认的外部契约

WorkTool 的消息回调配置接口为：

```text
POST https://api.worktool.ymdyes.cn/robot/robotInfo/update?robotId={robotId}
```

请求体为：

```json
{
  "openCallback": 1,
  "replyAll": 1,
  "callbackUrl": "https://wxrobot.aavisa.com/api/worktool/callback/{routeCode}?token={callbackToken}"
}
```

`replyAll=1` 用于接收未 `@` 机器人的消息，符合当前产品处理外部群普通文本消息的
目标。WorkTool 要求消息回调在 3 秒内返回 JSON；现有接收端会持久化入队并立即返回
`{"code":0,"message":"accepted"}`。

现有 `/api/admin/worktool/robots/{id}/callbacks/bind` 调用
`/robot/robotInfo/callBack/bind`，它配置的是类型 0、1、5、6 的通用事件回调。
其中类型 1 是指令执行结果，不是聊天消息。因此本功能不复用该接口。

## 总体方案

PowerShell 只与 WechatRobot 管理 API 通信。后端负责生成消息回调地址、解析加密的
WorkTool Robot ID，并调用 WorkTool。这样脚本不需要接触 WorkTool Robot ID 或明文
回调 Token。

### PowerShell 脚本

更新 `scripts/update-worktool-callback.ps1`，默认 API 和公网基址均为
`https://wxrobot.aavisa.com`，同时允许测试通过参数覆盖为本机回环地址。

脚本执行流程：

1. 提示输入管理员邮箱。
2. 使用 `Read-Host -AsSecureString` 读取密码，只在构造登录请求的最小作用域内转换，
   请求结束后清除临时引用；不写文件、不打印请求体。
3. 调用 `POST /api/auth/login` 获取短期 Bearer Token。
4. 调用 `GET /api/admin/worktool/robots` 获取机器人列表。
5. 没有机器人时明确失败；只有一个时自动选择；多个时显示名称和启用状态并要求输入
   序号。列表不包含 WorkTool Robot ID。
6. 对禁用机器人拒绝继续。
7. 显示机器人名称、固定公网域名和即将执行的“消息回调配置”动作，要求输入
   `UPDATE`。
8. 调用新的后端消息回调配置接口。
9. 成功时只显示机器人名称、脱敏回调路由和 WorkTool 已接受配置；不显示 Bearer
   Token、真实路由码、回调 Token或 Robot ID。

脚本默认只预览。只有传入 `-Apply` 且交互确认成功后才发送配置请求。
`-WhatIf` 不执行登录或任何网络请求。自动化测试仅允许通过显式测试参数对回环地址
提供非交互凭据和确认值；这些测试参数不得用于非回环目标。

### 后端管理接口

新增：

```text
POST /api/admin/worktool/robots/{id}/message-callback/configure
```

请求体：

```json
{
  "publicBaseUrl": "https://wxrobot.aavisa.com",
  "replyAll": true
}
```

接口继续要求 `Admin` 角色和 WorkTool 命令限流。生产环境只接受无用户信息、路径、
查询或片段的 HTTPS Origin。Development/Testing 环境可接受严格回环 HTTP 地址。

后端执行：

1. 查询启用的机器人配置，并确认存在 `CallbackRouteCode` 和可解密的 WorkTool
   Robot ID。
2. 生成至少 32 字节的密码学随机回调 Token。
3. 使用 `CallbackRouteCode` 构造接收路由，并将 Token 放入查询参数。
4. 调用 WorkTool `/robot/robotInfo/update`，发送
   `openCallback=1`、`replyAll=1` 和完整回调地址。
5. 仅在 WorkTool 返回 HTTP 成功且业务 `code=0` 后，把新 Token 的 SHA-256 写入
   `CallbackSecretHash`。
6. 返回成功状态及脱敏回调地址；响应不得包含路由码、Token 或 Robot ID。

为避免 WorkTool 已接受而数据库保存失败造成不可恢复的不一致，数据库更新失败时，
接口返回明确的配置不一致错误并记录不含敏感值的高严重度日志。正常失败路径中，
WorkTool 拒绝或网络失败都不得修改现有 `CallbackSecretHash`。

### WorkTool 客户端

在 `IWorkToolClient` 和 `WorkToolClient` 增加独立的消息回调配置方法，不能复用
`BindCallbackAsync`。方法使用已有凭据解析器取得 Robot ID，并调用
`robot/robotInfo/update`。

客户端只返回结构化成功或失败结果。异常、日志和 API 错误响应不得包含请求 URL
查询参数、Robot ID、完整回调 URL 或 WorkTool 原始响应体。

## 数据与安全边界

- `ApplicationUser` 只负责管理员身份认证。
- `RobotConfigEntity.Id` 是管理 API 选择机器人的内部 ID。
- 加密的 WorkTool Robot ID 只在后端凭据解析器与 WorkTool 客户端边界内出现。
- `CallbackRouteCode` 是不可猜测路由标识，不返回给脚本。
- 回调 Token 只在单次后端操作内以明文存在，数据库仅保存 SHA-256。
- PowerShell 输出、测试断言、审计和日志只使用机器人名称、内部 ID或单向指纹。

## 错误处理

- 登录失败：输出通用认证失败，不回显邮箱或密码。
- 非管理员：输出权限不足。
- 没有机器人、选择无效或机器人已禁用：不调用 WorkTool。
- 公网域名格式非法：在后端调用 WorkTool 前拒绝。
- WorkTool 超时、HTTP 失败、业务 `code != 0` 或无效 JSON：返回安全的 502，保留旧
  Token 哈希。
- 用户未输入精确的 `UPDATE`：脚本退出且不修改任何配置。

## 测试与验收

### 后端测试

- 合约测试验证准确的 WorkTool 路径、查询参数和请求体。
- 集成测试验证只有管理员可配置消息回调。
- 验证禁用或不存在的机器人不会调用 WorkTool。
- 验证成功后新 Token 能通过现有消息回调认证，旧 Token 失效。
- 验证 WorkTool 拒绝、超时和无效响应时旧 Token 仍有效。
- 验证 API 响应、日志和异常不包含 Robot ID、路由码或 Token。
- 保留现有通用事件回调测试，证明两个接口用途互不混淆。

### PowerShell 测试

- `-WhatIf` 不产生网络请求。
- 预览输出不包含管理员密码、Bearer Token、Robot ID、路由码或回调 Token。
- 单机器人自动选择，多机器人按序号选择。
- 禁用机器人、错误确认和业务失败均安全退出。
- 仅对回环假 API 运行自动化应用测试，不访问真实 WorkTool。

### 人工验收

1. 确认 `https://wxrobot.aavisa.com/health/live` 可达。
2. 以管理员运行脚本并选择目标机器人。
3. 输入 `UPDATE` 后确认脚本报告 WorkTool 已接受消息回调。
4. 在 WorkTool APP 中确认“新消息接收”已开启。
5. 在测试外部群发送一条不含敏感信息且不 `@` 机器人的文本。
6. 确认 API 在 3 秒内接受回调，并且消息只持久化一次。

真实 WorkTool 验收必须显式执行，不纳入默认自动化测试。

## 官方参考

- [WorkTool 消息回调接口规范](https://worktool.apifox.cn/doc-861677)
- [WorkTool 机器人消息回调配置](https://worktool.apifox.cn/api-22587884)
- [WorkTool 通用回调类型与查询](https://worktool.apifox.cn/api-44588019)
