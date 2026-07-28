# Model Configuration Dialog Style Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore the model configuration modal appearance and prevent global native-input CSS from overflowing Element Plus controls.

**Architecture:** Keep the existing `ModelConfigurationDialog` interaction and API unchanged. Fix the application-level Element Plus style dependencies in `main.ts`, then narrow global native form-control rules in `styles.css` so Element Plus owns the layout of its internal inputs.

**Tech Stack:** Vue 3, TypeScript, Element Plus, Vitest, Vue Test Utils, Vite.

## Global Constraints

- “新增模型配置”和“编辑”继续共用 `ModelConfigurationDialog.vue`。
- 不修改模型 API、字段、校验、保存流程或数据库。
- 原生 HTML 表单控件继续保留现有可访问性样式。
- Element Plus 输入控件不得出现双边框、越界或数字按钮错位。
- 不启用子代理；在当前会话逐项执行和验证。

---

### Task 1: Add style dependency regression coverage

**Files:**
- Modify: `src/web/wechatrobot-admin/src/views/element-plus-operational.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/models/ModelConfigurationDialog.spec.ts`

**Interfaces:**
- Consumes: `ModelConfigurationDialog` with `modelValue: boolean`.
- Produces: tests that require dialog/input-number styles and verify the form remains inside an Element Plus dialog.

- [ ] **Step 1: Write the failing style-entry test**

Read `src/web/wechatrobot-admin/src/main.ts` and assert that it contains:

```ts
import 'element-plus/es/components/dialog/style/css';
import 'element-plus/es/components/input-number/style/css';
```

- [ ] **Step 2: Write the dialog-structure regression test**

Mount `ModelConfigurationDialog` with `modelValue: true` and assert that the rendered tree contains `.el-dialog`, `.el-dialog__body`, and `.el-input-number`.

- [ ] **Step 3: Run the focused tests and verify RED**

Run:

```powershell
npm test -- --run src/views/element-plus-operational.spec.ts src/views/models/ModelConfigurationDialog.spec.ts
```

Expected: style-entry assertion fails because both imports are absent.

### Task 2: Restore component styles and isolate native control CSS

**Files:**
- Modify: `src/web/wechatrobot-admin/src/main.ts`
- Modify: `src/web/wechatrobot-admin/src/styles.css`

**Interfaces:**
- Consumes: Element Plus component class names `.el-input__inner`, `.el-select__input`, and `.el-input-number`.
- Produces: globally available Dialog/InputNumber CSS without changing component behavior.

- [ ] **Step 1: Import missing Element Plus styles**

Add:

```ts
import 'element-plus/es/components/dialog/style/css';
import 'element-plus/es/components/input-number/style/css';
```

beside the existing per-component imports in `main.ts`.

- [ ] **Step 2: Restrict raw form-control rules**

Replace broad native-control selectors with selectors excluding any Element Plus class token:

```css
button:not([class^="el-"]):not([class*=" el-"]),
input:not([class^="el-"]):not([class*=" el-"]),
select,
textarea {
  font: inherit;
}

button:not([class^="el-"]):not([class*=" el-"]),
input:not([class^="el-"]):not([class*=" el-"]),
select {
  min-height: 44px;
}

input:not([class^="el-"]):not([class*=" el-"]),
select,
textarea {
  width: 100%;
  /* preserve existing native-control declarations */
}
```

Apply the same exclusion to checkbox/radio, focus-visible, disabled button and
label rules so Element Plus owns its internal controls.

- [ ] **Step 3: Run focused tests and verify GREEN**

Run:

```powershell
npm test -- --run src/views/element-plus-operational.spec.ts src/views/models/ModelConfigurationDialog.spec.ts
```

Expected: all focused tests pass.

- [ ] **Step 4: Run complete frontend verification**

Run:

```powershell
npm run typecheck
npm test -- --run
npm run build
```

Expected: all commands exit 0 with no test failures or TypeScript/build errors.

- [ ] **Step 5: Restart and verify the running UI**

Restart Vite on `127.0.0.1:5173`, open the model configuration page, click “新增模型配置”, and verify:

- overlay and centered dialog are visible;
- dialog body is no wider than its container;
- text, select, password, and number inputs remain within the dialog;
- mobile layout has no horizontal overflow.
