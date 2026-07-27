# Element Plus Confirmation Dialogs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace every browser-native confirmation and prompt with consistent Element Plus dialogs.

**Architecture:** Put cancellation normalization and visual options in a small shared dialog utility. Pages await that utility while retaining injectable action props where current component tests use them.

**Tech Stack:** Vue 3, TypeScript, Element Plus, Vitest.

## Global Constraints

- Product code must contain no `window.alert`, `window.confirm` or `window.prompt`.
- `ElAlert` remains unchanged.
- Cancellation is a normal false/null result, not an application error.

---

### Task 1: Shared dialog utility

**Files:**
- Create: `src/web/wechatrobot-admin/src/utils/dialogs.ts`
- Create: `src/web/wechatrobot-admin/src/utils/dialogs.spec.ts`

- [ ] Write failing tests for confirm success, cancel normalization, prompt result and dangerous button styling.
- [ ] Implement `confirmAction` and `promptAction` over `ElMessageBox`.
- [ ] Run utility tests.

### Task 2: Replace native dialogs

**Files:**
- Modify: `src/web/wechatrobot-admin/src/views/handoffs/HandoffQueueView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/DocumentDetailView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentManagementView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeReviewView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeTagsView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/models/ModelSettingsView.vue`
- Modify: `src/web/wechatrobot-admin/src/views/users/UserRolesView.vue`
- Test: relevant colocated `*.spec.ts` files

- [ ] Update tests to inject or mock asynchronous dialog actions and verify cancel paths.
- [ ] Replace all native calls, including split-position prompt.
- [ ] Run `rg` to prove no native dialog calls remain in product code.
- [ ] Run frontend tests, typecheck and production build.
