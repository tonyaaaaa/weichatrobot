# OpenAI 兼容接口地址规范化设计

## 背景

模型配置允许填写 OpenAI 兼容服务的基础地址。当前客户端始终追加
`v1/chat/completions` 或 `v1/embeddings`，会把已经包含版本路径的地址错误拼成
`.../v4/v1/chat/completions`。例如 Z.ai Coding 套餐地址
`https://api.z.ai/api/coding/paas/v4` 应请求
`.../api/coding/paas/v4/chat/completions`。

## 目标

- 同时支持只包含主机名、已经包含版本路径、以及完整端点三种配置形式。
- 聊天与向量客户端使用同一套规则。
- 保持现有只填主机名时自动补 `/v1` 的兼容行为。
- 连接测试失败时保存安全且更有诊断价值的 HTTP 状态摘要。

## 地址规则

设目标资源为 `chat/completions` 或 `embeddings`：

1. 基础地址已经以目标资源结尾时，直接使用，不重复追加。
2. 基础地址路径为空或仅为 `/` 时，追加 `v1/{目标资源}`。
3. 基础地址已有任意路径时，追加 `{目标资源}`。
4. 路径判断忽略大小写，统一处理首尾斜杠。

示例：

| 配置地址 | 最终聊天地址 |
| --- | --- |
| `https://api.openai.com` | `https://api.openai.com/v1/chat/completions` |
| `https://api.openai.com/v1` | `https://api.openai.com/v1/chat/completions` |
| `https://api.z.ai/api/coding/paas/v4` | `https://api.z.ai/api/coding/paas/v4/chat/completions` |
| `https://example.com/chat/completions` | `https://example.com/chat/completions` |

## 失败摘要

当 `HttpRequestException.StatusCode` 可用时，保存 `http_{状态码}`，例如
`http_401`、`http_403`、`http_404`。无法取得状态码时保留 `http_error`。
摘要不得包含响应正文、API Key、请求地址查询参数或异常消息。

## 非目标

- 不自动修改用户保存的模型名称。
- 不针对某一家供应商写死域名。
- 不探测或猜测供应商支持的模型列表。
