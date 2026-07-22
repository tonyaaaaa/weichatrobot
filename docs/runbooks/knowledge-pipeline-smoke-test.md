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
docker compose --env-file .superpowers/sdd/checkpoint-2.env -p wechatrobot-checkpoint2 up -d --wait --wait-timeout 180 mysql qdrant
docker compose --env-file .superpowers/sdd/checkpoint-2.env -p wechatrobot-checkpoint2 ps
Invoke-WebRequest -UseBasicParsing http://127.0.0.1:36343/readyz -Headers @{ 'api-key' = '<local-only-qdrant-key>' }
```

## 2. 启动回环假提供商

假服务将 PUT 对象保存到忽略目录，GET 返回相同字节，并按输入文本生成固定维度向量。OCR 端点故意报错，以便文本 PDF 意外进入 OCR 时立即暴露；本检查点把文本 PDF 阈值设为 `0`，预期 OCR 调用数为零。

```powershell
$objectRoot = (Resolve-Path .superpowers/sdd).Path + '\checkpoint-2-objects'
$providerLog = (Resolve-Path .superpowers/sdd).Path + '\checkpoint-2-provider.log'
$fakeProvider = Start-Process powershell -PassThru -WindowStyle Hidden -ArgumentList @(
  '-NoProfile','-ExecutionPolicy','Bypass','-File',
  (Resolve-Path scripts/Start-FakeKnowledgeProviders.ps1).Path,
  '-ObjectRoot',$objectRoot,'-LogPath',$providerLog,'-Port','5591','-EmbeddingDimension','8'
)
Test-NetConnection 127.0.0.1 -Port 5591
```

`LoopbackObjectStorage` 只接受显式启用的 `http://localhost` 或 `http://127.0.0.1`，且 API/Worker 仅允许在 `Development` 环境注册它。生产默认仍是阿里云 OSS。

## 3. 启动 API 和 Worker

在两个独立 PowerShell 窗口设置下列临时值。主密钥和 JWT 密钥必须现场生成，不能提交；以下用占位符表示。

API：

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__WechatRobot = 'Server=127.0.0.1;Port=33326;Database=wechatrobot_checkpoint2;User=checkpoint2;Password=<local-only-user-password>'
$env:Cors__AllowedOrigins__0 = 'http://127.0.0.1:5173'
$env:Database__ApplyMigrationsOnStartup = 'true'
$env:WECHATROBOT_MASTER_KEY_BASE64 = '<base64-encoded-32-byte-local-key>'
$env:Jwt__Issuer = 'checkpoint-2'
$env:Jwt__Audience = 'checkpoint-2-api'
$env:Jwt__SigningKey = '<local-signing-key-at-least-32-characters>'
$env:BootstrapAdmin__Email = 'checkpoint2@example.test'
$env:BootstrapAdmin__Password = '<local-password-matching-identity-policy>'
$env:BootstrapAdmin__DisplayName = 'Checkpoint 2 Admin'
$env:ObjectStorage__Provider = 'loopback'
$env:LoopbackObjectStorage__BaseUrl = 'http://127.0.0.1:5591/objects/'
$env:DocumentSource__AllowLoopbackHttp = 'true'
$env:Oss__PublicReadRiskAccepted = 'true'
$env:Qdrant__BaseUrl = 'http://127.0.0.1:36343/'
$env:Qdrant__ApiKey = '<local-only-qdrant-key>'
$env:KnowledgeIndex__Dimension = '8'
$env:KnowledgeIndex__BatchSize = '2'
dotnet run --project src/server/WechatRobot.Api --urls http://127.0.0.1:5502
```

Worker：

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$env:ConnectionStrings__WechatRobot = 'Server=127.0.0.1;Port=33326;Database=wechatrobot_checkpoint2;User=checkpoint2;Password=<local-only-user-password>'
$env:WECHATROBOT_MASTER_KEY_BASE64 = '<same-local-master-key>'
$env:ObjectStorage__Provider = 'loopback'
$env:LoopbackObjectStorage__BaseUrl = 'http://127.0.0.1:5591/objects/'
$env:DocumentSource__AllowLoopbackHttp = 'true'
$env:Oss__PublicReadRiskAccepted = 'true'
$env:Qdrant__BaseUrl = 'http://127.0.0.1:36343/'
$env:Qdrant__ApiKey = '<local-only-qdrant-key>'
$env:KnowledgeIndex__Dimension = '8'
$env:KnowledgeIndex__BatchSize = '2'
$env:Ocr__BaseAddress = 'http://127.0.0.1:5591/'
$env:Ocr__MinimumExtractedTextCharacters = '0'
$env:WorkTool__BaseUrl = 'http://127.0.0.1:5591/'
$env:FixedReply__Text = 'checkpoint-2-local-only'
dotnet run --project src/server/WechatRobot.Worker
```

## 4. 初始化身份、Embedding、群和标签

登录临时管理员，并仅保存当前会话中的 Bearer Token：

```powershell
$login = Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5502/api/auth/login -ContentType 'application/json' -Body (@{
  email='checkpoint2@example.test'; password='<local-admin-password>'
} | ConvertTo-Json)
$headers = @{ Authorization = "Bearer $($login.accessToken)" }

$model = @{ provider='loopback'; configurationType='embedding'; baseUrl='http://127.0.0.1:5591'; model='checkpoint-embedding-8';
  apiKey='local-only'; timeoutSeconds=10; maxRetries=0; isEnabled=$true; isDefault=$true } | ConvertTo-Json
Invoke-RestMethod -Method Put -Uri http://127.0.0.1:5502/api/admin/model-configurations/checkpoint-embedding -Headers $headers -ContentType 'application/json' -Body $model
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5502/api/admin/model-configurations/checkpoint-embedding/test-connection -Headers $headers
```

在隔离 MySQL 中创建固定测试机器人、`技术部`、`产品`、`售后`、`财务` 和唯一的全局公开标签。为避免 PowerShell 管道编码影响中文，SQL 可使用 UTF-8 十六进制字面量。随后调用真实群配置 API，把产品和售后绑定到技术部。响应的 `allowedTagIds` 必须同时包含产品、售后和全局公开 ID，且不包含财务 ID。

## 5. 上传、预览、批准和索引

用 `HttpClient` 的 `MultipartFormDataContent` 向 `/api/knowledge/documents` 上传以下真实夹具，并分别传入固定 `documentId` 作为关联 ID：

| 关联 ID 末位 | 文件 | Content-Type | 标签 |
| --- | --- | --- | --- |
| `...0001` | `tests/fixtures/documents/headings.md` | `text/markdown` | 产品、售后 |
| `...0002` | `tests/fixtures/documents/utf8.txt` | `text/plain` | 售后 |
| `...0003` | `tests/fixtures/documents/text-pages.pdf` | `application/pdf` | 全局公开 |
| `...0004` | `tests/fixtures/documents/headings-table.docx` | DOCX MIME | 财务 |

每个上传响应必须是 201，并断言：

- `state=uploaded`，对象键位于 `wechatrobot/knowledge/<关联ID>/...`；
- 公共 URL 指向 `http://127.0.0.1:5591/objects/...`；
- `publicReadRiskAccepted=true`；
- `publicReadWarning` 明确说明文档标签不是公开 URL 的访问控制。

等待 `ParseKnowledgeDocument` 四条任务全部 `completed`。对每个 `versionId`：

1. GET `/api/knowledge/versions/{versionId}/previews`；
2. PUT `/api/knowledge/versions/{versionId}/previews/{previewId}`，使用当前 `expectedRevision` 修改首段；
3. POST `/api/knowledge/versions/{versionId}/previews/approve`，使用新修订号；
4. POST `/api/knowledge/documents/{documentId}/versions/{versionId}/index`；需要多标签回归时对首文档调用 `/reindex` 并传产品、售后两个 ID。

最后 GET `/api/knowledge/documents/{documentId}/index-status?checkConsistency=true`。四个响应都必须满足 `documentStatus=active`、`activeVersionId` 等于关联版本、批准分段数等于 Qdrant 活跃点数、`consistency=consistent`。

## 6. 可见性断言

从群配置响应取得允许标签集合 `{产品, 售后, 全局公开}`，对四个活跃集合执行带以下三个 `must` 条件的 Qdrant search：

- `active=true`；
- `version_id` 等于文档当前活跃版本；
- `tag_ids` 命中群允许集合中的任一 ID。

预期产品/售后文档、售后文档、全局公开文档分别返回 `2/1/2` 个点；仅财务标签的 DOCX 返回 `0`。另外写入一条 `active=false` 的测试点，确认同一 active search 结果不增加，从而证明未激活或部分生成的数据不可检索。

## 7. 2026-07-22 实测结果

- MySQL、Qdrant 均为 healthy；API `5502`、假提供商 `5591` 可用。
- 四个上传均为 HTTP 201，解析任务 `completed=4`；预览数 `2/1/2/2`，修订号均由 1 增至 2，批准分段共 7 个。
- 四个索引任务最终均 `completed`；四个文档均 `active`，一致性结果全部为 `consistent`，活跃点数 `2/1/2/2`。
- 产品文档以产品、售后两个标签重新索引成功；技术部群的 OR 规则返回产品/售后 `2/1`，全局公开 `2`，财务 `0`。
- 一条 `active=false` 测试点存在于 Qdrant，但被 `active=true` 查询排除。
- 所有提供商请求均命中 `127.0.0.1`；没有访问真实 OSS、OpenAI、OCR、WorkTool 或企业微信。

## 8. 限定清理

先停止本检查点启动的 API、Worker 和假提供商进程。只关闭本项目容器，不删除卷或镜像：

```powershell
Stop-Process -Id $fakeProvider.Id
docker compose --env-file .superpowers/sdd/checkpoint-2.env -p wechatrobot-checkpoint2 down
```

如需清除记录，必须只删除固定的 `40000000-...` 文档、`30000000-...` 群、`20000000-...` 机器人和 `10000000-...` 标签；删除 Qdrant 时只处理索引状态 API 返回的检查点集合名。不要使用 `down -v`，不要删除共享镜像或其他 Compose 项目。
