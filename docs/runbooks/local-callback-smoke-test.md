# 本地 WorkTool 双回调冒烟测试

本流程使用真实 API、Worker、MySQL 和 Qdrant，但所有 WorkTool 流量只发送到
`127.0.0.1` 的假服务。不得填写真实 WorkTool 地址、机器人编号、回调密钥或企业微信凭据。

本流程验证两条不同链路：

- 消息回调：假 WorkTool → `/api/worktool/callback/{routeCode}` → 入站处理。
- 指令结果回调：Worker → `sendRawMessage` → 假 WorkTool 接受并返回 `messageId`
  → `/api/worktool/command-results/{routeCode}` → `executedSucceeded`。

HTTP 200 和 `code=0` 只表示 WorkTool 已接受命令，不代表机器人已执行。只有关联同一
`messageId` 的 type-1 指令结果回调才能把发送状态确认为 `executedSucceeded`。

## 前提条件

- Docker Desktop、`docker compose` 和 `dotnet` 可用。
- 从仓库根目录执行命令。
- 示例端口 `33316`、`36333`、`36334`、`5501`、`5588` 未占用。
- `.superpowers/sdd/checkpoint-1.env` 只保存本次测试的临时 MySQL/Qdrant 值，且不得提交。

## 启动隔离依赖与假 WorkTool

```powershell
docker compose --env-file .superpowers/sdd/checkpoint-1.env -p wechatrobot-checkpoint1 up -d --wait --wait-timeout 120
docker compose --env-file .superpowers/sdd/checkpoint-1.env -p wechatrobot-checkpoint1 ps
Invoke-WebRequest http://127.0.0.1:36333/readyz

$fakeLog = (Resolve-Path .superpowers/sdd).Path + '\fake-worktool.log'
$fake = Start-Process powershell -PassThru -WindowStyle Hidden -ArgumentList @(
  '-NoProfile','-ExecutionPolicy','Bypass','-File',
  'scripts/Start-FakeWorkTool.ps1','-LogPath',$fakeLog
)
```

假服务实现机器人信息、在线查询、消息回调配置、type-1 指令结果回调绑定/删除和
`sendRawMessage`。每次发送会返回确定性的 `fake-command-000001` 形式 `messageId`，
并向已绑定的 type-1 URL 回传结果码 0。

## 启动 API 与 Worker

在 API 窗口设置以下仅供本地验证的值。`Development` 只用于允许回环 HTTP 回调；
外部地址仍必须使用 HTTPS。

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__WechatRobot = 'Server=127.0.0.1;Port=33316;Database=wechatrobot;User=<local-user>;Password=<local-password>'
$env:Database__ApplyMigrationsOnStartup = 'true'
$env:WECHATROBOT_MASTER_KEY_BASE64 = '<base64-encoded-32-byte-local-key>'
$env:Jwt__Issuer = 'checkpoint-1'
$env:Jwt__Audience = 'checkpoint-1-api'
$env:Jwt__SigningKey = '<local-signing-key-at-least-32-characters>'
$env:BootstrapAdmin__Email = 'checkpoint-admin@example.test'
$env:BootstrapAdmin__Password = '<temporary-strong-local-password>'
$env:BootstrapAdmin__DisplayName = 'Checkpoint Admin'
$env:WorkTool__BaseUrl = 'http://127.0.0.1:5588/'
dotnet run --project src/server/WechatRobot.Api --urls http://127.0.0.1:5501
```

在 Worker 窗口使用相同数据库和主密钥：

```powershell
$env:ConnectionStrings__WechatRobot = 'Server=127.0.0.1;Port=33316;Database=wechatrobot;User=<local-user>;Password=<local-password>'
$env:WECHATROBOT_MASTER_KEY_BASE64 = '<与 API 完全相同的主密钥>'
$env:WorkTool__BaseUrl = 'http://127.0.0.1:5588/'
$env:FixedReply__Text = '检查点固定回复'
$env:FixedReply__SendRateLimitPerMinute = '50'
dotnet run --project src/server/WechatRobot.Worker
```

## 通过管理 API 创建测试机器人并配置双回调

不要直接写 `WorkToolRobotId`、回调路由或回调密钥列；这些字段由管理 API 加密或随机生成。

```powershell
$apiBase = 'http://127.0.0.1:5501/'
$robotId = '11111111-1111-1111-1111-111111111111'
$loginBody = @{
  email = 'checkpoint-admin@example.test'
  password = '<temporary-strong-local-password>'
} | ConvertTo-Json
$login = Invoke-RestMethod -Method Post -Uri "${apiBase}api/auth/login" -ContentType 'application/json' -Body $loginBody
$adminToken = $login.accessToken
$headers = @{ Authorization = "Bearer $adminToken" }

$robotBody = @{
  name = 'checkpoint-1 robot'
  workToolRobotId = 'checkpoint-1-robot'
  isEnabled = $true
} | ConvertTo-Json
Invoke-RestMethod -Method Put -Uri "${apiBase}api/admin/worktool/robots/$robotId" -Headers $headers -ContentType 'application/json' -Body $robotBody

$groupBody = @{
  robotConfigId = $robotId
  name = 'Checkpoint Group'
  workToolGroupRemark = 'Checkpoint Group'
  manualInvitationCompleted = $true
} | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "${apiBase}api/admin/worktool/groups/register" -Headers $headers -ContentType 'application/json' -Body $groupBody

.\scripts\update-worktool-callback.ps1 `
  -ApiBaseUrl $apiBase `
  -PublicBaseUrl $apiBase `
  -Email 'checkpoint-admin@example.test' `
  -Apply `
  -Confirmation UPDATE
```

The script prompts securely for the temporary bootstrap administrator password.
This checkpoint assumes the local database contains only the enabled checkpoint
robot; otherwise select it by its displayed name when prompted.

脚本只调用本系统的两个管理员端点，不直接调用 WorkTool：

- `POST /api/admin/worktool/robots/{id}/message-callback/configure`
- `POST /api/admin/worktool/robots/{id}/command-result-callback/configure`

脚本输出不得包含管理令牌、WorkTool 机器人编号、随机回调路由或回调查询密钥。

## 发送重复入站消息并核验最终执行

`/fake/inbound` 是回环假服务专用触发器，它使用已配置但不公开的消息回调 URL 转发载荷：

```powershell
$payload = @{
  spoken = '检查点：无需 @ 的群消息'
  rawSpoken = '检查点：无需 @ 的群消息'
  receivedName = 'Checkpoint Sender'
  groupName = 'Checkpoint Group'
  groupRemark = 'Checkpoint Group'
  roomType = 1
  atMe = $false
  textType = 1
  messageId = 'checkpoint-no-at-001'
} | ConvertTo-Json -Compress
$fakeInbound = 'http://127.0.0.1:5588/fake/inbound'
Invoke-WebRequest -UseBasicParsing -Method Post -Uri $fakeInbound -ContentType 'application/json' -Body $payload
Invoke-WebRequest -UseBasicParsing -Method Post -Uri $fakeInbound -ContentType 'application/json' -Body $payload
```

等待 Worker 和指令结果回调完成后执行：

```powershell
$verificationSql = @"
SELECT
  (SELECT COUNT(*) FROM conversation_message WHERE RobotConfigId = '$robotId') AS inbound_rows,
  (SELECT COUNT(*) FROM durable_job WHERE JobType = 'ProcessInboundMessage' AND PayloadJson LIKE '%$robotId%') AS process_jobs,
  (SELECT COUNT(*) FROM durable_job WHERE JobType = 'ProcessInboundMessage' AND PayloadJson LIKE '%$robotId%' AND Status = 'completed') AS completed_process_jobs,
  (SELECT COUNT(*) FROM send_command WHERE RobotConfigId = '$robotId') AS send_commands,
  (SELECT COUNT(*) FROM send_command WHERE RobotConfigId = '$robotId' AND Status = 'executedSucceeded' AND WorkToolCommandMessageId IS NOT NULL AND WorkToolResultCode = 0 AND WorkToolResultAtUtc IS NOT NULL) AS executed_sends;
"@
$verificationSql | docker compose --env-file .superpowers/sdd/checkpoint-1.env -p wechatrobot-checkpoint1 exec -T mysql sh -lc 'mysql -uroot -p"$MYSQL_ROOT_PASSWORD" "$MYSQL_DATABASE"'
```

期望统计为 `1, 1, 1, 1, 1`。`fake-worktool.log` 中应只有一次该机器人对应的
`POST /wework/sendRawMessage`，并出现一次成功的指令结果回调；日志不会保存回调配置正文。
若状态仍为 `accepted`，只能说明 WorkTool 已接收，不能把本次冒烟测试记为执行成功。

## 清理

```powershell
Remove-Item Env:ASPNETCORE_ENVIRONMENT, Env:ConnectionStrings__WechatRobot, Env:Database__ApplyMigrationsOnStartup
Remove-Item Env:WECHATROBOT_MASTER_KEY_BASE64, Env:Jwt__Issuer, Env:Jwt__Audience, Env:Jwt__SigningKey
Remove-Item Env:BootstrapAdmin__Email, Env:BootstrapAdmin__Password, Env:BootstrapAdmin__DisplayName, Env:WorkTool__BaseUrl
Stop-Process -Id $fake.Id
docker compose --env-file .superpowers/sdd/checkpoint-1.env -p wechatrobot-checkpoint1 down
```

关闭本流程启动的 API/Worker 窗口，删除临时 env 和假服务日志。不要执行 `down -v`。
