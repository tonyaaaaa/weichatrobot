# Admin Page Completeness Minimal Remediation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Verify every routed administration page against its existing real workflow and repair only reproducible skeleton, missing-state, overflow, or blocked-operation defects.

**Architecture:** Build an evidence-backed route inventory first. Existing pages remain unchanged when their real API workflow, state handling, and responsive interaction already work; each discovered defect gets one failing test and the smallest local fix.

**Tech Stack:** Vue 3, TypeScript, Element Plus, Vitest, browser visual inspection, existing ASP.NET Core APIs.

## Global Constraints

- Execute after the complete group-detail plan.
- Work on current `master`; do not branch, stage, commit, push, deploy, or use subagents.
- Existing functional pages are the reference implementation; do not redesign them solely for visual uniformity.
- Do not create editable system settings without a real versioned, audited runtime API.
- Do not fabricate counts, lists, status, WorkTool capability, or member identity.
- Add a failing test before every product-code change.

---

### Task 1: Inventory the existing page workflows

**Files:**
- Create: `docs/runbooks/admin-page-completeness-checklist.md`
- Create: `src/web/wechatrobot-admin/src/views/pageCompleteness.spec.ts`

- [ ] **Step 1: Add a failing route coverage test**

Create a manifest keyed by every non-public route name and assert that no routed page is omitted.

- [ ] **Step 2: Run the test and observe missing entries**

```powershell
Set-Location src/web/wechatrobot-admin
npm test -- --run src/views/pageCompleteness.spec.ts
```

- [ ] **Step 3: Record real functionality before judging presentation**

For each route, record its API module, primary operation, loading state, empty state, error/retry state, destructive confirmation, concurrency version, desktop result, and 375-pixel result.

- [ ] **Step 4: Classify only evidence-backed outcomes**

Use:

- `complete`: existing workflow works; no code change.
- `fix-required`: one or more reproducible defects.
- `blocked-no-contract`: no honest editable workflow can exist.

Expected: system settings is `blocked-no-contract`; no page is classified from appearance alone.

### Task 2: Preserve and verify existing group operations

**Files:**
- Modify only when a failing test proves a defect:
  - `src/web/wechatrobot-admin/src/views/groups/GroupOperationsView.spec.ts`
  - `src/web/wechatrobot-admin/src/views/groups/GroupOperationsView.vue`

- [ ] **Step 1: Run existing group-operation tests**

Verify manual invitation acknowledgment, robot selection, registered-group selection, WorkTool preview token, execute, acceptance state, final state, and audit-scope loading.

- [ ] **Step 2: Browser-check existing operation variants**

Check `Create`, `AddMembers`, `RemoveMembers`, `Rename`, and `UpdateAnnouncement` without changing the layout.

- [ ] **Step 3: If a workflow is blocked, write one failing test**

The test must name the exact blocked behavior, such as stale confirmation token remaining valid after form edits or a mobile field becoming unreachable.

- [ ] **Step 4: Apply the smallest fix and rerun focused tests**

Do not split the page or introduce a new wizard unless the existing architecture makes the proven defect impossible to repair locally.

### Task 3: Verify existing user, robot, knowledge, memory, audit, and queue pages

**Files:**
- Modify only the page and focused test associated with a proven defect.

- [ ] **Step 1: Run the complete existing frontend suite**

```powershell
Set-Location src/web/wechatrobot-admin
npm test -- --run
npm run typecheck
npm run build
```

- [ ] **Step 2: Inspect every navigation route at 1440 and 375 pixels**

Check actual operations rather than component choice: load, query, create/edit, save, delete/disable, retry, empty state, error recovery, dialogs, and overflow.

- [ ] **Step 3: Keep working pages unchanged**

Mark them `complete` in the checklist with test and browser evidence.

- [ ] **Step 4: Repair each proven defect test-first**

Examples of valid repair scope are a missing retry button, inaccessible dialog action, overflowing input, missing dirty guard, wrong query initialization, or a button calling no API. Replacing all native controls is not valid by itself.

### Task 4: Keep system settings as an honest contract boundary

**Files:**
- Test: `src/web/wechatrobot-admin/src/views/settings/SystemSettingsView.spec.ts`
- Modify: `src/web/wechatrobot-admin/src/views/settings/SystemSettingsView.vue` only if its current boundary statement is misleading or inaccessible.

- [ ] **Step 1: Assert no fake settings form exists**

The page must contain no editable setting fields or save button while the backend read/write/version/audit/runtime contract is absent.

- [ ] **Step 2: Verify the boundary wording**

Explain that settings require read, versioned save, audit, rollback, and runtime consumption. Do not label the page complete business functionality.

### Task 5: Final completeness and visual verification

- [ ] **Step 1: Ensure every route is `complete` or `blocked-no-contract`**

No route may remain unchecked or `fix-required`.

- [ ] **Step 2: Run frontend tests, typecheck, and production build**

- [ ] **Step 3: Run relevant backend tests and solution build**

- [ ] **Step 4: Restart API, Worker, and frontend from `.local` configuration**

- [ ] **Step 5: Recheck changed pages at 1440, 768, and 375 pixels**

- [ ] **Step 6: Run `git diff --check` and secret hygiene**

- [ ] **Step 7: Report unchanged functional pages separately from repaired pages**

Do not imply that an unchanged page was redesigned. State the intentional `blocked-no-contract` boundary explicitly.
