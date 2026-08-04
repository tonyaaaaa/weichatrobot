# Agent Framework GLM 请求长度修复设计

## 背景

私聊回答通过 `ChatClientAgent` 和 OpenAI .NET SDK 调用官方 GLM 的
OpenAI 兼容接口。发送前，`OpenAiCompatibleRequestTuningHandler` 会为官方
GLM 请求增加 `thinking.type=disabled`，并在缺失时增加 `max_tokens`。

当前实现用新的 `ByteArrayContent` 替换请求体后，又复制了原内容的全部请求头，
其中包括根据旧请求体计算出的 `Content-Length`。新请求体字节数增加，但旧长度
仍被显式保留，导致 `SocketsHttpHandler` 在写入请求流时抛出：

`Unable to write content to request stream; content would exceed Content-Length.`

## 官方规则

- Agent Framework 的 `ChatClientAgent` 通过 `IChatClient` 调用推理服务，现有
  Agent Framework 边界保持不变。
- .NET `ByteArrayContent` 能根据其字节数组计算内容长度。
- 只有没有显式提供 `Content-Length` 时，.NET 才使用内容对象计算出的长度。
- HTTP 发送方不得发送已知与实际正文不一致的 `Content-Length`。

## 设计

修复范围限定在 `OpenAiCompatibleRequestTuning.TuneRequestAsync`：

1. 保留官方 GLM 主机和 `glm-*` 模型的现有判断。
2. 保留 `thinking.type=disabled` 和缺省 `max_tokens=2048` 的现有语义。
3. 使用修改后 JSON 的 UTF-8 字节创建新的 `ByteArrayContent`。
4. 新内容只继承原请求的 `Content-Type`。
5. 不复制或手工设置 `Content-Length`，由 `ByteArrayContent` 根据实际字节数计算。
6. 不复制可能随正文变化而失效的 `Content-MD5`、`Content-Encoding`、
   `Content-Range` 等表示元数据。

不修改 Agent Framework 调用链、模型配置、重试策略、数据库结构或知识库逻辑。

## 调用点影响检查

所有通过 `OpenAiCompatibleAgentChatClientFactory` 创建 `IChatClient`，且使用
官方 GLM 配置的调用都会经过该 Handler：

| 调用点 | 是否受影响 |
| --- | --- |
| `AnswerAgent` 非联网搜索回答 | 是 |
| `QueryRewriteAgent` 多轮问题改写 | 是 |
| `MessageIntentAgent` 群聊意图判断 | 是 |
| `TemplateRoutingAgent` 固定回复匹配 | 是 |
| `PrivateKnowledgeProposalAgent` 私聊知识提案 | 是 |
| `AgentCapabilityProbe` 模型能力探测 | 是 |
| `OpenAiCompatibleChatClient` 联网搜索和 legacy 回答 | 否；在序列化前修改字典 |
| Embedding、Qdrant 和 WorkTool 客户端 | 否；不使用该 Handler |

修复共享 Handler 后，上述所有受影响 Agent 调用同时恢复，不需要在各 Agent 内
分别处理请求头。

## 测试

先添加一个失败回归测试，通过真实回环 TCP/HTTP 连接和
`SocketsHttpHandler` 发送经过调优的请求，避免现有内存 Handler 绕过真实
`Content-Length` 写入校验。测试必须验证：

- 请求能够完整发送并收到响应；
- `Content-Length` 等于服务器实际读取到的 UTF-8 正文字节数；
- 请求体包含 `thinking.type=disabled`；
- 原有或缺省 `max_tokens` 语义保持不变；
- `Content-Type` 仍为 `application/json`；
- Authorization 处理语义不变。

完成最小修复后，运行相关单元测试、完整单元测试、契约测试、私聊集成测试、
Release 构建和 `git diff --check`。

## 发布影响

该处理器由 Worker 的 Agent Framework 请求链使用，生产修复必须更新并重启
Worker。发布包继续同时包含 API、Worker 和 Web，以保持现有交付结构。
本修复没有 EF Core 迁移，也不需要修改 MySQL 或 Qdrant 数据。
