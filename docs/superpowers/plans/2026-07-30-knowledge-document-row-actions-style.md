# Knowledge Document Row Actions Style Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将知识文档列表中的详情与物理删除操作统一为简洁、无边框的 Element Plus 轻量按钮。

**Architecture:** 仅调整 `KnowledgeDocumentsView` 的桌面表格和移动卡片操作区。继续使用现有路由、权限判断、加载状态和删除确认流程，不修改 API 或业务语义。

**Tech Stack:** Vue 3、TypeScript、Element Plus、Vitest、Vue Test Utils。

## Global Constraints

- “查看详情”使用主色轻量按钮。
- “提交物理删除”使用危险色轻量按钮。
- 两个按钮统一使用 `link` 和 `size="small"`。
- 保留现有 `data-testid`、链接地址、加载状态和点击处理。
- 不修改详情页顶部的独立管理操作区。

---

### Task 1: 统一知识文档列表操作按钮

**Files:**
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentsView.vue`
- Test: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentsView.spec.ts`

**Interfaces:**
- Consumes: `ElButton`、现有文档详情路由、`requestPhysicalDelete(document)`。
- Produces: 桌面表格与移动卡片中一致的轻量操作按钮。

- [ ] **Step 1: Write the failing test**

在组件测试中找到桌面表格里的详情按钮和删除按钮，断言两者均为 `ElButton`，具有 `is-link`、`el-button--small`，并分别保留主色与危险色语义。

- [ ] **Step 2: Run test to verify it fails**

Run:

```powershell
npm test -- src/views/knowledge/KnowledgeDocumentsView.spec.ts
```

Expected: FAIL，因为当前详情操作还是原生 `<a>`，删除按钮仍使用 `plain` 描边样式。

- [ ] **Step 3: Write minimal implementation**

将桌面表格和移动卡片中的详情链接改为：

```vue
<ElButton
  tag="a"
  type="primary"
  link
  size="small"
  :href="detailUrl"
>查看详情</ElButton>
```

将对应删除按钮改为：

```vue
<ElButton
  type="danger"
  link
  size="small"
  :loading="isDeleting"
  @click="requestPhysicalDelete(document)"
>提交物理删除</ElButton>
```

- [ ] **Step 4: Run test to verify it passes**

Run:

```powershell
npm test -- src/views/knowledge/KnowledgeDocumentsView.spec.ts
npm run typecheck
npm test -- --run
npm run build
```

Expected: 聚焦测试、完整前端测试、类型检查和生产构建全部通过。
