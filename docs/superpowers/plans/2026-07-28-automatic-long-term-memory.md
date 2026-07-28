# 自动长期记忆实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 自动从群聊中整理用户偏好、群规则、机器人经验和业务事实，形成可审计的候选与长期记忆；行为记忆可按规则自动晋升，业务事实必须经管理员批准后进入知识库 Qdrant。

**Architecture:** MySQL 是候选、观察、正式记忆、版本和审计的权威来源。Worker 通过 Durable Job 异步提取、合并、晋升和索引；独立 Qdrant 记忆集合只保存检索元数据，召回后回 MySQL 校验。回答链在业务知识检索前召回少量行为记忆，但在提示词中与业务事实证据隔离。业务事实路由到现有知识候选审核/发布链。

**Tech Stack:** ASP.NET Core 10、EF Core/MySQL 5.7、Durable Job、Qdrant、OpenAI-compatible Chat/Embedding、Vue 3、Element Plus、xUnit v3、Vitest。

## Global Constraints

- 设计依据：`docs/superpowers/specs/2026-07-28-automatic-long-term-memory-design.md`。
- 依赖顺序：人工转接退役 → 群生命周期/上下文 → Web Search 降级 → 本计划。
- 第一阶段用户作用域使用 `RobotConfigId + GroupProfileId + normalized receivedName`；
  接受已批准的昵称漂移和同名风险，但 UI 不得宣称这是稳定企业微信身份。
- 不新增 `memory` 模型类型；整理使用当前启用的默认 Chat 模型。
- 行为记忆不是业务事实证据；`BusinessFact` 永不自动发布。
- 记忆故障不得阻塞 WorkTool 回调、业务知识检索或正常回复。
- 不保存完整模型输入/输出、密钥、验证码、连接字符串或原始上游响应。
- 所有迁移兼容 MySQL 5.7，并保留历史人工转接表和数据。

---

### Task 1: 建立记忆领域模型和规则

**Files:**

- Create: `src/server/WechatRobot.Domain/Memory/MemoryScope.cs`
- Create: `src/server/WechatRobot.Domain/Memory/MemoryCandidate.cs`
- Create: `src/server/WechatRobot.Domain/Memory/MemoryEntry.cs`
- Create: `src/server/WechatRobot.Domain/Memory/MemoryPromotionPolicy.cs`
- Create: `tests/server/WechatRobot.UnitTests/Memory/MemoryScopeTests.cs`
- Create: `tests/server/WechatRobot.UnitTests/Memory/MemoryPromotionPolicyTests.cs`

- [ ] **Step 1: 写作用域和晋升失败测试**

覆盖：

```text
Global: 无 robot/group/subject
Robot: 必须有 robot
Group: 必须有 robot + group
User: 必须有 robot + group + normalized subject
普通行为记忆: 3 个会话 + 2 个自然日 + confidence >= 0.80
明确行为记忆: confidence >= 0.80 可直接晋升
BusinessFact: 永不直接晋升
未解决冲突: 不晋升
```

- [ ] **Step 2: 运行并确认编译失败**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~Memory"
```

- [ ] **Step 3: 实现纯领域规则**

使用明确枚举：

```csharp
public enum MemoryScopeType { Global, Robot, Group, User }
public enum MemoryType { UserPreference, GroupRule, RobotExperience, BusinessFact }
public enum MemoryCandidateStatus { Pending, Accumulating, Promoted, RoutedToKnowledge, Rejected, Expired }
public enum MemoryEntryStatus { Active, Superseded, Forgotten, Expired }
```

昵称规范化只做 Unicode 稳定化、首尾空白和大小写处理，不猜测真实身份。

- [ ] **Step 4: 运行测试**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~Memory"
```

Expected: PASS。

- [ ] **Step 5: 提交**

```powershell
git add src/server/WechatRobot.Domain/Memory tests/server/WechatRobot.UnitTests/Memory
git commit -m "feat: define long term memory domain rules"
```

### Task 2: 增加 MySQL 记忆模型和知识候选解耦迁移

**Files:**

- Create: `src/server/WechatRobot.Infrastructure/Persistence/Entities/MemoryCandidateEntity.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Entities/MemoryObservationEntity.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Entities/MemoryEntryEntity.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Entities/MemoryAuditEntity.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Configurations/MemoryConfigurations.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/KnowledgeCandidateEntity.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/WechatRobotDbContext.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/<timestamp>_AddAutomaticLongTermMemory.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/WechatRobotDbContextModelSnapshot.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Memory/MemoryMigrationTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Knowledge/HumanAnswerReviewTests.cs`

- [ ] **Step 1: 写迁移失败测试**

断言四张记忆表、唯一索引和外键存在；`KnowledgeCandidate.HandoffCaseId` 可空，
并新增：

```text
SourceType
SourceConversationMessageId
SourceMemoryCandidateId
```

历史候选回填 `HistoricalHandoff`，且历史转接表行数不变。

- [ ] **Step 2: 运行并确认失败**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~MemoryMigrationTests|FullyQualifiedName~HumanAnswerReviewTests"
```

- [ ] **Step 3: 添加实体、映射和 DbSet**

按规格完整实现字段、长度、版本、状态和作用域索引。至少增加唯一约束：

```text
memory_observation(MemoryCandidateId, ConversationMessageId)
knowledge_candidate(SourceMemoryCandidateId) where source is not null
```

若 MySQL 5.7 不支持所需过滤唯一索引，使用可空唯一列语义或生成的幂等键列，
并用集成测试证明行为。

- [ ] **Step 4: 生成并审查迁移**

```powershell
dotnet ef migrations add AddAutomaticLongTermMemory --project src/server/WechatRobot.Infrastructure --startup-project src/server/WechatRobot.Api
```

迁移必须是非破坏性的；历史回填使用数据库侧有界语句，不加载全表到内存。

- [ ] **Step 5: 在 MySQL 5.7 运行测试**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~MemoryMigrationTests|FullyQualifiedName~HumanAnswerReviewTests"
```

Expected: PASS。

- [ ] **Step 6: 提交**

```powershell
git add src/server/WechatRobot.Infrastructure/Persistence tests/server/WechatRobot.IntegrationTests/Memory tests/server/WechatRobot.IntegrationTests/Knowledge/HumanAnswerReviewTests.cs
git commit -m "feat: persist long term memory records"
```

### Task 3: 实现受约束的自动提取和验证

**Files:**

- Create: `src/server/WechatRobot.Application/Memory/MemoryExtractionContracts.cs`
- Create: `src/server/WechatRobot.Application/Memory/MemoryExtractionValidator.cs`
- Create: `src/server/WechatRobot.Application/Memory/MemoryExtractionService.cs`
- Create: `src/server/WechatRobot.Infrastructure/Memory/ChatMemoryExtractor.cs`
- Create: `tests/server/WechatRobot.UnitTests/Memory/MemoryExtractionValidatorTests.cs`
- Create: `tests/server/WechatRobot.ContractTests/Models/MemoryExtractionContractTests.cs`

- [ ] **Step 1: 写验证失败测试**

覆盖合法 JSON、未知枚举、越界可信度、过长内容、来源消息不属于窗口、重复来源、
密码/API Key/验证码/连接字符串、提示词注入内容和空候选。

- [ ] **Step 2: 运行并确认失败**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~MemoryExtraction"
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --filter "FullyQualifiedName~MemoryExtraction"
```

- [ ] **Step 3: 实现固定提示词和 JSON 合同**

模型输入仅包含整理窗口、作用域元数据和少量已有摘要，所有数据置于明确的
不可信边界。模型输出只接受：

```json
{
  "memories": [
    {
      "type": "UserPreference",
      "content": "偏好结论优先",
      "confidence": 0.9,
      "explicit": true,
      "sourceMessageIds": ["00000000-0000-0000-0000-000000000000"]
    }
  ]
}
```

- [ ] **Step 4: 增加稳定失败码**

至少包括：

```text
memory_model_unavailable
memory_invalid_json
memory_invalid_source
memory_secret_detected
memory_content_invalid
```

不保存完整模型响应。

- [ ] **Step 5: 运行测试**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~MemoryExtraction"
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --filter "FullyQualifiedName~MemoryExtraction"
```

Expected: PASS。

- [ ] **Step 6: 提交**

```powershell
git add src/server/WechatRobot.Application/Memory src/server/WechatRobot.Infrastructure/Memory tests/server/WechatRobot.UnitTests/Memory tests/server/WechatRobot.ContractTests/Models
git commit -m "feat: extract validated memory candidates"
```

### Task 4: 实现候选合并、观察幂等和自动晋升

**Files:**

- Create: `src/server/WechatRobot.Application/Memory/IMemoryStore.cs`
- Create: `src/server/WechatRobot.Application/Memory/MemoryOrganizationService.cs`
- Create: `src/server/WechatRobot.Infrastructure/Memory/EfMemoryStore.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Memory/MemoryOrganizationTests.cs`

- [ ] **Step 1: 写并发和幂等失败测试**

覆盖：

```text
同一消息重试只产生一条 observation
同一会话重复表达只计一次 session
跨自然日按 UTC 日期计数
并发合并只产生一个 candidate
并发晋升只产生一个 active entry
冲突候选保持 accumulating
明确偏好替代旧偏好并写 audit
BusinessFact 只创建一个 KnowledgeCandidate
```

- [ ] **Step 2: 运行并确认失败**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~MemoryOrganizationTests"
```

- [ ] **Step 3: 实现有界候选检索和事务更新**

先按作用域、类型和规范化键做数据库筛选，再用指纹/Embedding/受约束 Chat
判断缩小后的候选。模型只返回 `same|related|conflict|unrelated`，最终状态由
服务端规则决定。

- [ ] **Step 4: 路由业务事实**

创建：

```csharp
KnowledgeCandidateEntity
{
    HandoffCaseId = null,
    SourceType = "MemoryExtraction",
    SourceMemoryCandidateId = candidate.Id
}
```

未批准前不创建 `PublishKnowledgeCandidate` 任务。

- [ ] **Step 5: 运行测试**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~MemoryOrganizationTests|FullyQualifiedName~HumanAnswerReviewTests"
```

Expected: PASS。

- [ ] **Step 6: 提交**

```powershell
git add src/server/WechatRobot.Application/Memory src/server/WechatRobot.Infrastructure/Memory tests/server/WechatRobot.IntegrationTests/Memory tests/server/WechatRobot.IntegrationTests/Knowledge
git commit -m "feat: organize and promote memory candidates"
```

### Task 5: 接入 Durable Job 和周期整理

**Files:**

- Modify: `src/server/WechatRobot.Application/Jobs/IDurableJobRepository.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs`
- Create: `src/server/WechatRobot.Worker/Jobs/MemoryExtractionWorker.cs`
- Create: `src/server/WechatRobot.Worker/Jobs/MemoryMaintenanceWorker.cs`
- Modify: `src/server/WechatRobot.Worker/Program.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Memory/MemoryDurableJobTests.cs`

- [ ] **Step 1: 写任务失败测试**

覆盖：

```text
明确“记住”创建高优先级 ExtractMemory
会话空闲 30 分钟只整理未处理窗口
重复调度使用同一幂等键
默认 Chat 模型不可用进入 retrying
超过次数进入 dead-letter
群停用取消未租约任务
群归档阻止新任务
租约任务写入前复查群状态
每日维护执行合并、晋升和过期
```

- [ ] **Step 2: 运行并确认失败**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~MemoryDurableJobTests"
```

- [ ] **Step 3: 实现任务类型和处理器**

使用现有 `DurableJobEntity`：

```text
ExtractConversationMemory
MaintainLongTermMemory
IndexMemoryEntry
RemoveMemoryEntryFromIndex
```

payload 只保存实体 ID、窗口水位、模型配置 ID/版本和幂等信息，不保存完整消息
正文或模型请求。所有群级记忆任务同时填写既有
`DurableJobEntity.GroupProfileId`，供群停用和归档进行数据库侧阻塞检查。

- [ ] **Step 4: 注册 Worker**

注册独立 BackgroundService，保持快速轮询和取消令牌传播。任务处理失败使用稳定
原因码；不得阻塞 `DurableJobWorker` 的入站处理。

- [ ] **Step 5: 运行测试**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~MemoryDurableJobTests|FullyQualifiedName~DurableRobotCoordinationTests"
```

Expected: PASS。

- [ ] **Step 6: 提交**

```powershell
git add src/server/WechatRobot.Application/Jobs src/server/WechatRobot.Infrastructure/Persistence src/server/WechatRobot.Infrastructure/Conversations src/server/WechatRobot.Worker tests/server/WechatRobot.IntegrationTests/Memory
git commit -m "feat: process memory through durable jobs"
```

### Task 6: 建立独立记忆向量集合和召回

**Files:**

- Create: `src/server/WechatRobot.Application/Memory/IMemoryVectorIndex.cs`
- Create: `src/server/WechatRobot.Application/Memory/MemoryRecallService.cs`
- Create: `src/server/WechatRobot.Infrastructure/Memory/QdrantMemoryVectorIndex.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Knowledge/QdrantVectorStore.cs`
- Modify: `src/server/WechatRobot.Api/Program.cs`
- Modify: `src/server/WechatRobot.Worker/Program.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Memory/QdrantMemoryTests.cs`
- Create: `tests/server/WechatRobot.UnitTests/Memory/MemoryRecallServiceTests.cs`

- [ ] **Step 1: 写集合隔离和召回失败测试**

断言：

```text
记忆 collection 名称不以业务知识 collection 完全相同
payload 只含 memory id/scope/type/statusVersion/generation
作用域过滤 User -> Group -> Robot -> Global
命中后回 MySQL 校验 active/version/expiry
forgotten/superseded/expired 不返回
最多 5 条并受总字符上限约束
维度变化创建新 generation
```

- [ ] **Step 2: 运行并确认失败**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~MemoryRecall"
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~QdrantMemoryTests"
```

- [ ] **Step 3: 实现独立索引适配器**

复用 `IVectorStore` 的安全 HTTP 边界，但 collection 命名、代次和 payload
由 `QdrantMemoryVectorIndex` 独立管理。建议命名：

```text
memory_{distance}_{dimension}_g{generation}
```

- [ ] **Step 4: 实现召回降级**

Qdrant 或 Embedding 失败时返回空记忆和稳定失败码，不抛到正常回答链。召回
结果只能来自 MySQL 当前有效行。

- [ ] **Step 5: 运行测试**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~MemoryRecall"
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~QdrantMemoryTests"
```

Expected: PASS。

- [ ] **Step 6: 提交**

```powershell
git add src/server/WechatRobot.Application/Memory src/server/WechatRobot.Infrastructure/Memory src/server/WechatRobot.Infrastructure/Knowledge/QdrantVectorStore.cs src/server/WechatRobot.Api/Program.cs src/server/WechatRobot.Worker/Program.cs tests/server/WechatRobot.UnitTests/Memory tests/server/WechatRobot.IntegrationTests/Memory
git commit -m "feat: index and recall behavioral memory"
```

### Task 7: 将行为记忆安全注入回答链并审计

**Files:**

- Modify: `src/server/WechatRobot.Application/Conversations/AnswerDecision.cs`
- Modify: `src/server/WechatRobot.Application/Conversations/GroundedAnswerService.cs`
- Modify: `src/server/WechatRobot.Application/Messaging/InboundMessageProcessor.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/RetrievalAuditEntity.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/<timestamp>_AddMemoryRecallAudit.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/WechatRobotDbContextModelSnapshot.cs`
- Modify: `tests/server/WechatRobot.UnitTests/Conversations/GroundedAnswerTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Conversations/RagReplyPipelineTests.cs`

- [ ] **Step 1: 写提示词隔离失败测试**

模型消息必须存在两个清晰分区：

```text
UNTRUSTED_BEHAVIOR_MEMORY
UNTRUSTED_BUSINESS_EVIDENCE
```

并明确行为记忆只能影响表达和规则，不能作为业务事实。测试恶意记忆文本不会
改变系统指令，也不会进入业务证据审计。

- [ ] **Step 2: 运行并确认失败**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~GroundedAnswerTests"
```

- [ ] **Step 3: 集成非阻塞召回**

前置安全策略通过后、知识检索前召回。召回失败继续回答；成功提交发送后异步
增加 `RecallCount` 和 `LastRecalledAtUtc`。失败、取消或未提交不计数。

- [ ] **Step 4: 添加 `MemoryRecallJson`**

审计只保存记忆 ID、作用域、类型、版本、召回分数和失败码，不保存完整记忆
内容。迁移默认值为 `[]` 或空对象，兼容现有行。

- [ ] **Step 5: 运行测试**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~Conversations"
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~RagReplyPipelineTests|FullyQualifiedName~ConversationAudit"
```

Expected: PASS。

- [ ] **Step 6: 提交**

```powershell
git add src/server/WechatRobot.Application/Conversations src/server/WechatRobot.Application/Messaging src/server/WechatRobot.Infrastructure/Persistence src/server/WechatRobot.Infrastructure/Conversations tests/server/WechatRobot.UnitTests/Conversations tests/server/WechatRobot.IntegrationTests/Conversations
git commit -m "feat: recall memory during grounded answers"
```

### Task 8: 增加记忆管理 API 和运营查询

**Files:**

- Create: `src/server/WechatRobot.Api/Memory/MemoryEndpoints.cs`
- Create: `src/server/WechatRobot.Application/Memory/MemoryAdministrationService.cs`
- Create: `src/server/WechatRobot.Infrastructure/Memory/MemoryAdministrationQuery.cs`
- Modify: `src/server/WechatRobot.Api/Knowledge/KnowledgeReviewEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Audit/ConversationAuditEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Program.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Memory/MemoryEndpointTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Knowledge/HumanAnswerReviewTests.cs`

- [ ] **Step 1: 写 API 失败测试**

实现规格中的候选、正式记忆和任务路由。验证分页上限、筛选、权限、`409`
并发、幂等键、管理审计和秘密脱敏。

- [ ] **Step 2: 运行并确认 404**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~MemoryEndpointTests"
```

- [ ] **Step 3: 实现候选和正式记忆操作**

管理员可编辑、晋升、拒绝、重整、忘记和恢复。`BusinessFact` 的 promote 请求
必须被拒绝并引导到知识审核；所有变更使用 `ExpectedVersion`。

- [ ] **Step 4: 扩展知识审核与会话审计**

知识候选响应显示 `HistoricalHandoff|MemoryExtraction|ManualCorrection`。
增加从会话审计创建 `ManualCorrection` 候选的操作，批准时仍要求至少一个启用
知识标签。

- [ ] **Step 5: 运行测试**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~MemoryEndpointTests|FullyQualifiedName~HumanAnswerReviewTests|FullyQualifiedName~ConversationAuditEndpointTests"
```

Expected: PASS。

- [ ] **Step 6: 提交**

```powershell
git add src/server/WechatRobot.Api/Memory src/server/WechatRobot.Application/Memory src/server/WechatRobot.Infrastructure/Memory src/server/WechatRobot.Api/Knowledge src/server/WechatRobot.Api/Audit src/server/WechatRobot.Api/Program.cs tests/server/WechatRobot.IntegrationTests
git commit -m "feat: add memory administration api"
```

### Task 9: 增加记忆中心和知识学习审核前端

**Files:**

- Create: `src/web/wechatrobot-admin/src/api/memory.ts`
- Create: `src/web/wechatrobot-admin/src/api/memory.spec.ts`
- Create: `src/web/wechatrobot-admin/src/views/memory/MemoryCenterView.vue`
- Create: `src/web/wechatrobot-admin/src/views/memory/MemoryCenterView.spec.ts`
- Create: `src/web/wechatrobot-admin/src/views/memory/MemoryCandidatesPanel.vue`
- Create: `src/web/wechatrobot-admin/src/views/memory/MemoryEntriesPanel.vue`
- Create: `src/web/wechatrobot-admin/src/views/memory/MemoryJobsPanel.vue`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeReviewView.vue`
- Create: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeReviewView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/audit/ConversationAuditView.vue`
- Modify: `src/web/wechatrobot-admin/src/router/index.ts`
- Modify: `src/web/wechatrobot-admin/src/router/index.spec.ts`

- [ ] **Step 1: 写前端失败测试**

验证一级菜单“记忆中心”、三个页签、群筛选查询参数、加载/空/失败状态、
`ElMessageBox` 确认、`409` 刷新提示和权限矩阵。

- [ ] **Step 2: 运行并确认失败**

```powershell
npm test -- --run src/router/index.spec.ts src/views/knowledge/KnowledgeReviewView.spec.ts src/views/audit/ConversationAuditView.spec.ts
```

Workdir: `src/web/wechatrobot-admin`

- [ ] **Step 3: 实现 API 和页面**

“待整理”显示可信度、证据次数、不同会话/日期数和作用域；“长期记忆”显示
状态、有效期和替代链；“整理任务”显示状态、次数、稳定失败码和受控重试。

- [ ] **Step 4: 更新知识与会话页面**

“知识审核”改为“知识学习审核”，显示来源类型。会话审计增加“创建知识候选”，
但不显示完整模型输入或隐藏提示词。

- [ ] **Step 5: 运行前端验证**

```powershell
npm run typecheck
npm test -- --run
npm run build
```

Workdir: `src/web/wechatrobot-admin`

Expected: PASS。

- [ ] **Step 6: 提交**

```powershell
git add src/web/wechatrobot-admin/src
git commit -m "feat: add memory administration ui"
```

### Task 10: 完成全链路验收

**Files:**

- Modify: `docs/runbooks/local-development.md`
- Create: `docs/runbooks/long-term-memory-operations.md`

- [ ] **Step 1: 运行服务端完整验证**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj
```

- [ ] **Step 2: 运行前端完整验证**

```powershell
npm run typecheck
npm test -- --run
npm run build
```

Workdir: `src/web/wechatrobot-admin`

- [ ] **Step 3: 使用 `.local` 做场景验收**

依次验证：明确偏好直接候选、跨会话普通晋升、业务事实进入知识审核但不自动
索引、冲突替代、忘记、恢复、群停用取消整理、Qdrant 记忆故障不影响知识问答、
Worker 重启继续任务且不重复。

- [ ] **Step 4: 验证数据边界**

只读确认 MySQL 是权威状态、记忆和知识 Qdrant collection 相互隔离、历史人工
转接表仍存在、`KnowledgeCandidate.HandoffCaseId` 历史关联仍可读。

- [ ] **Step 5: 检查差异和秘密**

```powershell
git diff --check
rg -n "password=|AccessKeySecret|SigningKey=|Qdrant__ApiKey=|Bearer " src tests docs
```

- [ ] **Step 6: 提交运行手册**

```powershell
git add docs/runbooks
git commit -m "docs: add long term memory operations"
```
