# WorkTool type=512 私有证据采集

此工具只验证 WorkTool `type=512` 的真实返回结构，不会把未验证字段接入生产功能。

## 使用条件

- 只选择专用测试群，并先获得群成员对本次诊断的同意。
- 只在可信的 Windows 开发机运行，`DOTNET_ENVIRONMENT` 必须为 `Development`。
- `.env` 必须能连接当前开发数据库，并包含与 API/Worker 相同的主加密密钥。
- 数据库必须已经应用全局 WorkTool 限流迁移。
- 机器人必须已启用、在线并在目标测试群内；群名称必须与 WorkTool 完全一致。

运行前先确认证据目录不会进入 Git：

```powershell
git check-ignore .local/worktool-type512-evidence/raw.json
```

预期输出该路径。没有输出时禁止继续。

## 执行

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project tools/WechatRobot.WorkToolEvidence -- `
  --robot-config-id 17341f22-a502-4fd4-8e1d-4746fb667c48 `
  --group-name "仅用于测试的客户群" `
  --output-directory .local/worktool-type512-evidence
```

工具使用与 API/Worker 相同的 `.env` 加载、数据库机器人凭据、HTTP 客户端和全局 60 QPM 限流。它最多等待 90 秒，并只接受同时匹配 `type=512` 和本次 `messageId` 的结果。

## 审阅与清理

- `raw.json` 只能在本机查看，禁止粘贴到聊天、Issue、日志或提交中。
- 对外只报告 `shape.json` 中的属性位置和 JSON 类型，不报告群名、成员昵称、机器人 ID、消息 ID或错误原文。
- 审阅完成后立即安全删除两个文件：

```powershell
Remove-Item -LiteralPath .local\worktool-type512-evidence\raw.json
Remove-Item -LiteralPath .local\worktool-type512-evidence\shape.json
git status --short
```

如果 90 秒内没有匹配结果，只记录 WorkTool 的脱敏失败码；不得猜测返回结构，也不得启用群客服配置。
