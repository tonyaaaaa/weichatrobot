# WechatRobot 项目级 Agent 指南设计

## 背景

仓库已经包含 API、后台 Worker、Vue 管理端、MySQL、Qdrant、WorkTool、
OCR、OSS、模型配置和知识库处理等多个组件。Agent 在修改代码前需要快速理解
项目边界、本地运行方式、外部平台限制和验证要求。

现有 `AGENTS.md` 只说明 `.local` 启动约定，不能覆盖整个项目的协作要求。

## 目标

- 为进入仓库的 Agent 提供简洁、准确的项目介绍。
- 把容易引发误操作的约束写成明确的“必须”和“禁止”规则。
- 统一本地启动、测试、运行态验证和交付方式。
- 保护用户现有修改、本地密钥、数据库数据和外部平台状态。
- 通过链接已有运行手册减少重复内容和文档漂移。

## 非目标

- 不把 `AGENTS.md` 写成完整业务需求文档。
- 不复制所有 API、数据库表或部署步骤。
- 不修改业务代码、启动脚本或 CI 配置。
- 不改变现有架构、测试框架或 Git 工作流。

## 文档定位

采用“项目介绍 + 强制规则 + 常用验证 + 深层文档链接”的混合结构。

`AGENTS.md` 是 Agent 进入仓库后的第一层执行契约。详细设计、专项验收和部署
步骤继续保存在 `docs/superpowers` 与 `docs/runbooks`。

## 内容结构

### 项目介绍

说明 WechatRobot 是面向企业微信和 WorkTool 场景的 AI 知识库群聊机器人平台，
覆盖消息接收、知识检索、模型调用、自动回复、人工接管、知识审核和管理后台。

列出经过仓库核实的主要技术栈：

- ASP.NET Core 10 API 与 Worker。
- Vue 3、TypeScript、Vite 和 Element Plus 管理端。
- MySQL 业务数据库。
- Qdrant 向量数据库。
- WorkTool 消息与指令通道。
- OCR、OSS 和 OpenAI-compatible 模型提供方。

### 目录职责

简要描述以下路径的职责：

- `src/server/WechatRobot.Domain`
- `src/server/WechatRobot.Application`
- `src/server/WechatRobot.Infrastructure`
- `src/server/WechatRobot.Api`
- `src/server/WechatRobot.Worker`
- `src/web/wechatrobot-admin`
- `tests/server`
- `tests/e2e`
- `scripts`
- `docs/runbooks`
- `docs/superpowers`

### 架构边界

- Domain 不依赖基础设施。
- Application 定义用例和接口。
- Infrastructure 实现数据库、外部服务和持久任务。
- API 负责 HTTP、认证、授权和快速接收回调。
- Worker 负责可重试的后台处理。
- 前端不得虚构后端尚未提供的列表、CRUD 或平台能力。

### 本地启动

- `.local` 是本地运行配置的唯一事实来源。
- `WECHATROBOT_ENV_FILE` 指向 `.local/.env` 的绝对路径。
- API 和 Worker 必须以 `.local` 为工作目录，以加载
  `.local/appsettings.json`。
- 默认端口为 API `5268`、前端 `5173`。
- 启动后验证 API liveness、认证 readiness、Worker 和前端。

### 配置与安全

- `.local` 永不提交。
- 不输出密钥、令牌、机器人 ID、回调 token、连接字符串或解密后的凭据。
- API key 和机器人凭据按现有加密模型持久化。
- 主密钥、JWT、数据库、OCR 和 OSS 密钥只能来自受控环境配置。
- 日志、测试输出和错误响应必须保持脱敏。

### 工作区保护

- 修改前检查 `git status` 和 `git worktree list`。
- 用户已有修改不得覆盖、回退或混入无关重构。
- 对脏工作区只修改任务直接涉及的最小文件范围。
- 未经明确要求不得执行清理、重置、提交、推送或部署。

### WorkTool 规则

- 以真实代码路径和官方接口能力为准，不从显示名推断稳定成员身份。
- 配置成功必须同时验证 HTTP 状态和 WorkTool 业务码。
- 回调查询兼容仓库已验证的成功码，但不得把未知失败静默当成成功。
- 公开回调地址不得使用 `127.0.0.1`。
- 回调 token 和机器人凭据不得进入响应或普通日志。
- 涉及成员同步、指令类型和回调字段时先核实官方能力及真实样本。

### 数据与迁移

- EF Core 实体、配置、迁移和模型快照必须保持一致。
- 不手工修改已应用迁移。
- 数据迁移或清理必须有范围检查、回滚考虑和验证证据。
- MySQL 与 Qdrant 的状态必须分别验证，不能用单一健康结果代替。

### 后端与前端开发规则

- 后端遵循现有依赖注入、异步和取消令牌模式。
- 外部调用必须使用现有 typed client、超时、限流、重试和脱敏边界。
- Vue 页面复用现有 API 模块、组件和 Element Plus 交互模式。
- 前后端契约变更必须同步类型、端点、测试和错误处理。
- 不进行与当前任务无关的格式化、重命名或视觉重做。

### 测试与验证

按改动范围选择最小但充分的验证：

- 后端：Unit、Contract、Integration。
- 前端：Vitest、typecheck、Vite build。
- 端到端：Playwright。
- 文档与代码：`git diff --check`。
- 运行态：API health、认证 readiness、Worker heartbeat、前端 HTTP 200。

修复缺陷时应先建立能够复现原始症状的测试。不能用旧二进制、旧日志或仅编译
成功代替行为验证。

### 交付规则

- 明确说明修改内容、验证命令、测试数量和仍存在的阻塞。
- 区分本次修改与工作区原有改动。
- 只有用户明确要求时才提交、推送、创建 PR 或部署。
- 不把未验证结果描述为已完成。

## 文档链接策略

`AGENTS.md` 只链接稳定入口：

- `docs/runbooks/`：本地联调、验收与部署。
- `docs/superpowers/specs/`：已批准设计。
- `docs/superpowers/plans/`：实施计划。

不链接一次性日志、临时输出或 `.local` 内容。

## 验收标准

- `AGENTS.md` 包含上述项目介绍和规则类别。
- `.local` 规则与当前真实启动方式一致。
- 技术栈、路径和验证命令均能在仓库中找到依据。
- 没有密钥、内部凭据或本地配置值。
- Markdown 层级、空行、行尾和最终换行符合规范。
- `git diff --check -- AGENTS.md` 通过。
