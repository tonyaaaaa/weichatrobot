# 全系统 Element Plus 确认弹框设计

## 范围

替换管理后台全部 11 处 `window.confirm` 和 1 处 `window.prompt`。页面内的 `ElAlert` 是状态提示，不属于浏览器弹框，继续保留。

## 统一接口

- 新增 `confirmAction(message, options?) => Promise<boolean>`，内部使用 `ElMessageBox.confirm`。
- 新增 `promptAction(message, options?) => Promise<string | null>`，内部使用 `ElMessageBox.prompt`。
- 用户取消统一返回 `false` 或 `null`，不向页面抛出取消异常。
- 删除、停用、清除密钥等不可逆操作使用危险按钮样式；生成、批准、合并等使用警告样式。
- 保留页面现有可注入确认函数，以便组件测试不依赖真实弹层。

## 验收

- 产品代码中不存在 `window.confirm`、`window.prompt` 或 `window.alert`。
- 弹框不再显示浏览器地址标题。
- 确认、取消、输入校验和危险操作颜色均有组件测试。
