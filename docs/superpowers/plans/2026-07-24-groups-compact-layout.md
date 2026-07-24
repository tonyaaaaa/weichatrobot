# Groups Compact Layout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reorganize `/groups` into a responsive primary-secondary layout with compact, single-row matching rules while preserving all existing behavior.

**Architecture:** Keep the existing Vue data flow and API calls unchanged. Add semantic layout wrappers to `GroupRulesView.vue`, make `RuleEditor.vue` and `ContextPolicyForm.vue` responsible for their own compact internal grids, and scope every new style to those components so global form behavior remains unchanged.

**Tech Stack:** Vue 3 SFCs, TypeScript, Vue Test Utils, Vitest, CSS Grid/Flexbox, Vite.

## Global Constraints

- Do not modify group matching semantics, API requests, DTOs, or persisted data.
- Do not modify global form element sizing or layout rules.
- Keep controls at least 44px high and preserve labels, focus states, and `aria-live`.
- Use a two-column layout at widths of at least 900px and a single-column layout below that breakpoint.
- Do not include unrelated database changes already present in the worktree.

---

### Task 1: Lock the new page hierarchy with component tests

**Files:**
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts`
- Test: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts`

**Interfaces:**
- Consumes: rendered `GroupRulesView` DOM.
- Produces: regression expectations for `.group-page-header`, `.group-identity-bar`, `.group-layout`, `.group-primary-column`, `.group-secondary-column`, `.group-save-bar`, `.rule-section-heading`, `.rule-row`, and `.context-policy-grid`.

- [ ] **Step 1: Write the failing page-structure test**

Add a test that mounts the view with an empty configuration and asserts:

```ts
expect(wrapper.find('.group-page-header').exists()).toBe(true);
expect(wrapper.find('.group-identity-bar').exists()).toBe(true);
expect(wrapper.find('.group-layout').exists()).toBe(true);
expect(wrapper.find('.group-primary-column').find('[aria-label="群匹配规则"]').exists()).toBe(true);
expect(wrapper.find('.group-secondary-column').find('[aria-label="上下文策略"]').exists()).toBe(true);
expect(wrapper.find('.group-save-bar [data-testid="save-configuration"]').exists()).toBe(true);
```

- [ ] **Step 2: Write the failing compact-rule test**

Add an exact include rule through the existing button, then assert:

```ts
const row = wrapper.get('.rule-row');
expect(row.find('select').exists()).toBe(true);
expect(row.find('input[type="text"]').exists()).toBe(true);
expect(row.find('.rule-case-toggle').exists()).toBe(true);
expect(row.find('.rule-remove').attributes('aria-label')).toContain('删除包含规则');
expect(wrapper.find('.rule-section-heading [data-testid="add-exact-include"]').exists()).toBe(true);
expect(wrapper.find('.context-policy-grid').exists()).toBe(true);
```

- [ ] **Step 3: Run the focused tests and verify RED**

Run:

```powershell
npm test -- src/views/groups/GroupRulesView.spec.ts
```

Expected: both new tests fail because the semantic wrappers and compact rule classes do not exist.

### Task 2: Implement compact rule and context controls

**Files:**
- Modify: `src/web/wechatrobot-admin/src/components/groups/RuleEditor.vue`
- Modify: `src/web/wechatrobot-admin/src/components/groups/ContextPolicyForm.vue`
- Test: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts`

**Interfaces:**
- Consumes: existing `includeRules`, `excludeRules`, `configured`, and `effective` props and existing `add`, `remove`, and `clear` events.
- Produces: responsive `.rule-group`, `.rule-section-heading`, `.rule-row`, `.rule-case-toggle`, `.rule-remove`, and `.context-policy-grid` structures.

- [ ] **Step 1: Move add actions into each rule section heading**

Render an action group beside each include/exclude heading:

```vue
<div class="rule-section-heading">
  <h3>{{ direction === 'include' ? '包含（任一匹配）' : '排除（优先级最高）' }}</h3>
  <div class="rule-add-actions">
    <button v-for="kind in ruleKinds" :key="kind" type="button"
      :data-testid="`add-${kind}-${direction}`" @click="add(direction, kind)">
      添加{{ labels[kind] }}
    </button>
  </div>
</div>
```

- [ ] **Step 2: Render each rule as a semantic single-row grid**

Use `type="text"` on the pattern input, a compact checkbox label, and a named destructive action:

```vue
<div class="rule-row">
  <select v-model="rule.patternKind" :aria-label="`${direction}-${index}-类型`">...</select>
  <input v-model="rule.pattern" type="text" :aria-label="`${direction}-${index}-模式`" ...>
  <label class="rule-case-toggle"><input v-model="rule.ignoreCase" type="checkbox">忽略大小写</label>
  <button class="rule-remove danger-action" type="button"
    :aria-label="`删除${direction === 'include' ? '包含' : '排除'}规则 ${index + 1}`"
    @click="emit('remove', direction, index)">删除</button>
</div>
```

- [ ] **Step 3: Add scoped responsive styles**

Use CSS Grid with `7rem minmax(12rem, 1fr) auto auto` on desktop, keep checkbox inputs at `1.25rem`, and switch the pattern field to span the full row below 700px. Do not alter global selectors.

- [ ] **Step 4: Group context fields in a responsive grid**

Wrap the six existing context labels with:

```vue
<div class="context-policy-grid">
  <!-- unchanged context fields -->
</div>
```

Use two equal columns by default and one column below 600px. Keep the clear button outside the grid and apply `danger-action`.

- [ ] **Step 5: Run the focused tests**

Run:

```powershell
npm test -- src/views/groups/GroupRulesView.spec.ts
```

Expected: the compact-rule assertions pass; the page hierarchy test still fails until Task 3.

### Task 3: Implement the complete `/groups` primary-secondary layout

**Files:**
- Modify: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue`
- Modify: `src/web/wechatrobot-admin/src/components/groups/RulePreview.vue`
- Test: `src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts`

**Interfaces:**
- Consumes: existing component state and functions without signature changes.
- Produces: `.group-page-header`, `.group-identity-bar`, `.group-layout`, `.group-primary-column`, `.group-secondary-column`, `.tag-choice-list`, `.preview-editor`, and `.group-save-bar`.

- [ ] **Step 1: Add the compact header and identity bar**

Split the title copy and operations link into `.group-page-header`, then place the ID input and read button in `.group-identity-bar` with an explicit `<label for="group-config-id">`.

- [ ] **Step 2: Add primary and secondary columns**

Render:

```vue
<div class="group-layout">
  <div class="group-primary-column">
    <RuleEditor ... />
    <section class="group-panel preview-panel">...</section>
  </div>
  <aside class="group-secondary-column">
    <section class="group-panel">...</section>
    <ContextPolicyForm ... />
  </aside>
</div>
```

Keep DOM order as primary content followed by secondary content so the single-column mobile sequence is rules, preview, tags, context.

- [ ] **Step 3: Compact tags and preview**

Wrap tag labels in `.tag-choice-list` using an auto-fit grid. Put the preview textarea and button in `.preview-editor`, then render `RulePreview` immediately below it without a duplicate top-level panel heading.

- [ ] **Step 4: Add the save bar**

Move the existing save button and `aria-live` notice into `.group-save-bar`, preserving `canSave`, the test ID, and the existing click handler.

- [ ] **Step 5: Add scoped page styles**

Use:

```css
.group-layout { display: grid; grid-template-columns: minmax(0, 1.55fr) minmax(18rem, 1fr); gap: var(--space-xl); align-items: start; }
@media (max-width: 900px) { .group-layout { grid-template-columns: 1fr; } }
```

Add local panel, header, identity, preview, tags, and save-bar rules. Ensure all selectors are scoped beneath `.group-rules-view`.

- [ ] **Step 6: Run the focused tests and verify GREEN**

Run:

```powershell
npm test -- src/views/groups/GroupRulesView.spec.ts
```

Expected: all `GroupRulesView` tests pass.

### Task 4: Verify the frontend and responsive runtime

**Files:**
- Verify: `src/web/wechatrobot-admin`
- Verify: `tests/e2e/admin-workflows.spec.ts`

**Interfaces:**
- Consumes: completed Vue components.
- Produces: test, typecheck, build, and browser evidence.

- [ ] **Step 1: Run all frontend tests**

Run:

```powershell
npm test
```

Expected: Vitest exits with zero failed tests.

- [ ] **Step 2: Run type checking**

Run:

```powershell
npm run typecheck
```

Expected: TypeScript and Vue SFC checks exit successfully.

- [ ] **Step 3: Build the admin frontend**

Run:

```powershell
npm run build
```

Expected: Vite build exits successfully and generates `dist`.

- [ ] **Step 4: Validate desktop and narrow layouts in a browser**

Open `http://127.0.0.1:5173/groups`, verify a desktop viewport around 1440px and a narrow viewport around 390px, and capture screenshots. Confirm:

- Desktop uses two columns and each rule occupies one row.
- Narrow layout uses one column and has no horizontal scroll.
- Add, edit, delete, preview, clear-context, load, and save controls remain visible and keyboard focusable.

- [ ] **Step 5: Review and commit only the intended frontend files**

Run:

```powershell
git diff --check
git status --short
git diff -- src/web/wechatrobot-admin/src/views/groups/GroupRulesView.vue src/web/wechatrobot-admin/src/views/groups/GroupRulesView.spec.ts src/web/wechatrobot-admin/src/components/groups/RuleEditor.vue src/web/wechatrobot-admin/src/components/groups/ContextPolicyForm.vue src/web/wechatrobot-admin/src/components/groups/RulePreview.vue
```

Stage only the plan and intended frontend files, then commit with:

```powershell
git commit -m "feat: compact groups configuration layout"
```
