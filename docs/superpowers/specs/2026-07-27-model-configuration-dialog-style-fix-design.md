# 模型配置弹框样式修复规格

## 背景

模型配置页面已经使用 `ElDialog` 承载新增和编辑表单，但前端按需引入
Element Plus 时未加载 Dialog 与 InputNumber 的组件样式。同时，全局原生
`input` 规则作用到了 Element Plus 内部输入元素，造成弹框没有遮罩和容器、
数字输入控件错位、输入框越过外层边框。

## 目标

- “新增模型配置”和“编辑”继续共用同一个弹框。
- 弹框显示遮罩、白色容器、标题、关闭按钮和底部操作区。
- 桌面端弹框宽度不超过 640px，小屏幕保留 16px 页面边距并改为单列表单。
- Element Plus 输入框、选择器和数字输入框不得被原生控件全局样式重复装饰。
- 原生 HTML 表单控件继续保留现有的最小点击高度、边框和聚焦可见性。
- 不修改模型 API、字段、校验、保存流程或数据库。

## 设计

1. 在应用入口补充 `dialog` 与 `input-number` 的 Element Plus 样式依赖。
2. 保留 `ModelConfigurationDialog.vue` 的现有字段和交互，不把表单移回页面。
3. 将全局原生 `button`、`input`、`select`、`textarea`、`label` 和聚焦态规则
   限制为非 Element Plus 内部控件，为所有 `el-` 类组件保留组件自身样式。
4. 使用前端组件测试验证弹框打开后具有 Dialog 结构，使用样式入口测试防止
   按需样式再次漏引入。
5. 通过类型检查、前端测试和生产构建后，重启本地 Vite 并在运行页面验证。

## 全项目样式审计

当前代码使用 Alert、Button、ConfigProvider、Dialog、Empty、Form、Input、
InputNumber、Pagination、Progress、Select、Skeleton、Switch、Table 和 Tag 等
Element Plus 组件。入口已覆盖其他组件样式，确认仅缺 Dialog 与 InputNumber；
Option、FormItem、TableColumn 由各自父组件样式覆盖，不需要独立导入。

## 验收标准

- 点击“新增模型配置”后，表单只出现在居中的模态弹框内。
- 页面背景有遮罩，弹框可通过关闭按钮或“取消”关闭。
- 所有输入控件完整包含在弹框内，无右侧越界、双边框或数字按钮错位。
- 640px 以下视口表单为单列且无水平滚动。
- 新增、编辑、校验和保存行为保持不变。
