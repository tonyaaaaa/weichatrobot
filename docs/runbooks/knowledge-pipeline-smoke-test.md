# 本地知识库管线冒烟测试

本检查点运行真实的 ASP.NET Core API、Worker、MySQL 8.4.10 和 Qdrant 1.18.2，但对象存储、OpenAI 兼容 Embedding 和 OCR 地址均固定为 `127.0.0.1`。不要填写真实 OSS、模型、WorkTool 或企业微信凭据。

## 1. 端口和临时配置

从仓库根目录执行。示例端口为 MySQL `33326`、Qdrant `36343/36344`、API `5502`、假提供商 `5591`。创建被 Git 忽略的 `.superpowers/sdd/checkpoint-2.env`：

```dotenv
MYSQL_ROOT_PASSWORD=<local-only-root-password>
MYSQL_DATABASE=wechatrobot_checkpoint2
MYSQL_USER=checkpoint2
MYSQL_PASSWORD=<local-only-user-password>
MYSQL_PORT=33326
QDRANT_HTTP_PORT=36343
QDRANT_GRPC_PORT=36344
QDRANT_API_KEY=<local-only-qdrant-key>
OCR_PORT=18010
```

只启动本检查点需要的两个容器；OCR 使用回环假服务，不构建 PaddleOCR 镜像：

```powershell
$checkpointEnv = @{}
Get-Content .superpowers/sdd/checkpoint-2.env | Where-Object { $_ -match '^[^#].+=' } | ForEach-Object {
  $name,$value = $_.Split('=',2)
  $checkpointEnv[$name] = $value
}
$env:CHECKPOINT2_MYSQL_PASSWORD = $checkpointEnv.MYSQL_PASSWORD
$env:CHECKPOINT2_QDRANT_API_KEY = $checkpointEnv.QDRANT_API_KEY
docker compose --env-file .superpowers/sdd/checkpoint-2.env -p wechatrobot-checkpoint2 up -d --wait --wait-timeout 180 mysql qdrant
docker compose --env-file .superpowers/sdd/checkpoint-2.env -p wechatrobot-checkpoint2 ps
Invoke-WebRequest -UseBasicParsing http://127.0.0.1:36343/readyz -Headers @{ 'api-key' = $env:CHECKPOINT2_QDRANT_API_KEY }
```

## 2. 启动回环假提供商

假服务将 PUT 对象保存到忽略目录，GET 返回相同字节，并按输入文本生成固定维度向量。OCR 端点故意报错，以便文本 PDF 意外进入 OCR 时立即暴露；本检查点把文本 PDF 阈值设为 `0`，预期 OCR 调用数为零。

```powershell
$objectRoot = (Resolve-Path .superpowers/sdd).Path + '\checkpoint-2-objects'
$providerLog = (Resolve-Path .superpowers/sdd).Path + '\checkpoint-2-provider.log'
$repoRoot = (Resolve-Path .).Path
$fakeProvider = Start-Process powershell -PassThru -WindowStyle Hidden -WorkingDirectory $repoRoot `
  -RedirectStandardOutput (Join-Path $repoRoot '.superpowers/sdd/checkpoint-2-provider.out.log') `
  -RedirectStandardError (Join-Path $repoRoot '.superpowers/sdd/checkpoint-2-provider.err.log') -ArgumentList @(
  '-NoProfile','-ExecutionPolicy','Bypass','-File',
  (Resolve-Path scripts/Start-FakeKnowledgeProviders.ps1).Path,
  '-ObjectRoot',$objectRoot,'-LogPath',$providerLog,'-Port','5591','-EmbeddingDimension','8'
)
$deadline = (Get-Date).AddSeconds(30)
do {
  $fakeProvider.Refresh()
  if ($fakeProvider.HasExited) { throw 'Fake provider exited before readiness.' }
  $providerReady = (Test-NetConnection 127.0.0.1 -Port 5591 -WarningAction SilentlyContinue).TcpTestSucceeded
  if (-not $providerReady) { Start-Sleep -Milliseconds 500 }
} while (-not $providerReady -and (Get-Date) -lt $deadline)
if (-not $providerReady) { throw 'Fake provider readiness timed out.' }
```

`LoopbackObjectStorage` 和回环 HTTP 文档源只接受显式启用的 `http://localhost` 或 `http://127.0.0.1`，禁用自动重定向，且 API/Worker 仅允许在 `Development` 环境注册。生产默认仍是阿里云 OSS。

## 3. 启动 API 和 Worker

在当前 PowerShell 会话中设置仅供检查点使用的临时环境变量，然后启动已构建的 API 和 Worker 可执行文件。`Start-Process` 返回值必须保留到第 8 节，作为限定清理的唯一 PID 来源：

```powershell
$repoRoot = (Resolve-Path .).Path
$logRoot = (Resolve-Path .superpowers/sdd).Path
$checkpointAdminPassword = 'Checkpoint2!LocalOnly'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DOTNET_ENVIRONMENT = 'Development'
$env:ConnectionStrings__WechatRobot = "Server=127.0.0.1;Port=33326;Database=wechatrobot_checkpoint2;User=checkpoint2;Password=$env:CHECKPOINT2_MYSQL_PASSWORD"
$env:Cors__AllowedOrigins__0 = 'http://127.0.0.1:5173'
$env:Database__ApplyMigrationsOnStartup = 'true'
$env:WECHATROBOT_MASTER_KEY_BASE64 = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:Jwt__Issuer = 'checkpoint-2'
$env:Jwt__Audience = 'checkpoint-2-api'
$env:Jwt__SigningKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
$env:BootstrapAdmin__Email = 'checkpoint2@example.test'
$env:BootstrapAdmin__Password = $checkpointAdminPassword
$env:BootstrapAdmin__DisplayName = 'Checkpoint 2 Admin'
$env:ObjectStorage__Provider = 'loopback'
$env:LoopbackObjectStorage__BaseUrl = 'http://127.0.0.1:5591/objects/'
$env:DocumentSource__AllowLoopbackHttp = 'true'
$env:Oss__PublicReadRiskAccepted = 'true'
$env:Qdrant__BaseUrl = 'http://127.0.0.1:36343/'
$env:Qdrant__ApiKey = $env:CHECKPOINT2_QDRANT_API_KEY
$env:KnowledgeIndex__Dimension = '8'
$env:KnowledgeIndex__BatchSize = '2'
$env:KnowledgeIndex__MaximumCollectionsPerSearch = '64'
$env:Ocr__BaseAddress = 'http://127.0.0.1:5591/'
$env:Ocr__MinimumExtractedTextCharacters = '0'
$env:WorkTool__BaseUrl = 'http://127.0.0.1:5591/'
$env:FixedReply__Text = 'checkpoint-2-local-only'

dotnet build WechatRobot.slnx --no-restore
$apiWorkingDirectory = (Resolve-Path src/server/WechatRobot.Api).Path
$workerWorkingDirectory = (Resolve-Path src/server/WechatRobot.Worker).Path
$apiExecutable = (Resolve-Path src/server/WechatRobot.Api/bin/Debug/net10.0/WechatRobot.Api.exe).Path
$workerExecutable = (Resolve-Path src/server/WechatRobot.Worker/bin/Debug/net10.0/WechatRobot.Worker.exe).Path
$apiProcess = Start-Process $apiExecutable -PassThru -WindowStyle Hidden -WorkingDirectory $apiWorkingDirectory `
  -ArgumentList @('--urls','http://127.0.0.1:5502') `
  -RedirectStandardOutput (Join-Path $logRoot 'checkpoint-2-api.out.log') `
  -RedirectStandardError (Join-Path $logRoot 'checkpoint-2-api.err.log')
$workerProcess = Start-Process $workerExecutable -PassThru -WindowStyle Hidden -WorkingDirectory $workerWorkingDirectory `
  -RedirectStandardOutput (Join-Path $logRoot 'checkpoint-2-worker.out.log') `
  -RedirectStandardError (Join-Path $logRoot 'checkpoint-2-worker.err.log')

$deadline = (Get-Date).AddMinutes(2)
do {
  $apiProcess.Refresh(); $workerProcess.Refresh()
  if ($apiProcess.HasExited) { throw 'API exited before readiness.' }
  if ($workerProcess.HasExited) { throw 'Worker exited before readiness.' }
  try { $apiReady = (Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5502/ -TimeoutSec 2).StatusCode -eq 200 }
  catch { $apiReady = $false }
  $workerReady = (Test-Path (Join-Path $logRoot 'checkpoint-2-worker.out.log')) -and
    [bool](Select-String -Quiet -Path (Join-Path $logRoot 'checkpoint-2-worker.out.log') -Pattern 'Application started')
  if (-not ($apiReady -and $workerReady)) { Start-Sleep -Seconds 1 }
} while (-not ($apiReady -and $workerReady) -and (Get-Date) -lt $deadline)
if (-not ($apiReady -and $workerReady)) { throw 'API or Worker readiness timed out.' }
```

## 4. 初始化身份、Embedding、群和标签

登录临时管理员，并仅保存当前会话中的 Bearer Token：

```powershell
$login = Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5502/api/auth/login -ContentType 'application/json' -Body (@{
  email='checkpoint2@example.test'; password=$checkpointAdminPassword
} | ConvertTo-Json)
$headers = @{ Authorization = "Bearer $($login.accessToken)" }

$model = @{ provider='loopback'; configurationType='embedding'; baseUrl='http://127.0.0.1:5591'; model='checkpoint-embedding-8';
  apiKey='local-only'; timeoutSeconds=10; maxRetries=0; isEnabled=$true; isDefault=$true } | ConvertTo-Json
Invoke-RestMethod -Method Put -Uri http://127.0.0.1:5502/api/admin/model-configurations/checkpoint-embedding -Headers $headers -ContentType 'application/json' -Body $model
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5502/api/admin/model-configurations/checkpoint-embedding/test-connection -Headers $headers
```

在隔离 MySQL 中幂等创建测试数据。中文使用 UTF-8 十六进制字面量，避免终端代码页污染：

```sql
INSERT IGNORE INTO robot_config
  (Id,Name,WorkToolRobotId,CallbackSecretHash,IsEnabled,SendRateLimitPerMinute,SendRateTokens,SendRateUpdatedAtUtc,SendCoordinationVersion,CreatedAtUtc,UpdatedAtUtc)
VALUES
  ('20000000-0000-0000-0000-000000000001','checkpoint-2','checkpoint-2-robot','local-only',1,50,50,UTC_TIMESTAMP(),0,UTC_TIMESTAMP(),UTC_TIMESTAMP());
INSERT IGNORE INTO group_profile
  (Id,RobotConfigId,ExternalGroupId,Name,IsEnabled,CreatedAtUtc,UpdatedAtUtc)
VALUES
  ('30000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000001','checkpoint-2-group',CONVERT(0xE68A80E69CAFE983A8 USING utf8mb4),1,UTC_TIMESTAMP(),UTC_TIMESTAMP());
INSERT IGNORE INTO knowledge_tag (Id,Name,NormalizedName,IsEnabled,IsGlobalPublic,CreatedAtUtc) VALUES
  ('10000000-0000-0000-0000-000000000001',CONVERT(0xE4BAA7E59381 USING utf8mb4),CONVERT(0xE4BAA7E59381 USING utf8mb4),1,0,UTC_TIMESTAMP()),
  ('10000000-0000-0000-0000-000000000002',CONVERT(0xE594AEE5908E USING utf8mb4),CONVERT(0xE594AEE5908E USING utf8mb4),1,0,UTC_TIMESTAMP()),
  ('10000000-0000-0000-0000-000000000003',CONVERT(0xE8B4A2E58AA1 USING utf8mb4),CONVERT(0xE8B4A2E58AA1 USING utf8mb4),1,0,UTC_TIMESTAMP()),
  ('10000000-0000-0000-0000-000000000004',CONVERT(0xE585A8E5B180E585ACE5BC80 USING utf8mb4),CONVERT(0xE585A8E5B180E585ACE5BC80 USING utf8mb4),1,1,UTC_TIMESTAMP());
```

把 SQL 保存为忽略文件 `.superpowers/sdd/checkpoint-2-bootstrap.sql`，再执行：

```powershell
Get-Content -Raw .superpowers/sdd/checkpoint-2-bootstrap.sql |
  docker exec -i wechatrobot-checkpoint2-mysql-1 mysql --default-character-set=utf8mb4 `
    -ucheckpoint2 -p$env:CHECKPOINT2_MYSQL_PASSWORD wechatrobot_checkpoint2

$groupBody = @{
  includeRules = @(); excludeRules = @()
  boundTagIds = @('10000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000002')
  context = @{ senderIsolated=$null; historyTurns=$null; idleTimeoutMinutes=$null; tokenCap=$null; summaryEnabled=$null; includeBotHistory=$null }
  clearContext = $false
} | ConvertTo-Json -Depth 5
$group = Invoke-RestMethod -Method Put -Uri http://127.0.0.1:5502/api/groups/30000000-0000-0000-0000-000000000001/configuration `
  -Headers $headers -ContentType application/json -Body $groupBody
$expected = @('10000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000002','10000000-0000-0000-0000-000000000004')
if (@($group.allowedTagIds | Where-Object { $_ -in $expected }).Count -ne 3 -or
    '10000000-0000-0000-0000-000000000003' -in $group.allowedTagIds) { throw 'Group tag visibility failed.' }
```

## 5. 上传、预览、批准和索引

下面的完整 PowerShell 块上传四种真实夹具，并分别传入固定 `documentId` 作为关联 ID：

| 关联 ID 末位 | 文件 | Content-Type | 标签 |
| --- | --- | --- | --- |
| `...0001` | `tests/fixtures/documents/headings.md` | `text/markdown` | 产品、售后 |
| `...0002` | `tests/fixtures/documents/utf8.txt` | `text/plain` | 售后 |
| `...0003` | `tests/fixtures/documents/text-pages.pdf` | `application/pdf` | 全局公开 |
| `...0004` | `tests/fixtures/documents/headings-table.docx` | DOCX MIME | 财务 |

```powershell
$base = 'http://127.0.0.1:5502'
$fixtures = @(
  @{ id='40000000-0000-0000-0000-000000000001'; path='tests/fixtures/documents/headings.md'; type='text/markdown'; tags=@('10000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000002') },
  @{ id='40000000-0000-0000-0000-000000000002'; path='tests/fixtures/documents/utf8.txt'; type='text/plain'; tags=@('10000000-0000-0000-0000-000000000002') },
  @{ id='40000000-0000-0000-0000-000000000003'; path='tests/fixtures/documents/text-pages.pdf'; type='application/pdf'; tags=@('10000000-0000-0000-0000-000000000004') },
  @{ id='40000000-0000-0000-0000-000000000004'; path='tests/fixtures/documents/headings-table.docx'; type='application/vnd.openxmlformats-officedocument.wordprocessingml.document'; tags=@('10000000-0000-0000-0000-000000000003') }
)
$client = [System.Net.Http.HttpClient]::new()
$client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$login.accessToken)
$uploaded = foreach ($fixture in $fixtures) {
  $form = [System.Net.Http.MultipartFormDataContent]::new()
  $file = [System.Net.Http.ByteArrayContent]::new([IO.File]::ReadAllBytes((Resolve-Path $fixture.path)))
  $file.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new($fixture.type)
  $form.Add($file,'file',[IO.Path]::GetFileName($fixture.path))
  $form.Add([System.Net.Http.StringContent]::new($fixture.id),'documentId')
  $response = $client.PostAsync("$base/api/knowledge/documents",$form).GetAwaiter().GetResult()
  if ($response.StatusCode -ne 201) { throw $response.Content.ReadAsStringAsync().Result }
  $value = $response.Content.ReadFromJsonAsync([System.Text.Json.JsonElement]).Result
  if ($value.GetProperty('state').GetString() -ne 'uploaded' -or
      -not $value.GetProperty('publicReadRiskAccepted').GetBoolean()) { throw 'Upload contract failed.' }
  [pscustomobject]@{ documentId=$fixture.id; versionId=$value.GetProperty('versionId').GetGuid(); tags=$fixture.tags }
}

$deadline = (Get-Date).AddMinutes(3)
do {
  $completed = docker exec wechatrobot-checkpoint2-mysql-1 mysql -N -B -ucheckpoint2 -p$env:CHECKPOINT2_MYSQL_PASSWORD `
    -D wechatrobot_checkpoint2 -e "SELECT COUNT(*) FROM durable_job WHERE JobType='ParseKnowledgeDocument' AND Status='completed' AND CorrelationId LIKE '40000000-%'"
  if ([int]$completed -lt 4) { Start-Sleep 2 }
} while ([int]$completed -lt 4 -and (Get-Date) -lt $deadline)
if ([int]$completed -lt 4) { throw 'Parse jobs timed out.' }

foreach ($item in $uploaded) {
  $preview = Invoke-RestMethod -Uri "$base/api/knowledge/versions/$($item.versionId)/previews" -Headers $headers
  $edited = Invoke-RestMethod -Method Put -Uri "$base/api/knowledge/versions/$($item.versionId)/previews/$($preview.items[0].id)" `
    -Headers $headers -ContentType application/json -Body (@{ text="$($preview.items[0].text) [checkpoint-2 edited]"; expectedRevision=$preview.revision } | ConvertTo-Json)
  $approved = Invoke-RestMethod -Method Post -Uri "$base/api/knowledge/versions/$($item.versionId)/previews/approve" `
    -Headers $headers -ContentType application/json -Body (@{ expectedRevision=$edited.revision } | ConvertTo-Json)
  if ($approved.Count -eq 0) { throw 'No chunks approved.' }
  Invoke-RestMethod -Method Post -Uri "$base/api/knowledge/documents/$($item.documentId)/versions/$($item.versionId)/index" `
    -Headers $headers -ContentType application/json -Body (@{ tagIds=$item.tags } | ConvertTo-Json)
}

$deadline = (Get-Date).AddMinutes(5)
do {
  $statuses = @($uploaded | ForEach-Object { Invoke-RestMethod -Uri "$base/api/knowledge/documents/$($_.documentId)/index-status?checkConsistency=true" -Headers $headers })
  $ready = @($statuses | Where-Object { $_.documentStatus -eq 'active' -and $_.consistency -eq 'consistent' -and $_.approvedChunkCount -eq $_.activePointCount }).Count -eq 4
  if (-not $ready) { Start-Sleep 5 }
} while (-not $ready -and (Get-Date) -lt $deadline)
if (-not $ready) { throw 'Index consistency timed out.' }
```

## 6. 可见性断言

从群配置响应取得允许标签集合 `{产品, 售后, 全局公开}`，下列块对四个活跃集合执行 Qdrant search。查询同时要求 `active=true`、当前 `version_id` 和标签 OR 命中：

- `active=true`；
- `version_id` 等于文档当前活跃版本；
- `tag_ids` 命中群允许集合中的任一 ID。

```powershell
$qdrantHeaders = @{ 'api-key'=$env:CHECKPOINT2_QDRANT_API_KEY }
$expectedCounts = @(2,1,2,0)
for ($index=0; $index -lt $statuses.Count; $index++) {
  $status = $statuses[$index]
  $body = @{ vector=@(1,0,0,0,0,0,0,0); limit=50; with_payload=$true; with_vector=$false; filter=@{ must=@(
    @{ key='active'; match=@{ value=$true } },
    @{ key='version_id'; match=@{ value=$status.activeVersionId } },
    @{ key='tag_ids'; match=@{ any=$group.allowedTagIds } }
  ) } } | ConvertTo-Json -Depth 8
  $result = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:36343/collections/$($status.collectionName)/points/search" `
    -Headers $qdrantHeaders -ContentType application/json -Body $body
  if ($result.result.Count -ne $expectedCounts[$index]) { throw "Visibility failed for $($uploaded[$index].documentId)." }
}
```

## 7. 2026-07-22 实测结果

- MySQL、Qdrant 均为 healthy；API `5502`、假提供商 `5591` 可用。
- 四个上传均为 HTTP 201，解析任务 `completed=4`；预览数 `2/1/2/2`，修订号均由 1 增至 2，批准分段共 7 个。
- 四个索引任务最终均 `completed`；四个文档均 `active`，一致性结果全部为 `consistent`，活跃点数 `2/1/2/2`。
- 产品文档以产品、售后两个标签重新索引成功；技术部群的 OR 规则返回产品/售后 `2/1`，全局公开 `2`，财务 `0`。
- 一条 `active=false` 测试点存在于 Qdrant，但被 `active=true` 查询排除。
- 所有提供商请求均命中 `127.0.0.1`；没有访问真实 OSS、OpenAI、OCR、WorkTool 或企业微信。

## 8. 限定清理

只停止本检查点捕获的 API、Worker 和假提供商 PID，并等待它们退出；然后只关闭本项目容器，不删除卷或镜像：

```powershell
$checkpointProcesses = @($apiProcess,$workerProcess,$fakeProvider)
foreach ($process in $checkpointProcesses) {
  $process.Refresh()
  if (-not $process.HasExited) { Stop-Process -Id $process.Id }
}
foreach ($process in $checkpointProcesses) {
  try { Wait-Process -Id $process.Id -Timeout 15 -ErrorAction Stop }
  catch {
    if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) {
      Stop-Process -Id $process.Id -Force
      Wait-Process -Id $process.Id -Timeout 15
    }
  }
}
docker compose --env-file .superpowers/sdd/checkpoint-2.env -p wechatrobot-checkpoint2 down
```

如需清除记录，必须只删除固定的 `40000000-...` 文档、`30000000-...` 群、`20000000-...` 机器人和 `10000000-...` 标签；删除 Qdrant 时只处理索引状态 API 返回的检查点集合名。不要使用 `down -v`，不要删除共享镜像或其他 Compose 项目。
