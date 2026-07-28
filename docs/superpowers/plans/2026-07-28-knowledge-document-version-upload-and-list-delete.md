# Knowledge Document Version Upload and List Delete Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose the existing document-version upload and Admin physical-delete contracts in the Vue knowledge-document administration UI.

**Architecture:** Extend the shared `KnowledgeApi.upload` boundary with an optional document ID, then consume it from the existing detail page. Reuse the existing physical-delete API and auth store in the list page, keeping backend authorization authoritative and refreshing persisted state after successful mutations.

**Tech Stack:** Vue 3 Composition API, TypeScript, Element Plus, Axios, Pinia, Vitest, Vue Test Utils.

## Global Constraints

- Preserve existing backend routes and request contracts.
- Preserve unrelated dirty-worktree changes.
- Only Admin sees the list physical-delete action.
- Use the exact confirmation text `这会停用文档并提交异步物理清理，期间不可上传新版本。确认继续？`.
- Never expose upstream response bodies or secret-bearing fields.
- Every production behavior is preceded by a failing regression test.

---

### Task 1: Optional document ID in upload API

**Files:**
- Modify: `src/web/wechatrobot-admin/src/api/knowledge.ts`
- Test: `src/web/wechatrobot-admin/src/api/knowledgeDocuments.spec.ts`

**Interfaces:**
- Consumes: existing `POST /api/knowledge/documents` multipart endpoint.
- Produces: `upload(file, onProgress, documentId?)`.

- [ ] **Step 1: Write the failing API tests**

Add tests that inspect the posted `FormData`: a normal upload contains `file`
and no `documentId`; a version upload contains the literal current document ID.

- [ ] **Step 2: Verify the API tests fail for the missing third argument**

Run:

```powershell
npm test -- --run src/api/knowledgeDocuments.spec.ts
```

Expected: the version-upload assertion fails because `documentId` is absent.

- [ ] **Step 3: Implement the minimal optional parameter**

Update the interface and implementation to append:

```ts
if (documentId) form.append('documentId', documentId);
```

- [ ] **Step 4: Verify the API tests pass**

Run the same focused test and expect all cases to pass.

### Task 2: Upload a new version from document details

**Files:**
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentManagementView.vue`
- Test: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentManagementView.spec.ts`

**Interfaces:**
- Consumes: `upload(file, onProgress, documentId?)` from Task 1 and existing `getDocument`.
- Produces: accessible file input, progress feedback, submit action, success refresh.

- [ ] **Step 1: Write failing component tests**

Cover:

- selecting a supported file and submitting calls `upload(file, callback, documentId)`;
- successful upload reloads detail and displays the returned version number;
- `disabled` and `delete-requested` documents do not expose an enabled upload action.

- [ ] **Step 2: Verify the focused component tests fail**

Run:

```powershell
npm test -- --run src/views/knowledge/KnowledgeDocumentManagementView.spec.ts
```

Expected: tests fail because the upload controls and API member do not exist.

- [ ] **Step 3: Implement the minimal upload panel**

Add `upload` to `ManagementApi`, track the selected file, progress, and busy
operation, then call:

```ts
const result = await props.api.upload(file, value => {
  uploadProgress.value = value;
}, props.documentId);
```

After success, clear the selection, show `新版本 v${result.version} 已提交处理。`,
and reload persisted detail.

- [ ] **Step 4: Verify the focused component tests pass**

Run the same focused test and expect all cases to pass.

### Task 3: Admin physical delete from document list

**Files:**
- Modify: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentsView.vue`
- Test: `src/web/wechatrobot-admin/src/views/knowledge/KnowledgeDocumentsView.spec.ts`

**Interfaces:**
- Consumes: auth roles, `requestPhysicalDelete(documentId, expectedStateVersion)`, and shared confirmation helper.
- Produces: Admin-only row action with stable success and error handling.

- [ ] **Step 1: Write failing list component tests**

Cover:

- `KnowledgeOperator` cannot see the delete action;
- `Admin` sees it and confirmation calls the API with the row ID/state version;
- success refreshes the list;
- concurrency updates the stale row;
- `document-delete-requested` displays the dedicated message.

- [ ] **Step 2: Verify the focused list tests fail**

Run:

```powershell
npm test -- --run src/views/knowledge/KnowledgeDocumentsView.spec.ts
```

Expected: tests fail because the Admin delete action is missing.

- [ ] **Step 3: Implement the minimal list mutation**

Extend `DocumentsApi`, read roles from `useAuthStore`, inject the shared
confirmation function for testability, track the busy row, and reuse the
existing row-state replacement behavior for concurrency.

- [ ] **Step 4: Verify the focused list tests pass**

Run the same focused test and expect all cases to pass.

### Task 4: Frontend regression and visual verification

**Files:**
- Review: all files changed in Tasks 1–3.

**Interfaces:**
- Consumes: the completed API and component behavior.
- Produces: verified frontend build and UI evidence.

- [ ] **Step 1: Run all frontend tests**

```powershell
npm test -- --run
```

- [ ] **Step 2: Run type checking**

```powershell
npm run typecheck
```

- [ ] **Step 3: Run the production build**

```powershell
npm run build
```

- [ ] **Step 4: Check diff hygiene**

From repository root:

```powershell
git diff --check
```

- [ ] **Step 5: Inspect the local UI when the stack is available**

Open the document list and detail routes, verify Admin/non-Admin visibility,
upload feedback, delete confirmation, and that the action area remains usable
at narrow viewport widths.
