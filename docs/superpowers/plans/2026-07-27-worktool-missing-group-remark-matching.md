# WorkTool Missing Group Remark Matching Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让缺少 `groupRemark` 的真实 WorkTool 回调在群名称唯一时可靠匹配，同时保留同名群歧义保护。

**Architecture:** 在 `GroundedConversationRepository` 内分层选择名称候选：备注精确项优先、无备注配置兜底、回调无备注时按唯一名称确认。规则候选复用同一备注兼容条件，后续规则计算保持不变。

**Tech Stack:** ASP.NET Core 10、EF Core、MySQL、xUnit v3/Microsoft Testing Platform。

## Global Constraints

- 不修改数据库中的现有群资料。
- 不使用 `ExternalGroupId` 作为回调身份。
- 同名候选无法消歧时必须拒绝。
- 先验证测试失败，再修改生产代码。

---

### Task 1: 修复群身份候选解析

**Files:**
- Modify: `tests/server/WechatRobot.IntegrationTests/Conversations/InboundGroupRulePipelineTests.cs`
- Modify: `src/server/WechatRobot.Infrastructure/Conversations/GroundedConversationRepository.cs`

**Interfaces:**
- Consumes: `EvaluateInboundPolicyAsync(Guid messageId, string groupName, string? groupRemark, bool wasMentioned, CancellationToken token)`
- Produces: 不改变公开接口，仅调整群候选选择语义。

- [x] **Step 1: 写失败测试**

增加以下真实行为用例：

```csharp
[Fact]
public async Task Unique_visible_name_matches_when_callback_omits_configured_remark()
```

并断言决策为 `Proceed`、`GroupProfileId` 为该唯一群。

- [x] **Step 2: 验证测试因现有备注过滤失败**

运行：

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -c Release -- --filter-class "WechatRobot.IntegrationTests.Conversations.InboundGroupRulePipelineTests"
```

预期新增用例失败，实际结果为 `group_rule_unmatched`。

- [x] **Step 3: 实现最小候选优先级**

按以下优先级查询名称候选：

```csharp
if (string.IsNullOrWhiteSpace(groupRemark))
    exactNames = await namedGroups.Take(2).ToArrayAsync(token);
else
    exactNames = await namedGroups
        .Where(item => item.WorkToolGroupRemark == groupRemark)
        .Take(2)
        .ToArrayAsync(token);
```

回调备注非空但没有精确项时，再查询本地备注为空的兜底项。规则候选仅在回调提供备注时应用备注兼容过滤。

- [x] **Step 4: 验证聚焦测试和相关项目**

运行：

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj -c Release --no-restore -- --filter-class "WechatRobot.IntegrationTests.Conversations.InboundGroupRulePipelineTests"
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj -c Release --no-restore
git diff --check
```

预期全部通过且无差异格式错误。
