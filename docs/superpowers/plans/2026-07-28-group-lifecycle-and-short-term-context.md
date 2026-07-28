# 群生命周期与短期上下文实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为本地登记群增加启用、停用、归档、恢复、重新导入恢复、会话直达和短期上下文查看/清空能力，并严格区分短期上下文与长期记忆。

**Architecture:** `GroupProfileEntity` 继续是本地群身份与生命周期真相源，新增独立 `StateVersion` 并发控制和 `ArchivedAtUtc` 软归档。生命周期服务在事务内检查发送、入站和记忆任务阻塞项；消息处理与发送边界都重新检查状态。上下文查询复用现有会话、消息和摘要数据，不调用模型，不读写长期记忆。

**Tech Stack:** ASP.NET Core 10、EF Core/MySQL 5.7、Durable Job、Vue 3、Element Plus、Vitest、xUnit v3。

## Global Constraints

- 设计依据：`docs/superpowers/specs/2026-07-28-group-lifecycle-and-context-management-design.md` 第 1 至 5、7 至 13 节。
- 本计划依赖先完成 `2026-07-28-handoff-runtime-retirement.md`。
- 不物理删除群、会话、消息、摘要、审计或记忆数据。
- `StateVersion` 只管理生命周期；`ConfigurationVersion` 只管理配置。
- MySQL 5.7 不依赖强制 `CHECK` 约束、窗口函数或 JSON 表函数。
- 停用和归档不能撤回已经进入 WorkTool 外部请求的发送。
- 长期记忆入口只提供按群跳转；记忆 CRUD 由长期记忆计划实现。

---

### Task 1: 建立群生命周期数据模型和迁移

**Files:**

- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/GroupProfileEntity.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Entities/DurableJobEntity.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/WechatRobotDbContext.cs`
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Configurations/GroupProfileConfiguration.cs`（若现有映射仍内联，则修改现有位置，不重复创建）
- Create: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/<timestamp>_AddGroupLifecycle.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/Migrations/WechatRobotDbContextModelSnapshot.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Persistence/MigrationTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Persistence/PersistenceInvariantTests.cs`

- [ ] **Step 1: 写迁移和不变量失败测试**

```csharp
[Fact]
public async Task Archived_group_cannot_be_enabled()
{
    database.GroupProfiles.Add(new GroupProfileEntity
    {
        Id = Guid.NewGuid(),
        RobotConfigId = robotId,
        Name = "测试群",
        IsEnabled = true,
        ArchivedAtUtc = DateTime.UtcNow
    });

    await Assert.ThrowsAsync<InvalidOperationException>(
        () => database.SaveChangesAsync());
}
```

同时断言升级后现有群 `ArchivedAtUtc == null`、`StateVersion == 0`，并且已有
`ConfigurationVersion` 不变。为归档阻塞项增加通用的
`DurableJobEntity.GroupProfileId` 可空列和索引，现有非群任务保持 `null`。

- [ ] **Step 2: 运行并确认编译或断言失败**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~MigrationTests|FullyQualifiedName~PersistenceInvariantTests"
```

Expected: FAIL，实体尚无生命周期字段。

- [ ] **Step 3: 添加实体字段和保存前校验**

```csharp
public DateTime? ArchivedAtUtc { get; set; }
public int StateVersion { get; set; }
```

在 `WechatRobotDbContext` 保存不变量中拒绝
`ArchivedAtUtc != null && IsEnabled`。将 `StateVersion` 配置为并发令牌。
`DurableJobEntity.GroupProfileId` 只作为任务归属和运营查询字段，不替代任务
payload；后续入站和记忆任务入队时必须填写。

- [ ] **Step 4: 生成并审查迁移**

```powershell
dotnet ef migrations add AddGroupLifecycle --project src/server/WechatRobot.Infrastructure --startup-project src/server/WechatRobot.Api
```

迁移只能增加群生命周期字段、Durable Job 的可空群归属字段和必要索引；不得
修改或删除人工转接历史列、群关系、会话和审计表。

- [ ] **Step 5: 运行测试**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~MigrationTests|FullyQualifiedName~PersistenceInvariantTests"
```

Expected: PASS。

- [ ] **Step 6: 提交**

```powershell
git add src/server/WechatRobot.Infrastructure/Persistence tests/server/WechatRobot.IntegrationTests/Persistence
git commit -m "feat: add group lifecycle state"
```

### Task 2: 实现生命周期服务和 API

**Files:**

- Create: `src/server/WechatRobot.Application/Groups/GroupLifecycleService.cs`
- Create: `src/server/WechatRobot.Application/Groups/IGroupLifecycleStore.cs`
- Create: `src/server/WechatRobot.Infrastructure/Groups/EfGroupLifecycleStore.cs`
- Modify: `src/server/WechatRobot.Api/Groups/GroupEndpoints.cs`
- Modify: `src/server/WechatRobot.Api/Program.cs`
- Create: `tests/server/WechatRobot.UnitTests/Groups/GroupLifecycleServiceTests.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Groups/GroupLifecycleEndpointTests.cs`

- [ ] **Step 1: 写状态机失败测试**

覆盖：

```text
enabled -> disabled
disabled -> enabled
disabled -> archived
archived -> restored-disabled
same target -> idempotent/no version increment
stale expectedStateVersion -> 409
archive enabled group -> 409
archive with active work -> 409 + blocker counts
```

返回模型：

```csharp
public sealed record GroupLifecycleState(
    Guid Id,
    string State,
    bool IsEnabled,
    DateTime? ArchivedAtUtc,
    int StateVersion);
```

- [ ] **Step 2: 运行测试并确认失败**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~GroupLifecycleServiceTests"
```

Expected: FAIL，服务尚不存在。

- [ ] **Step 3: 实现应用服务与存储合同**

服务暴露：

```csharp
Task<GroupLifecycleResult> DisableAsync(Guid id, int expectedVersion, CancellationToken token);
Task<GroupLifecycleResult> EnableAsync(Guid id, int expectedVersion, CancellationToken token);
Task<GroupLifecycleResult> ArchiveAsync(Guid id, int expectedVersion, CancellationToken token);
Task<GroupLifecycleResult> RestoreAsync(Guid id, int expectedVersion, CancellationToken token);
```

`IGroupLifecycleStore` 以数据库侧计数检查：

- `activeSendCommands`
- `activeInboundMessages`
- `activeMemoryJobs`

状态值和原因码使用稳定常量，不能把异常字符串作为 API 合同。

- [ ] **Step 4: 添加管理 API**

```text
POST /api/groups/{id}/disable
POST /api/groups/{id}/enable
POST /api/groups/{id}/archive
POST /api/groups/{id}/restore
```

请求统一为：

```csharp
public sealed record ChangeGroupStateRequest(int ExpectedStateVersion);
```

并发或阻塞返回 `409`；不存在返回 `404`；成功返回完整生命周期状态并写统一
管理审计。

- [ ] **Step 5: 运行测试**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --filter "FullyQualifiedName~GroupLifecycleServiceTests"
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~GroupLifecycleEndpointTests"
```

Expected: PASS。

- [ ] **Step 6: 提交**

```powershell
git add src/server/WechatRobot.Application/Groups src/server/WechatRobot.Infrastructure/Groups src/server/WechatRobot.Api/Groups src/server/WechatRobot.Api/Program.cs tests/server/WechatRobot.UnitTests/Groups tests/server/WechatRobot.IntegrationTests/Groups
git commit -m "feat: add group lifecycle api"
```

### Task 3: 在消息、发送和整理边界执行状态检查

**Files:**

- Modify: `src/server/WechatRobot.Application/Messaging/InboundMessageProcessor.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs`
- Modify: `src/server/WechatRobot.Worker/Jobs/DurableJobWorker.cs`
- Modify: `src/server/WechatRobot.Worker/Jobs/RobotSendWorker.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Messaging/DurableRobotCoordinationTests.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/Conversations/InboundGroupRulePipelineTests.cs`

- [ ] **Step 1: 写停用竞态失败测试**

覆盖三个时点：

1. 入站处理前群已停用，写 `group_disabled` 终态但不进入会话序列。
2. 回答生成后、创建发送命令前群被停用，不创建发送。
3. 发送任务已租约但尚未进入 WorkTool 请求时群被停用，取消发送。

- [ ] **Step 2: 运行并确认旧行为失败**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~DurableRobotCoordinationTests|FullyQualifiedName~InboundGroupRulePipelineTests"
```

Expected: FAIL，旧任务仍继续处理或发送。

- [ ] **Step 3: 实现边界检查和受控取消**

所有状态读取必须通过持久化存储完成，不能依赖 Worker 内存缓存。
`group_disabled`/`group_archived` 是终态原因；停用时只取消尚未越过外部发送
边界的任务。入站任务入队时写入 `DurableJobEntity.GroupProfileId`；启用后不
重放已取消任务。

- [ ] **Step 4: 运行测试**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~DurableRobotCoordinationTests|FullyQualifiedName~InboundGroupRulePipelineTests"
```

Expected: PASS。

- [ ] **Step 5: 提交**

```powershell
git add src/server/WechatRobot.Application/Messaging src/server/WechatRobot.Infrastructure/Conversations src/server/WechatRobot.Infrastructure/Persistence/DurableJobRepository.cs src/server/WechatRobot.Worker/Jobs tests/server/WechatRobot.IntegrationTests
git commit -m "feat: enforce group lifecycle at runtime boundaries"
```

### Task 4: 让 WorkTool 重新导入恢复归档记录

**Files:**

- Modify: `src/server/WechatRobot.Infrastructure/WorkTool/WorkToolGroupImportService.cs`
- Modify: `src/server/WechatRobot.Api/WorkTool/WorkToolGroupOperationEndpoints.cs`
- Modify: `tests/server/WechatRobot.IntegrationTests/WorkTool/WorkToolGroupImportServiceTests.cs`
- Modify: `tests/server/WechatRobot.ContractTests/WorkTool/GroupListContractTests.cs`

- [ ] **Step 1: 写归档恢复失败测试**

```csharp
[Fact]
public async Task Importing_unique_archived_match_restores_same_disabled_record()
{
    var result = await service.ImportAsync(robotId, [Selection("测试群")], token);

    Assert.Equal(archivedId, result.Single().GroupProfileId);
    var restored = await database.GroupProfiles.FindAsync(archivedId);
    Assert.Null(restored!.ArchivedAtUtc);
    Assert.False(restored.IsEnabled);
}
```

并覆盖多个匹配时仍返回冲突、不创建新 ID。

- [ ] **Step 2: 运行并确认失败**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~WorkToolGroupImportServiceTests"
```

Expected: FAIL，现有导入只查活动记录或创建新记录。

- [ ] **Step 3: 修改导入匹配**

候选查询包含归档记录；唯一归档命中时清空 `ArchivedAtUtc`、保持
`IsEnabled=false`、递增一次 `StateVersion`、更新导入/最后发现时间并写审计。
新导入群沿用现有默认启用行为，除非规格后续明确修改。

- [ ] **Step 4: 运行集成与合约测试**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~WorkToolGroupImportServiceTests"
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --filter "FullyQualifiedName~GroupListContractTests"
```

Expected: PASS。

- [ ] **Step 5: 提交**

```powershell
git add src/server/WechatRobot.Infrastructure/WorkTool/WorkToolGroupImportService.cs src/server/WechatRobot.Api/WorkTool/WorkToolGroupOperationEndpoints.cs tests/server/WechatRobot.IntegrationTests/WorkTool tests/server/WechatRobot.ContractTests/WorkTool
git commit -m "feat: restore archived groups on import"
```

### Task 5: 添加短期上下文查询和清空 API

**Files:**

- Modify: `src/server/WechatRobot.Application/Conversations/IGroundedConversationRepository.cs`
- Create: `src/server/WechatRobot.Application/Conversations/ConversationContextQueryService.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs`
- Modify: `src/server/WechatRobot.Api/Groups/GroupEndpoints.cs`
- Create: `tests/server/WechatRobot.IntegrationTests/Groups/GroupConversationContextEndpointTests.cs`

- [ ] **Step 1: 写查询合同失败测试**

目标路由：

```text
GET  /api/groups/{id}/conversation-context
POST /api/groups/{id}/conversation-context/clear
```

查询响应包含会话 ID、发送人显示名、作用域键脱敏值、摘要、清空位置、最后
活动时间、实际上下文消息预览和分页信息。不得包含完整凭据、WorkTool 机器人
标识或长期记忆内容。

- [ ] **Step 2: 运行并确认 404**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~GroupConversationContextEndpointTests"
```

Expected: FAIL，路由不存在。

- [ ] **Step 3: 实现数据库侧分页查询**

复用 `ConversationContextService` 的轮数、空闲时间、清空水位和 token 上限
规则，生成“当前实际会被送入模型的上下文预览”。不调用 Chat 模型，不生成新
摘要。清空请求必须携带当前会话版本或群配置版本，并写管理审计。

- [ ] **Step 4: 运行测试**

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --filter "FullyQualifiedName~GroupConversationContextEndpointTests|FullyQualifiedName~ConversationSessionConcurrencyTests"
```

Expected: PASS。

- [ ] **Step 5: 提交**

```powershell
git add src/server/WechatRobot.Application/Conversations src/server/WechatRobot.Infrastructure/Conversations src/server/WechatRobot.Api/Groups tests/server/WechatRobot.IntegrationTests
git commit -m "feat: expose group conversation context"
```

### Task 6: 重构群管理前端

**Files:**

- Modify: `src/web/wechatrobot-admin/src/api/groups.ts`
- Modify: `src/web/wechatrobot-admin/src/api/worktool.ts`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupListView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupListView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts`
- Create: `src/web/wechatrobot-admin/src/views/groups/GroupContextView.vue`
- Create: `src/web/wechatrobot-admin/src/views/groups/GroupContextView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/router/index.ts`
- Modify: `src/web/wechatrobot-admin/src/router/index.spec.ts`

- [ ] **Step 1: 写页面行为失败测试**

群列表必须显示明确状态和操作：

```text
启用：停用、配置、会话、上下文、记忆
停用：启用、归档、配置、会话、上下文、记忆
归档：恢复、会话、上下文、记忆
```

归档默认隐藏，可通过筛选展示。所有危险操作使用共享
`ElMessageBox.confirm`，并展示 WorkTool 已发送请求无法撤回的边界。

- [ ] **Step 2: 运行并确认失败**

```powershell
npm test -- --run src/views/groups/GroupListView.spec.ts src/views/groups/GroupRulesView.spec.ts src/router/index.spec.ts
```

Workdir: `src/web/wechatrobot-admin`

Expected: FAIL，现有页面只有登记/配置流程。

- [ ] **Step 3: 实现 API 类型和页面**

增加 `GroupLifecycleState`、`GroupContextPage` 和状态变更 API。群列表使用
Element Plus 表格、标签、筛选和下拉操作；准确 WorkTool 导入群默认折叠高级
匹配规则。会话按钮跳转：

```text
/audit/conversations?groupId={id}
```

记忆按钮预留到长期记忆计划定义的：

```text
/memory?groupId={id}
```

- [ ] **Step 4: 完成上下文页**

按会话展示摘要、实际有效上下文、清空位置和最后活动时间；清空操作不出现
“删除记忆”措辞，并明确不会影响长期记忆。

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
git commit -m "feat: add group lifecycle and context ui"
```

### Task 7: 完成运行验收

**Files:**

- Modify: `docs/runbooks/local-development.md`
- Modify: `docs/runbooks/iis-test-deployment-wxrobot.aavisa.com.md`

- [ ] **Step 1: 运行服务端完整测试**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj
```

- [ ] **Step 2: 运行前端完整测试**

```powershell
npm run typecheck
npm test -- --run
npm run build
```

Workdir: `src/web/wechatrobot-admin`

- [ ] **Step 3: 使用 `.local` 验收**

依次验证：停用阻止新回复、启用不补发、归档有任务时被阻止、归档后重新导入
恢复同一 ID 且保持停用、会话直达筛选正确、清空上下文不删除长期记忆。

- [ ] **Step 4: 检查差异**

```powershell
git diff --check
```

- [ ] **Step 5: 提交运行文档**

```powershell
git add docs/runbooks
git commit -m "docs: document group lifecycle operations"
```
