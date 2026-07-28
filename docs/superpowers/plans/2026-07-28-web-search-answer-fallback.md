# Web Search 与模型知识降级实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在知识库未命中时，按群配置依次尝试模型原生 Web Search、模型自身知识和最终无证据策略，并让回答来源与失败原因可审计、不可伪造。

**Architecture:** 扩展通用 Chat 合同以表达可选 Web Search 参数和结构化来源，由具体 OpenAI 兼容客户端按显式 `WebSearchMode` 生成供应商请求。`GroundedAnswerService` 负责三级降级编排，只有同时取得非空答案和合法网页来源才认定为 `web_search`。群配置控制运行策略，模型配置声明能力，检索审计保存脱敏来源和稳定失败码。

**Tech Stack:** ASP.NET Core 10、EF Core/MySQL 5.7、OpenAI-compatible HTTP、xUnit 合约测试、Vue 3、Element Plus、Vitest。

## Global Constraints

- 设计依据：`docs/superpowers/specs/2026-07-28-group-lifecycle-and-context-management-design.md` 第 6、10 至 13 节。
- 本计划依赖人工转接退役和群生命周期计划。
- 当前只实现经真实供应商合同验证的 `ZaiChatCompletions`；不得凭名称推测其他
  OpenAI 兼容服务支持 Web Search。
- 不新增独立爬虫或 Web Search HTTP 服务。
- 知识库已命中但输出安全检查失败时，不得绕过到 Web Search。
- 现有群迁移默认关闭 Web Search 和模型知识降级，升级后行为不变。
- 任何上游响应体、隐藏提示词、API Key 和请求 URL 查询密钥不得进入审计。

---

### Task 1: 扩展 Chat 合同并添加供应商合约测试

**Files:**

- Modify: `src/server/WechatRobot.Application/Models/IChatCompletionClient.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Models/OpenAiCompatibleChatClient.cs`
- Modify: `tests/server/WechatRobot.ContractTests/Models/OpenAiCompatibleClientTests.cs`

- [ ] **Step 1: 写请求和响应合同失败测试**

增加：

```csharp
public sealed record WebSearchOptions(
    int ResultCount,
    string Recency,
    string? DomainFilter,
    string ContentSize,
    bool IncludeSources);

public sealed record ChatSource(string Title, Uri Url);

public sealed record ChatCompletionResponse(
    string Content,
    IReadOnlyList<ChatSource>? Sources = null);
```

合约测试必须断言 `ZaiChatCompletions` 的真实请求 JSON 字段和真实响应样例解析；
普通 Chat 请求不能携带 Web Search 字段。

- [ ] **Step 2: 运行并确认编译失败**

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --filter "FullyQualifiedName~OpenAiCompatibleClientTests"
```

Expected: FAIL，合同尚不支持 Web Search。

- [ ] **Step 3: 实现可选合同和严格解析**

`ChatCompletionRequest` 增加可空 `WebSearchOptions`。客户端仅在模型配置
`WebSearchMode == "ZaiChatCompletions"` 时序列化已验证字段。来源只接受
`https`/`http` 绝对 URL；空标题可使用主机名，非法 URL 丢弃。

- [ ] **Step 4: 覆盖失败响应**

增加 HTTP 非成功、业务响应缺字段、答案为空、来源为空、来源 URL 非法和超时
用例。客户端抛出稳定异常类型，不泄露响应正文。

- [ ] **Step 5: 运行合约测试**

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --filter "FullyQualifiedName~OpenAiCompatibleClientTests"
```

Expected: PASS。

- [ ] **Step 6: 提交**

```powershell
git add src/server/WechatRobot.Application/Models/IChatCompletionClient.cs src/server/WechatRobot.Infrastructure/Models/OpenAiCompatibleChatClient.cs tests/server/WechatRobot.ContractTests/Models/OpenAiCompatibleClientTests.cs
git commit -m "feat: add verified web search chat contract"
```

### Task 2: 增加模型 Web Search 能力声明和测试操作

**Files:**

- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/ModelConfigEntity.cs`
- Modify: `src/server/WechatRobot.Application/Models/ModelConfigurationService.cs`
- Modify: `src/server/WechatRobot.Api/Models/ModelConfigurationEndpoints.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/<timestamp>_AddModelWebSearchMode.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/WechatRobotDbContextModelSnapshot.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationEndpointTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Models/ModelConfigurationMySqlConstraintTests.cs`

- [ ] **Step 1: 写失败测试**

验证只允许：

```text
None
ZaiChatCompletions
```

Embedding 配置必须为 `None`。新增：

```text
POST /api/admin/model-configurations/{id}/test-web-search
```

测试只有取得答案和至少一个合法来源才返回成功，且不写会话或检索审计。

- [ ] **Step 2: 运行并确认失败**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~ModelConfigurationEndpointTests|FullyQualifiedName~ModelConfigurationMySqlConstraintTests"
```

- [ ] **Step 3: 增加字段和验证**

```csharp
public string WebSearchMode { get; set; } = "None";
```

更新创建、编辑、响应、配置指纹和连接测试边界。普通连接测试仍只验证普通
Chat；Web Search 使用独立操作。

- [ ] **Step 4: 生成迁移**

```powershell
dotnet ef migrations add AddModelWebSearchMode --project src/server/WechatRobot.Infrastructure --startup-project src/server/WechatRobot.Api
```

迁移将所有现有模型回填 `None`，兼容 MySQL 5.7。

- [ ] **Step 5: 运行测试**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~ModelConfiguration"
```

Expected: PASS。

- [ ] **Step 6: 提交**

```powershell
git add src/server/WechatRobot.Infrastructure/Persistence src/server/WechatRobot.Application/Models src/server/WechatRobot.Api/Models tests/server/WechatRobot.IntegrationTests/Models
git commit -m "feat: configure model web search mode"
```

### Task 3: 增加群级回答降级配置

**Files:**

- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/GroupProfileEntity.cs`
- Modify: `src/server/WechatRobot.Application/Groups/GroupConfigurationService.cs`
- Modify: `src/server/WechatRobot.Api/Groups/GroupEndpoints.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/<timestamp>_AddGroupAnswerFallback.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/WechatRobotDbContextModelSnapshot.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Groups/GroupConfigurationTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Groups/GroupConfigurationMySqlTests.cs`

- [ ] **Step 1: 写默认值和验证失败测试**

字段：

```text
WebSearchEnabled=false
ModelKnowledgeFallbackEnabled=false
WebSearchShowSources=false
WebSearchResultCount=5
WebSearchRecency=NoLimit
WebSearchDomainFilter=null
WebSearchContentSize=Medium
FinalNoEvidencePolicy=InsufficientEvidence
```

验证结果数 `1..20`、合法枚举、域名白名单格式和并发
`ExpectedConfigurationVersion`。

- [ ] **Step 2: 运行并确认失败**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~GroupConfiguration"
```

- [ ] **Step 3: 实现契约和迁移**

群配置保存不要求当前默认模型支持搜索；这是运行时降级条件。域名字段只保存
规范化主机名列表，不接受 URL 路径、凭据或通配查询参数。

- [ ] **Step 4: 运行测试**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~GroupConfiguration"
```

Expected: PASS。

- [ ] **Step 5: 提交**

```powershell
git add src/server/WechatRobot.Infrastructure/Persistence src/server/WechatRobot.Application/Groups src/server/WechatRobot.Api/Groups tests/server/WechatRobot.IntegrationTests/Groups
git commit -m "feat: add group answer fallback settings"
```

### Task 4: 实现三级回答链

**Files:**

- Modify: `src/server/WechatRobot.Application/Conversations/AnswerDecision.cs`
- Modify: `src/server/WechatRobot.Application/Conversations/GroundedAnswerService.cs`
- Modify: `src/server/WechatRobot.Application/Conversations/IGroundedConversationRepository.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs`
- Modify: `tests/server/WechatRobot.UnitTests/Conversations/GroundedAnswerTests.cs`
- Modify: `tests/server/WechatRobot.UnitTests/Conversations/AnswerOutputFirewallTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Conversations/RagReplyPipelineTests.cs`

- [ ] **Step 1: 写回答矩阵失败测试**

至少覆盖：

| 知识证据 | Web Search | 搜索结果 | 模型知识 | 结果 |
|---|---|---|---|---|
| 命中 | 任意 | 任意 | 任意 | `knowledge` |
| 未命中 | 开启且支持 | 答案+来源 | 任意 | `web_search` |
| 未命中 | 开启且支持 | 无来源 | 开启 | `model_knowledge` |
| 未命中 | 不支持/失败 | 任意 | 开启 | `model_knowledge` |
| 未命中 | 关闭 | 不调用 | 关闭 | 最终策略 |
| 命中但输出拦截 | 任意 | 不调用 | 不调用 | 安全失败 |

- [ ] **Step 2: 运行并确认失败**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~GroundedAnswerTests"
```

- [ ] **Step 3: 扩展请求、结果和编排**

`GroundedAnswerRequest` 携带群降级配置和模型 Web Search 能力。
`GroundedAnswerResult` 明确：

```csharp
string AnswerSource; // knowledge | web_search | model_knowledge | none
IReadOnlyList<ChatSource> Sources;
string? FailureCode;
```

搜索异常转换为 `web_search_unsupported`、`web_search_timeout`、
`web_search_failed` 或 `web_search_no_sources`，然后按配置继续模型知识调用。

- [ ] **Step 4: 格式化可选来源**

只有 `WebSearchShowSources=true` 且来源合法时才在群消息末尾附加有界来源列表。
来源标题和 URL 作为不可信输出经过长度限制和转义。

- [ ] **Step 5: 运行单元和流水线测试**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~Conversations"
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~RagReplyPipelineTests"
```

Expected: PASS。

- [ ] **Step 6: 提交**

```powershell
git add src/server/WechatRobot.Application/Conversations src/server/WechatRobot.Infrastructure/Conversations tests/server/WechatRobot.UnitTests/Conversations tests/server/WechatRobot.IntegrationTests/Conversations
git commit -m "feat: add grounded answer fallback chain"
```

### Task 5: 持久化并展示回答来源审计

**Files:**

- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/RetrievalAuditEntity.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/<timestamp>_AddAnswerSourceAudit.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/WechatRobotDbContextModelSnapshot.cs`
- Modify: `src/server/WechatRobot.Application/Audit/IConversationAuditQuery.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Conversations/ConversationAuditQuery.cs`
- Modify: `src/server/WechatRobot.Api/Audit/ConversationAuditEndpoints.cs`
- Modify: `src/web/wechatrobot-admin/src/api/audit.ts`
- Modify: `src/web/wechatrobot-admin/src/views/audit/ConversationAuditView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/audit/ConversationAuditView.spec.ts`
- Modify: `tests/server/WechatRobot.IntegrationTests/Conversations/ConversationAuditEndpointTests.cs`

- [ ] **Step 1: 写审计失败测试**

审计响应必须包含：

```text
answerSource
webSearchFailureCode
webSearchSources[{title,url}]
modelConfigurationId
```

不得包含完整上游请求/响应、API Key 或隐藏提示词。

- [ ] **Step 2: 运行并确认失败**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~ConversationAuditEndpointTests"
```

- [ ] **Step 3: 实现非秘密审计字段**

优先使用明确列保存 `AnswerSource` 和失败码；来源保存为有界、脱敏 JSON。
`EvidenceJson` 继续只保存业务知识证据，不混入网页来源。

- [ ] **Step 4: 更新前端**

会话审计用标签区分“知识库”“联网搜索”“模型知识”“无证据”，仅为联网搜索
展示可点击 `https/http` 来源。加载、空数据和非法来源均有安全表现。

- [ ] **Step 5: 运行验证**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~ConversationAudit"
npm test -- --run src/views/audit/ConversationAuditView.spec.ts
npm run typecheck
```

前端命令 Workdir: `src/web/wechatrobot-admin`。

- [ ] **Step 6: 提交**

```powershell
git add src/server/WechatRobot.Infrastructure/Persistence src/server/WechatRobot.Application/Audit src/server/WechatRobot.Infrastructure/Conversations src/server/WechatRobot.Api/Audit src/web/wechatrobot-admin/src/api src/web/wechatrobot-admin/src/views/audit tests
git commit -m "feat: audit answer source and web citations"
```

### Task 6: 增加模型和群配置前端

**Files:**

- Modify: `src/web/wechatrobot-admin/src/api/models.ts`
- Modify: `src/web/wechatrobot-admin/src/api/groups.ts`
- Modify: `src/web/wechatrobot-admin/src/views/models/ModelConfigurationDialog.vue`
- Modify: `src/web/wechatrobot-admin/src/views/models/ModelConfigurationDialog.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/models/ModelSettingsView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts`

- [ ] **Step 1: 写交互失败测试**

模型弹框显示 `Web Search 模式` 和独立“测试 Web Search”；Embedding 类型隐藏
并固定 `None`。群配置中 Web Search 首次打开时，前端同时建议打开模型知识
降级，但只有保存后生效。

- [ ] **Step 2: 运行并确认失败**

```powershell
npm test -- --run src/views/models/ModelConfigurationDialog.spec.ts src/views/groups/GroupRulesView.spec.ts
```

Workdir: `src/web/wechatrobot-admin`

- [ ] **Step 3: 实现 Element Plus 表单**

高级字段在 Web Search 关闭时折叠；所有控件具有标签、帮助文案、键盘访问和
响应式布局。测试结果明确区分“普通连接成功”和“Web Search 可用”。

- [ ] **Step 4: 运行前端完整验证**

```powershell
npm run typecheck
npm test -- --run
npm run build
```

Workdir: `src/web/wechatrobot-admin`

- [ ] **Step 5: 提交**

```powershell
git add src/web/wechatrobot-admin/src
git commit -m "feat: configure web search fallback in admin"
```

### Task 7: 完成跨边界验收

- [ ] **Step 1: 运行服务端测试**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj
```

- [ ] **Step 2: 运行前端测试和构建**

```powershell
npm run typecheck
npm test -- --run
npm run build
```

Workdir: `src/web/wechatrobot-admin`

- [ ] **Step 3: 使用 `.local` 做真实但非破坏性测试**

使用已配置且明确支持 `ZaiChatCompletions` 的测试模型执行独立 Web Search
测试，再在测试群验证知识命中、搜索命中、搜索失败后模型知识和最终无证据四种
路径。若没有可用供应商凭据，明确记录“外部真实验证未完成”，不得用模拟结果
宣称成功。

- [ ] **Step 4: 差异和秘密检查**

```powershell
git diff --check
rg -n "api[_-]?key|authorization|password=" src tests docs
```

逐项审查命中，确保没有新增真实秘密或上游原始响应。
