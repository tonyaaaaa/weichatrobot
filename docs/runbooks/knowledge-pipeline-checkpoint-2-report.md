# 知识库管线检查点 2 报告

日期：2026-07-22

## 范围

本检查点使用真实 ASP.NET Core API、Worker、MySQL 8.4.10 和 Qdrant 1.18.2。对象存储、Embedding 和 OCR 均为 `127.0.0.1` 上的确定性假服务，没有访问真实 OSS、OpenAI、WorkTool 或企业微信。

## 安全与数据库整改

- API 与 Worker 的回环对象存储和 HTTP 文档源客户端均禁用自动重定向；3xx 会作为失败返回。
- 回环 HTTP 只接受原始 authority 为 `localhost` 或 `127.0.0.1`，拒绝 userinfo、IPv6、缩写 IPv4、路径穿越，并且只允许在 Development 环境启用。
- 群标签、索引标签、激活、状态一致性和检索复核均使用每批最多 100 个 GUID 的参数化 OR 谓词，不再依赖 MySQL EF 的集合 JSON 参数，也没有逐 GUID 查询。
- 向量搜索每次最多返回 50 条，跨集合候选总数最多 200 条；候选 live 状态和标签按 100 条一批复核。

## 最终实测

原始证据保存在被 Git 忽略的 `.superpowers/sdd/`：

- `checkpoint-2-final-group.json`：真实 MySQL PUT 群配置响应，`技术部` 中文无乱码，`allowedTagIds` 为产品、售后、全局公开，财务不在允许集合。
- `checkpoint-2-final-status-*.json`：重启后四文档最终代次、状态、点数和一致性响应。
- `checkpoint-2-final-qdrant-search-*.json` 和 `checkpoint-2-final-qdrant-scroll-*.json`：四集合的原始 visibility 和 payload 响应。
- `checkpoint-2-final-qdrant-inactive-*.json`：`active=false` 点确实存在于 scroll、但 active search 结果仍为 2 的原始响应。
- `checkpoint-2-final-sql.txt`：相关 ID、最终代次、标签和任务状态的 MySQL 原始结果。
- `checkpoint-2-provider-final.log`：最终代次的确定性 embedding 调用，不包含外部请求。

最终 Markdown 为 g4，TXT、PDF、DOCX 均为 g3；四个文档状态均为 `active`，一致性均为 `consistent`，批准分段数与活跃点数分别为 `2/2`、`1/1`、`2/2`、`2/2`。产品/售后、售后、全局公开、仅财务四个群可见性结果分别为 `2`、`1`、`2`、`0`。

早期诊断代次中，Qdrant 在本机恢复卷后创建新集合曾耗时约 36 至 55 秒，任务首次请求超时后由持久任务机制自动重试并完成。最终重新构建、重启后的 g4/g3 代次与这些诊断失败分开：四个 reindex 均为 `AttemptCount=0`，四条 embedding 批次分别为 `2/1/2/2`，所有最终 cleanup 任务也均为 `completed`。

## 自动验证

- `dotnet build WechatRobot.slnx --no-restore`：0 warning，0 error。
- Unit：65/65 通过。
- Contract：25/25 通过。
- 群配置与检索关键 Integration：10/10 通过。
- 运行手册中的 9 个 PowerShell fenced blocks 全部通过 PowerShell AST 语法解析，0 个语法错误。

## 清理

仅停止本检查点启动的 API、Worker、假提供商进程和 Compose 项目 `wechatrobot-checkpoint2`。Compose 使用 `down` 且不带 `-v`，保留命名卷和镜像。
