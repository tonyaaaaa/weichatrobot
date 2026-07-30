# 私聊全知识与免配置回答降级设计

## 1. 目标

让 WorkTool 外部联系人私聊（`roomType=2`）和内部联系人私聊
（`roomType=4`）使用一致的普通问答能力：

- 所有普通私聊问答必须通过 `IAnswerAgent` 的 Agent Framework 实现执行。
- 检索全部启用且可检索的已发布知识，不受群知识标签绑定限制。
- 知识未命中时依次尝试 Web Search 和大模型自身知识。
- 不增加私聊级或群级配置，不要求为私聊建立群档案。

本设计只改变普通私聊问答。`roomType=4` 的 `#知识入库` 流程以及
`roomType=2` 禁止直接入库的规则保持不变。

## 2. 已确认现状

`PrivateChatProcessor` 已读取全部启用知识标签，但原实现直接依赖
`GroundedAnswerService`，绕过了 Worker 中注册的
`IAnswerAgent -> AnswerAgent` Agent Framework 执行入口，并且没有传入私聊
回答降级策略。
`GroundedAnswerService` 在降级策略为空时默认关闭 Web Search 和大模型知识
降级，因此当前私聊在知识未命中后直接进入无证据结果。

群聊已经通过同一服务支持以下回答顺序：

```text
知识库 -> Web Search -> 大模型自身知识 -> 最终无证据策略
```

## 3. 方案

私聊处理器依赖 `IAnswerAgent`，由 Worker 注入 `AnswerAgent`。处理器使用固定
的私聊回答降级策略，不读取群配置，也不修改统一服务的全局默认值。

固定策略为：

- `WebSearchEnabled = true`
- `ModelKnowledgeFallbackEnabled = true`
- Web Search 使用现有有界默认参数。
- Web Search 来源随回答展示，以便用户识别联网结果。
- 最终无证据策略继续使用现有安全失败文本。

不修改统一默认值，避免让其他未显式配置的群聊或调用方意外开启联网搜索。

## 4. 数据流

普通私聊消息进入现有 Durable Job 后：

1. 建立或续接按机器人、房间类型和兼容身份隔离的私聊会话。
2. 读取全部启用知识标签作为检索范围。
3. 使用现有上下文和默认聊天模型调用 `IAnswerAgent`。
4. `AnswerAgent` 使用 Agent Framework `ChatClientAgent` 执行知识回答和大模型
   自身知识回答；供应商原生 Web Search 继续使用已经验证的 typed client。
5. 知识命中时返回有依据的知识回答，不调用 Web Search。
6. 知识未命中时尝试 Web Search。
7. Web Search 只有同时取得安全答案和至少一个合法 HTTP/HTTPS 来源才成功。
8. Web Search 不支持、超时、失败、无来源或输出不安全时，继续尝试大模型自身
   知识。
9. 大模型知识仍失败时返回现有安全失败文本。
10. 通过现有发送命令、重试和死信链路发送唯一回复。

## 5. 能力与安全边界

- “免配置”指不增加私聊级、联系人级或群级开关。
- Web Search 仍只能使用默认模型配置中已经验证并声明的供应商能力；系统不能
  根据模型名称猜测能力，也不能伪造联网成功。
- 默认模型未声明 Web Search 能力时，记录稳定失败原因并继续大模型知识回答。
- 知识命中但输出安全检查失败时，不得绕过到 Web Search。
- 不记录上游原始响应、提示词、API Key、机器人凭据或秘密 URL。
- 私聊仍使用显示名称派生的兼容身份，不将其描述为稳定企业微信成员 ID。

## 6. 审计与错误处理

继续复用现有私聊检索审计，保存：

- 回答来源：`knowledge`、`web_search`、`model_knowledge` 或最终无证据来源。
- 使用的模型配置 ID。
- 知识证据或有界 Web Search 来源。
- Web Search 的稳定失败码。

搜索失败不应导致任务失败或重复回复；只有整个回答处理无法安全完成时才使用
现有失败处理边界。

## 7. 测试与验收

先写回归测试并确认在当前实现上因降级策略未启用而失败，再实现最小改动。

至少覆盖：

- `roomType=2` 私聊检索全部启用标签。
- `roomType=4` 私聊检索全部启用标签。
- 两类普通私聊都调用 `IAnswerAgent`，不能直接调用
  `GroundedAnswerService`。
- 知识命中时不调用 Web Search 或大模型知识。
- 知识未命中且 Web Search 成功时返回联网答案和合法来源。
- Web Search 不支持、失败或无来源时继续返回大模型知识答案。
- Web Search 和大模型知识均失败时返回安全失败文本。
- 私聊检索审计准确记录回答来源和搜索失败码。
- `#知识入库` 的既有房间类型边界保持不变。

完成前运行相关服务端单元和集成测试，并执行 `git diff --check`。如果缺少真实
供应商凭据，只能声明本地合同和流水线测试通过，不能宣称真实 Web Search 已
完成外部验收。
