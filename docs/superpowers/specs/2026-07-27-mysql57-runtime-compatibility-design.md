# MySQL 5.7 运行时兼容设计

## 目标

在不升级生产数据库的前提下，使 WechatRobot 的业务写入、数据库迁移和集成测试同时兼容 MySQL 5.7.44 与当前 MySQL 8.x 测试环境。

## 设计结论

1. 保留现有 `CHECK` 约束。MySQL 8.x 继续把它们作为数据库侧防线；MySQL 5.7 忽略 `CHECK` 时，由 `WechatRobotDbContext` 在持久化前执行同等校验。
2. 持久化校验覆盖所有通过 EF Core `SaveChanges` / `SaveChangesAsync` 写入的新增和修改实体：
   - `RobotConfigEntity.SendRateLimitPerMinute` 必须为 1 至 60。
   - `GroupProfileEntity.HandoffPausePolicy` 必须为 `Group` 或 `Sender`。
   - `GroupProfileEntity.RegistrationSource` 必须为 `Manual` 或 `WorkToolImport`。
   - `GroupHumanAgentEntity.VerificationStatus` 必须为 `Verified`、`Missing`、`Conflict` 或 `Stale`。
3. 校验失败抛出稳定的 `InvalidOperationException`，且在数据库命令发出前失败。API 边界原有的请求校验继续保留。
4. 测试清理不再使用 Oracle EF Provider 在 MySQL 5.7 上生成不兼容别名语法的 `ExecuteDeleteAsync`；改用固定表名、参数化条件或实体删除。
5. 回调事务回滚测试使用 MySQL 5.7 和 8.x 都支持的 `BEFORE INSERT` 触发器与 `SIGNAL SQLSTATE '45000'` 注入故障，并在 `finally` 中移除触发器。
6. `MySqlFixture` 默认使用 `mysql:8.4.10`，可通过 `WECHATROBOT_TEST_MYSQL_IMAGE=mysql:5.7.44` 切换；两个版本都显式使用 `utf8mb4` 和 `utf8mb4_bin`。测试容器启用 `log_bin_trust_function_creators`，仅用于允许非 root 测试账号创建故障注入触发器。

## 非目标

- 不更换 EF Core MySQL Provider。
- 不修改已应用迁移。
- 不移除 MySQL 8.x 可执行的数据库约束。
- 不承诺所有未来原始 SQL 自动兼容；新增 SQL 仍需双版本验证。

## 验证

- 持久化约束的集成测试在两个 MySQL 版本上均通过。
- 回调回滚、发送协调、模型默认项、群规则和 WorkTool 限流相关测试在两个版本上均通过。
- 迁移兼容合约测试通过。
- 产品代码不引入 `ExecuteDeleteAsync`。
