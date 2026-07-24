# 本地 WorkTool 回调冒烟测试

该检查点使用真实的 API、Worker、MySQL 和 Qdrant 进程，但将 WorkTool 发送目标限定为本机回环地址的假服务。不得在此流程中填写真实 WorkTool 地址、机器人编号、回调密钥或企业微信凭据。

## 前提条件

- Docker Desktop 正在运行，`docker compose version` 和 `dotnet --version` 可用。
- 从仓库根目录执行命令；不要占用下列示例端口：`33316`、`36333`、`36334`、`5501`、`5588`。
- 创建只供本次执行使用的忽略文件 `.superpowers/sdd/checkpoint-1.env`。其中包含 `MYSQL_ROOT_PASSWORD`、`MYSQL_DATABASE`、`MYSQL_USER`、`MYSQL_PASSWORD`、`MYSQL_PORT`、`QDRANT_HTTP_PORT`、`QDRANT_GRPC_PORT` 和 `QDRANT_API_KEY`；不得提交该文件。

## 启动隔离依赖

使用独立 Compose 项目和独立端口，避免干扰常规本地服务：

```powershell
docker compose --env-file .superpowers/sdd/checkpoint-1.env -p wechatrobot-checkpoint1 up -d --wait --wait-timeout 120
docker compose --env-file .superpowers/sdd/checkpoint-1.env -p wechatrobot-checkpoint1 ps
Invoke-WebRequest http://127.0.0.1:36333/readyz
```

MySQL 和 Qdrant 均应为 `healthy`；Qdrant 的 `/readyz` 应返回 200。`docker-compose.yml` 的 Qdrant 健康检查使用镜像自带的 Bash TCP 请求，不能依赖镜像中不存在的 `wget`。

## 启动本地假 WorkTool、API 和 Worker

启动假服务，它只记录请求并固定返回 `{"code":0}`：

```powershell
$fakeLog = (Resolve-Path .superpowers/sdd).Path + '\fake-worktool.log'
$fake = Start-Process powershell -PassThru -WindowStyle Hidden -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File','scripts/Start-FakeWorkTool.ps1','-LogPath',$fakeLog)
```

在两个独立 PowerShell 窗口中设置仅供本地验证使用的环境变量并启动应用。以下密码和值均应替换为临时本地值，不能使用或记录生产凭据：

```powershell
$env:ConnectionStrings__WechatRobot = 'Server=127.0.0.1;Port=33316;Database=wechatrobot;User=<local-user>;Password=<local-password>'
$env:Cors__AllowedOrigins__0 = 'http://127.0.0.1:5173'
$env:Database__ApplyMigrationsOnStartup = 'true'
$env:WECHATROBOT_MASTER_KEY_BASE64 = '<base64-encoded-32-byte-local-key>'
$env:Jwt__Issuer = 'checkpoint-1'
$env:Jwt__Audience = 'checkpoint-1-api'
$env:Jwt__SigningKey = '<local-signing-key-at-least-32-characters>'
dotnet run --project src/server/WechatRobot.Api --urls http://127.0.0.1:5501
```

```powershell
$env:ConnectionStrings__WechatRobot = 'Server=127.0.0.1;Port=33316;Database=wechatrobot;User=<local-user>;Password=<local-password>'
$env:WorkTool__BaseUrl = 'http://127.0.0.1:5588/'
$env:FixedReply__Text = '检查点固定回复'
$env:FixedReply__SendRateLimitPerMinute = '50'
dotnet run --project src/server/WechatRobot.Worker
```

## 初始化临时机器人

API 完成迁移后，在仓库根目录执行以下 PowerShell。示例使用固定的测试 ID 与机器人编号，便于后续 SQL 核验；回调密钥仍应在本机临时生成，不能替换成真实密钥或提交到仓库。`CallbackSecretHash` 是 API 回调认证实际比较的 SHA-256 十六进制摘要。

```powershell
$robotId = '11111111-1111-1111-1111-111111111111'
$robotCode = 'checkpoint-1-robot'
$callbackSecret = 'checkpoint-' + [Guid]::NewGuid().ToString('N')
$callbackSecretHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($callbackSecret)))

# Re-running the test starts from a clean record set for this fixed test robot.
$cleanupSql = @"
DELETE letter FROM dead_letter AS letter INNER JOIN send_command AS command ON command.Id = letter.SendCommandId WHERE command.RobotConfigId = '$robotId';
DELETE letter FROM dead_letter AS letter INNER JOIN durable_job AS job ON job.Id = letter.DurableJobId WHERE job.PayloadJson LIKE '%$robotId%';
DELETE FROM send_command WHERE RobotConfigId = '$robotId';
DELETE FROM durable_job WHERE PayloadJson LIKE '%$robotId%';
DELETE FROM conversation_message WHERE RobotConfigId = '$robotId';
DELETE FROM group_profile WHERE RobotConfigId = '$robotId';
DELETE FROM robot_config WHERE Id = '$robotId';
"@
$cleanupSql | docker compose --env-file .superpowers/sdd/checkpoint-1.env -p wechatrobot-checkpoint1 exec -T mysql sh -lc 'mysql -uroot -p"$MYSQL_ROOT_PASSWORD" "$MYSQL_DATABASE"'

# All required robot_config columns are explicit for the current migrations.
$bootstrapSql = @"
INSERT INTO robot_config (
  Id, Name, WorkToolRobotId, CallbackSecretHash, IsEnabled,
  SendRateLimitPerMinute, SendRateTokens, SendRateUpdatedAtUtc,
  SendLeaseOwner, SendLeaseExpiresAtUtc, SendCoordinationVersion,
  CreatedAtUtc, UpdatedAtUtc
) VALUES (
  '$robotId', 'checkpoint-1 robot', '$robotCode', '$callbackSecretHash', 1,
  50, 50, UTC_TIMESTAMP(6),
  NULL, NULL, 0,
  UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
);
SELECT Id, WorkToolRobotId, SendRateLimitPerMinute, SendRateTokens FROM robot_config WHERE Id = '$robotId';
"@
$bootstrapSql | docker compose --env-file .superpowers/sdd/checkpoint-1.env -p wechatrobot-checkpoint1 exec -T mysql sh -lc 'mysql -uroot -p"$MYSQL_ROOT_PASSWORD" "$MYSQL_DATABASE"'
```

该记录满足当前迁移的必需字段和 `SendRateLimitPerMinute BETWEEN 1 AND 60` 约束；初始 `SendRateTokens=50` 与 `SendRateUpdatedAtUtc=UTC_TIMESTAMP(6)` 允许 Worker 正常取得发送租约。保存当前 PowerShell 会话中的 `$robotId`、`$robotCode` 和 `$callbackSecret`，供下一步回调与核验使用。

## 执行回调并核验

对同一个记录的 no-at 群消息发送两次；示例的业务载荷必须包含 `roomType: 1` 与 `atMe: false`：

```powershell
$payload = @{ spoken='检查点：无需 @ 的群消息'; rawSpoken='检查点：无需 @ 的群消息'; receivedName='Checkpoint Sender'; groupName='Checkpoint Group'; groupRemark='Checkpoint Group'; roomType=1; atMe=$false; textType=1; messageId='checkpoint-no-at-001' } | ConvertTo-Json -Compress
$uri = "http://127.0.0.1:5501/api/worktool/callback/$robotCode?token=$callbackSecret"
Invoke-WebRequest -UseBasicParsing -Method Post -Uri $uri -ContentType 'application/json' -Body $payload
Invoke-WebRequest -UseBasicParsing -Method Post -Uri $uri -ContentType 'application/json' -Body $payload
```

两次响应均必须是 HTTP 200 且正文为 `{"code":0,"message":"accepted"}`。随后在同一隔离 MySQL 中执行下面的统计；继续使用上一步的 `$robotId`：

```powershell
$verificationSql = @"
SELECT
  (SELECT COUNT(*) FROM conversation_message WHERE RobotConfigId = '$robotId') AS inbound_rows,
  (SELECT COUNT(*) FROM durable_job WHERE JobType = 'ProcessInboundMessage' AND PayloadJson LIKE '%$robotId%') AS process_jobs,
  (SELECT COUNT(*) FROM durable_job WHERE JobType = 'ProcessInboundMessage' AND PayloadJson LIKE '%$robotId%' AND Status = 'completed') AS completed_process_jobs,
  (SELECT COUNT(*) FROM send_command WHERE RobotConfigId = '$robotId') AS send_commands,
  (SELECT COUNT(*) FROM send_command WHERE RobotConfigId = '$robotId' AND Status = 'completed') AS completed_sends;
"@
$verificationSql | docker compose --env-file .superpowers/sdd/checkpoint-1.env -p wechatrobot-checkpoint1 exec -T mysql sh -lc 'mysql -uroot -p"$MYSQL_ROOT_PASSWORD" "$MYSQL_DATABASE"'
```

期望统计为 `1, 1, 1, 1, 1`，并且 `fake-worktool.log` 中只出现一行 `POST /wework/sendRawMessage?robotId=checkpoint-1-robot`。

## 2026-07-22 实测证据

- Compose 项目：`wechatrobot-checkpoint1`；MySQL `33316`，Qdrant HTTP `36333`、gRPC `36334`；两项服务均为 `healthy`，`/readyz` 返回 `200 all shards are ready`。
- API：`http://127.0.0.1:5501`；Worker 的 WorkTool 基地址：`http://127.0.0.1:5588/`。
- 两次回调均得到 `200 {"code":0,"message":"accepted"}`。
- 最终 SQL 统计为 `1  1  1  1  1`：一条入站消息、一条已完成的处理任务和一条已完成的发送命令。
- 假 WorkTool 日志恰有一次针对测试机器人发送的 `POST /wework/sendRawMessage` 请求。未访问真实 WorkTool、企业微信或任何生产地址。

## 清理

仅停止本检查点启动的进程和 Compose 服务；不要使用 `down -v`，以免删除卷：

```powershell
Stop-Process -Id $fake.Id
docker compose --env-file .superpowers/sdd/checkpoint-1.env -p wechatrobot-checkpoint1 down
```

分别关闭启动 API 与 Worker 的 PowerShell 窗口，或仅终止这些窗口创建的 `dotnet` 子进程。若需在不删除 Compose 卷的前提下删除测试记录，重新执行“初始化临时机器人”中的 `$cleanupSql` 管道；最后删除 `.superpowers/sdd/checkpoint-1.env` 和本次本地日志；它们本来就不受 Git 跟踪。
