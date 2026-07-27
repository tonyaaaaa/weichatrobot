# WorkTool Callback Contract Compatibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让公网回调端点接受 WorkTool 官方验证报文，同时继续拒绝未授权、超长和未知类型输入。

**Architecture:** 消息回调使用字段级 JSON 转换器兼容 `atMe` 的布尔值和布尔字符串；指令结果 DTO 使用明确的 `203/206/207` 白名单。端点、持久化和审计流程保持不变。

**Tech Stack:** .NET 10、ASP.NET Core Minimal APIs、System.Text.Json、xUnit v3、MySQL 集成测试。

## Global Constraints

- 不启用全局宽松 JSON 转换。
- `atMe` 只接受 `true`、`false`、`"true"`、`"false"` 和 `null`。
- 指令结果 `type` 只接受 `203`、`206`、`207`。
- 保留 token、长度、群文本范围、列表数量和脱敏审计校验。
- 公网验证必须重新发布 API 后执行。

---

### Task 1: WorkTool 消息回调布尔字段兼容

**Files:**
- Create: `src/server/WechatRobot.Application/WorkTool/FlexibleNullableBooleanJsonConverter.cs`
- Modify: `src/server/WechatRobot.Application/WorkTool/WorkToolCallbackDto.cs`
- Test: `tests/server/WechatRobot.ContractTests/WorkTool/RecordedCallbackSamples.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/WorkTool/CallbackIngestionTests.cs`

**Interfaces:**
- Consumes: WorkTool 官方 `atMe` JSON 字段。
- Produces: `FlexibleNullableBooleanJsonConverter : JsonConverter<bool?>`。

- [ ] **Step 1: 写入官方字符串布尔值失败测试**

在合约测试中反序列化以下固定样本并断言 `AtMe == true`：

```json
{"spoken":"你好","rawSpoken":"@管家 你好","receivedName":"仑哥","groupName":"测试群1","groupRemark":"测试群1备注名","roomType":1,"atMe":"true","textType":1}
```

同时断言 `"yes"` 抛出 `JsonException`。

- [ ] **Step 2: 运行测试并确认失败**

Run:

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore -- --filter-class WechatRobot.ContractTests.WorkTool.RecordedCallbackSampleTests
```

Expected: 官方字符串样本因 `System.Text.Json` 无法转换为 `Nullable<Boolean>` 而失败。

- [ ] **Step 3: 实现字段级转换器**

实现 `JsonConverter<bool?>`：

```csharp
public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
    reader.TokenType switch
    {
        JsonTokenType.True => true,
        JsonTokenType.False => false,
        JsonTokenType.Null => null,
        JsonTokenType.String when bool.TryParse(reader.GetString(), out var value) => value,
        _ => throw new JsonException("Expected a boolean value.")
    };
```

在 `WorkToolCallbackDto.AtMe` 上添加
`[JsonConverter(typeof(FlexibleNullableBooleanJsonConverter))]`。

- [ ] **Step 4: 添加端点级字符串样本测试**

向已认证的回调 URL 发送 `atMe = "true"`，断言 HTTP 200、返回
`{"code":0,"message":"accepted"}`，并持久化一条消息和一个任务。

- [ ] **Step 5: 运行相关测试**

Run:

```powershell
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore -- --filter-class WechatRobot.ContractTests.WorkTool.RecordedCallbackSampleTests
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-method '*Official_string_atMe_callback_is_accepted*'
```

Expected: PASS。

### Task 2: WorkTool 实际指令类型校验

**Files:**
- Modify: `src/server/WechatRobot.Application/WorkTool/WorkToolCommandResultDto.cs`
- Test: `tests/server/WechatRobot.IntegrationTests/WorkTool/CommandResultCallbackTests.cs`

**Interfaces:**
- Consumes: WorkTool 结果报文 `type`。
- Produces: `WorkToolCommandResultDto.IsValid` 对 `203/206/207` 的明确白名单。

- [ ] **Step 1: 把结果测试改为官方实际指令类型**

发送消息结果使用 `type = 203`；创建群使用 `206`；修改群使用 `207`。
新增 `type = 1` 和 `type = 999` 返回 HTTP 400 的测试。

- [ ] **Step 2: 运行测试并确认失败**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class WechatRobot.IntegrationTests.WorkTool.CommandResultCallbackTests
```

Expected: `203/206/207` 被现有 `Type != 1` 校验拒绝。

- [ ] **Step 3: 实现实际指令白名单**

在 `WorkToolCommandResultDto` 中定义：

```csharp
private static readonly HashSet<int> SupportedCommandTypes = [203, 206, 207];
```

当 `Type` 为空或不在集合中时返回 `unsupported-result-type`。

- [ ] **Step 4: 运行结果回调测试**

Run:

```powershell
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class WechatRobot.IntegrationTests.WorkTool.CommandResultCallbackTests
```

Expected: PASS。

### Task 3: 全量相关验证与本地重启

**Files:**
- Verify: `WechatRobot.slnx`
- Verify: local API and Worker binaries

- [ ] **Step 1: 运行服务端测试和构建**

```powershell
dotnet test tests/server/WechatRobot.UnitTests/WechatRobot.UnitTests.csproj --no-restore
dotnet test tests/server/WechatRobot.ContractTests/WechatRobot.ContractTests.csproj --no-restore
dotnet test tests/server/WechatRobot.IntegrationTests/WechatRobot.IntegrationTests.csproj --no-restore -- --filter-class '*CallbackIngestionTests' --filter-class '*CommandResultCallbackTests'
dotnet build WechatRobot.slnx --no-restore
git diff --check
```

Expected: 所有测试通过，构建 0 警告、0 错误，差异检查通过。

- [ ] **Step 2: 重启 API 和 Worker**

使用 `.local/.env`，API 监听 `http://127.0.0.1:5268`，并确认
`/health/live` 返回 `{"status":"healthy"}`。

- [ ] **Step 3: 验证边界并记录发布要求**

本地以官方样本验证两个端点返回 200。明确记录：
`https://wxrobot.aavisa.com` 只有重新发布 API 后才会应用修复。
