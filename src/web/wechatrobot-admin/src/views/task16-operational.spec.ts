import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import { describe, expect, it, vi } from 'vitest';
import KnowledgeDocumentsView from './knowledge/KnowledgeDocumentsView.vue';
import DocumentDetailView from './knowledge/DocumentDetailView.vue';
import KnowledgeTagsView from './knowledge/KnowledgeTagsView.vue';
import KnowledgeReviewView from './knowledge/KnowledgeReviewView.vue';
import ConversationAuditView from './audit/ConversationAuditView.vue';
import HandoffQueueView from './handoffs/HandoffQueueView.vue';
import ModelSettingsView from './models/ModelSettingsView.vue';
import UserRolesView from './users/UserRolesView.vue';
import SystemSettingsView from './settings/SystemSettingsView.vue';
import { safeEvidence } from '../utils/evidenceRedaction';

const primaryTagId = '11111111-1111-4111-8111-111111111111';

function createTagOptionsApi(ids: string[]) {
  return {
    options: vi.fn().mockResolvedValue(ids.map((id, index) => ({
      id,
      name: `标签 ${index + 1}`,
      isGlobalPublic: index === 0
    })))
  };
}

function createDocumentAdministrationApiStubs() {
  return {
    listDocuments: vi.fn(),
    getDocument: vi.fn(),
    getDocumentVersions: vi.fn(),
    retryDocumentUpload: vi.fn(),
    disableDocument: vi.fn(),
    requestPhysicalDelete: vi.fn(),
    deletePreview: vi.fn()
  };
}

describe('Task 16 operational pages', () => {
  it('shows upload progress, upload errors and DOC conversion guidance without a repeated OSS banner', async () => {
    const api = {
      upload: vi.fn(async (_file: File, progress: (value: number) => void) => {
        progress(45);
        throw new Error('OSS unavailable');
      })
    };
    const wrapper = mount(KnowledgeDocumentsView, { props: { api } });
    const input = wrapper.get('input[type="file"]');
    Object.defineProperty(input.element, 'files', { configurable: true, value: [new File(['x'], 'legacy.doc')] });
    await input.trigger('change');
    expect(wrapper.get('[data-testid="upload-document"]').attributes('disabled')).toBeDefined();
    Object.defineProperty(input.element, 'files', { configurable: true, value: [new File(['x'], 'manual.pdf')] });
    await input.trigger('change');
    await wrapper.get('[data-testid="upload-document"]').trigger('click');
    await flushPromises();
    expect(wrapper.text()).toContain('45%');
    expect(wrapper.text()).toContain('OSS unavailable');
    expect(wrapper.text()).toContain('DOC');
    expect(wrapper.text()).toContain('DOCX');
    expect(wrapper.text()).not.toContain('公共读 OSS 风险提示');
  });

  it('links a successful upload to its real document version detail', async () => {
    const api = {
      upload: vi.fn().mockResolvedValue({
        documentId: 'doc-1', versionId: 'ver-1', version: 1, state: 'uploading',
        safeFileName: 'manual.pdf', publicUrl: 'https://public.example.test/manual.pdf',
        publicReadWarning: '公共读 OSS'
      })
    };
    const wrapper = mount(KnowledgeDocumentsView, { props: { api } });
    const input = wrapper.get('input[type="file"]');
    Object.defineProperty(input.element, 'files', { value: [new File(['x'], 'manual.pdf')] });
    await input.trigger('change');
    await wrapper.get('[data-testid="upload-document"]').trigger('click');
    await flushPromises();
    expect(wrapper.get('[data-testid="open-document-detail"]').attributes('href')).toBe('/knowledge/documents/doc-1/versions/ver-1');
  });

  it('previews, edits, splits and merges chunks and retries a failed index job', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    vi.spyOn(window, 'prompt').mockReturnValue('5');
    const preview = { id: 'p1', sequence: 1, text: 'first chunk', revision: 3, status: 'draft' };
    const secondPreview = { id: 'p2', sequence: 2, text: 'second chunk', revision: 3, status: 'draft' };
    const failedStatus = {
      documentId: 'doc-1',
      activeVersionId: 'ver-1',
      documentStatus: 'active',
      collectionName: 'knowledge_doc_1',
      approvedChunkCount: 2,
      activePointCount: 2,
      consistency: 'consistent',
      driftDetails: [],
      jobs: [
        { id: 'job-1', versionId: 'ver-1', operation: 'reindex', status: 'failed', attemptCount: 3, failureReason: 'provider unavailable' }
      ]
    };
    const api = {
      ...createDocumentAdministrationApiStubs(),
      upload: vi.fn(),
      getPreviews: vi.fn().mockResolvedValue({ revision: 3, items: [preview, secondPreview] }),
      getIndexStatus: vi.fn().mockResolvedValue(failedStatus),
      editPreview: vi.fn().mockResolvedValue({ revision: 4, items: [{ ...preview, text: 'edited' }, secondPreview] }),
      splitPreview: vi.fn().mockResolvedValue({ revision: 5, items: [preview, secondPreview] }),
      mergePreviews: vi.fn().mockResolvedValue({ revision: 6, items: [preview] }),
      retryIndex: vi.fn().mockResolvedValue(undefined),
      generatePreviews: vi.fn().mockResolvedValue({ revision: 7, items: [preview, secondPreview] }),
      approvePreviews: vi.fn().mockResolvedValue([preview, secondPreview]),
      queueIndex: vi.fn().mockResolvedValue({ jobId: 'job-2' })
    };
    const wrapper = mount(DocumentDetailView, {
      props: {
        documentId: 'doc-1',
        versionId: 'ver-1',
        api,
        tagApi: createTagOptionsApi([primaryTagId])
      }
    });
    await flushPromises();
    expect(wrapper.get('[data-testid="queue-index"]').text()).toBe('重新索引');
    expect(wrapper.text()).toContain('active');
    expect((wrapper.get('[data-testid="text-p1"]').element as HTMLTextAreaElement).value).toBe('first chunk');
    await wrapper.get('[data-testid="text-p1"]').setValue('edited chunk');
    await wrapper.get('[data-testid="edit-p1"]').trigger('click');
    await flushPromises();
    await wrapper.get('[data-testid="split-p1"]').trigger('click');
    await flushPromises();
    await wrapper.get('[data-testid="select-p1"]').setValue(true);
    await wrapper.get('[data-testid="select-p2"]').setValue(true);
    await wrapper.get('[data-testid="merge-selected"]').trigger('click');
    await wrapper.get('[data-testid="retry-index"]').trigger('click');
    await wrapper.get('[data-testid="generate-previews"]').trigger('click');
    await flushPromises();
    await wrapper.get('[data-testid="approve-previews"]').trigger('click');
    await flushPromises();
    expect(wrapper.find('#index-tag-ids').exists()).toBe(false);
    await wrapper.get(`[data-testid="knowledge-tag-${primaryTagId}"]`).setValue(true);
    await wrapper.get('[data-testid="queue-index"]').trigger('click');
    await flushPromises();
    expect(api.editPreview).toHaveBeenCalled();
    expect(api.splitPreview).toHaveBeenCalledWith('ver-1', 'p1', 5, expect.any(Number));
    expect(api.mergePreviews).toHaveBeenCalled();
    expect(api.retryIndex).toHaveBeenCalledWith('job-1');
    expect(api.generatePreviews).toHaveBeenCalled();
    expect(api.approvePreviews).toHaveBeenCalled();
    expect(api.queueIndex).toHaveBeenCalled();
  });

  it('requires a document tag selection and submits enabled selected IDs', async () => {
    const firstTag = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
    const secondTag = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
    const api = {
      ...createDocumentAdministrationApiStubs(),
      upload: vi.fn(),
      getPreviews: vi.fn().mockResolvedValue({ revision: 1, items: [] }),
      getIndexStatus: vi.fn().mockResolvedValue({
        documentId: 'doc-1', activeVersionId: null, documentStatus: 'draft', collectionName: null,
        approvedChunkCount: 0, activePointCount: 0, consistency: 'inactive', driftDetails: [], jobs: []
      }),
      editPreview: vi.fn(), splitPreview: vi.fn(), mergePreviews: vi.fn(), retryIndex: vi.fn(),
      generatePreviews: vi.fn(), approvePreviews: vi.fn(),
      queueIndex: vi.fn().mockResolvedValue({ jobId: 'job-1' })
    };
    const wrapper = mount(DocumentDetailView, {
      props: {
        documentId: 'doc-1',
        versionId: 'ver-1',
        api,
        tagApi: createTagOptionsApi([firstTag, secondTag])
      }
    });
    await flushPromises();

    expect(wrapper.find('#index-tag-ids').exists()).toBe(false);
    await wrapper.get('[data-testid="queue-index"]').trigger('click');
    expect(api.queueIndex).not.toHaveBeenCalled();
    expect(wrapper.get('[data-testid="index-tag-error"]').text()).toContain('至少选择一个已启用的知识标签');

    await wrapper.get(`[data-testid="knowledge-tag-${firstTag}"]`).setValue(true);
    await wrapper.get(`[data-testid="knowledge-tag-${secondTag}"]`).setValue(true);
    await wrapper.get('[data-testid="queue-index"]').trigger('click');
    await flushPromises();
    expect(api.queueIndex).toHaveBeenCalledWith('doc-1', 'ver-1', [firstTag, secondTag], false);
  });

  it('rejects non-adjacent merges and sends adjacent IDs in sequence order regardless of selection order', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const first = { id: 'p1', sequence: 1, text: 'first', status: 'draft' };
    const second = { id: 'p2', sequence: 2, text: 'second', status: 'draft' };
    const third = { id: 'p3', sequence: 3, text: 'third', status: 'draft' };
    const api = {
      ...createDocumentAdministrationApiStubs(),
      upload: vi.fn(),
      getPreviews: vi.fn().mockResolvedValue({ revision: 2, items: [first, second, third] }),
      getIndexStatus: vi.fn().mockResolvedValue({
        documentId: 'doc-1', activeVersionId: null, documentStatus: 'draft', collectionName: null,
        approvedChunkCount: 0, activePointCount: 0, consistency: 'inactive', driftDetails: [], jobs: []
      }),
      editPreview: vi.fn(), splitPreview: vi.fn(),
      mergePreviews: vi.fn().mockResolvedValue({ revision: 3, items: [first, third] }),
      retryIndex: vi.fn(), generatePreviews: vi.fn(), approvePreviews: vi.fn(), queueIndex: vi.fn()
    };
    const wrapper = mount(DocumentDetailView, { props: { documentId: 'doc-1', versionId: 'ver-1', api } });
    await flushPromises();

    await wrapper.get('[data-testid="select-p1"]').setValue(true);
    await wrapper.get('[data-testid="select-p3"]').setValue(true);
    await wrapper.get('[data-testid="merge-selected"]').trigger('click');
    expect(api.mergePreviews).not.toHaveBeenCalled();
    expect(wrapper.text()).toContain('只能合并序号相邻的两个分段');

    await wrapper.get('[data-testid="select-p1"]').setValue(false);
    await wrapper.get('[data-testid="select-p2"]').setValue(true);
    await wrapper.get('[data-testid="merge-selected"]').trigger('click');
    await flushPromises();
    expect(api.mergePreviews).toHaveBeenCalledWith('ver-1', 'p2', 'p3', 2);
  });

  it('explains that any bound tag matches and global-public content is always visible', async () => {
    const pinia = createPinia();
    const wrapper = mount(KnowledgeTagsView, {
      props: {
        api: {
          list: vi.fn().mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 20 }),
          options: vi.fn(),
          create: vi.fn(),
          update: vi.fn(),
          setEnabled: vi.fn(),
          delete: vi.fn()
        }
      },
      global: { plugins: [pinia] }
    });
    await flushPromises();
    expect(wrapper.text()).toContain('任一标签');
    expect(wrapper.text()).toContain('全局公开');
    expect(wrapper.text()).toContain('OR');
  });

  it('shows candidate evidence and approves a revised answer with selected tags', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const api = {
      listCandidates: vi.fn().mockResolvedValue({ items: [{ id: 'c1', question: '怎么退款？', status: 'pending', version: 2, updatedAtUtc: '2026-07-22T00:00:00Z' }], total: 1, page: 1, pageSize: 20 }),
      getCandidate: vi.fn().mockResolvedValue({ id: 'c1', question: '怎么退款？', answer: '联系售后', evidenceJson: '{"source":"human"}', status: 'pending', version: 2 }),
      reviewCandidate: vi.fn().mockResolvedValue({ status: 'approved_pending_index' })
    };
    const wrapper = mount(KnowledgeReviewView, {
      props: { api, tagApi: createTagOptionsApi([primaryTagId]) }
    });
    await flushPromises();
    expect(api.listCandidates).toHaveBeenCalledWith('pending', 1, 20);
    expect(wrapper.findAllComponents({ name: 'ElOption' }).map(option => option.props('value'))).toEqual([
      'pending', 'revision', 'approved_pending_index', 'indexing', 'published', 'rejected'
    ]);
    await wrapper.get('[data-testid="candidate-c1"]').trigger('click');
    await flushPromises();
    expect(wrapper.find('#candidate-tags').exists()).toBe(false);
    await wrapper.get(`[data-testid="knowledge-tag-${primaryTagId}"]`).setValue(true);
    await wrapper.get('[data-testid="approve-candidate"]').trigger('click');
    expect(api.reviewCandidate).toHaveBeenCalledWith('c1', expect.objectContaining({
      decision: 'approve',
      tagIds: [primaryTagId],
      expectedVersion: 2
    }));
  });

  it('requires selected tags for approval but permits rejecting without tags', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const tagId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
    const candidate = { id: 'c1', question: 'Q', answer: 'A', evidenceJson: '{}', status: 'pending', version: 2 };
    const api = {
      listCandidates: vi.fn().mockResolvedValue({
        items: [{ id: 'c1', question: 'Q', status: 'pending', version: 2, updatedAtUtc: '2026-07-22T00:00:00Z' }],
        total: 1, page: 1, pageSize: 20
      }),
      getCandidate: vi.fn().mockResolvedValue(candidate),
      reviewCandidate: vi.fn().mockResolvedValue({ status: 'rejected' })
    };
    const wrapper = mount(KnowledgeReviewView, {
      props: { api, tagApi: createTagOptionsApi([tagId]) }
    });
    await flushPromises();
    await wrapper.get('[data-testid="candidate-c1"]').trigger('click');
    await flushPromises();

    expect(wrapper.find('#candidate-tags').exists()).toBe(false);
    await wrapper.get('[data-testid="approve-candidate"]').trigger('click');
    expect(api.reviewCandidate).not.toHaveBeenCalled();
    expect(wrapper.get('[data-testid="candidate-tag-error"]').text()).toContain('至少选择一个已启用的知识标签');

    await wrapper.get(`[data-testid="knowledge-tag-${tagId}"]`).setValue(true);
    await wrapper.get('[data-testid="approve-candidate"]').trigger('click');
    await flushPromises();
    expect(api.reviewCandidate).toHaveBeenCalledWith('c1', expect.objectContaining({ decision: 'approve', tagIds: [tagId] }));

    api.getCandidate.mockResolvedValue({ ...candidate, id: 'c2' });
    api.reviewCandidate.mockResolvedValue({ status: 'rejected' });
    await wrapper.setProps({ api });
    await wrapper.get('[data-testid="candidate-c1"]').trigger('click');
    await flushPromises();
    await wrapper.get('[data-testid="reject-candidate"]').trigger('click');
    await flushPromises();
    expect(api.reviewCandidate).toHaveBeenLastCalledWith('c2', expect.objectContaining({ decision: 'reject', tagIds: [] }));
  });

  it('renders authorized audit sources but strips secrets from full evidence', async () => {
    const api = {
      groupOptions: vi.fn().mockResolvedValue([]),
      capability: vi.fn().mockResolvedValue({
        available: true,
        items: [{
          id: 'a1', question: 'Q', answer: 'A', sources: ['产品手册#3'],
          evidence: { score: 0.91, apiKey: 'sk-secret', authorization: 'Bearer hidden' },
          inputSummary: { promptTemplateVersion: 'grounded-v2' },
          send: { status: 'completed', attemptCount: 1 },
          handoff: {
            state: 'Resolved', reasonCode: 'explicit_transfer',
            transitions: [{ sequence: 1, fromState: 'AIActive', toState: 'WaitingHuman', reasonCode: 'explicit_transfer' }]
          },
          knowledgeCandidate: { status: 'approved_pending_index' },
          createdAtUtc: '2026-07-22T00:00:00Z'
        }],
        total: 21, page: 1, pageSize: 20
      })
    };
    const wrapper = mount(ConversationAuditView, { props: { api } });
    await flushPromises();
    expect(wrapper.text()).toContain('产品手册#3');
    expect(wrapper.text()).toContain('0.91');
    expect(wrapper.text()).toContain('grounded-v2');
    expect(wrapper.text()).toContain('completed');
    expect(wrapper.text()).toContain('Resolved');
    expect(wrapper.text()).toContain('AIActive');
    expect(wrapper.text()).toContain('approved_pending_index');
    expect(wrapper.text()).not.toContain('sk-secret');
    expect(wrapper.text()).not.toContain('Bearer hidden');
    const pagination = wrapper.findComponent({ name: 'ElPagination' });
    expect(pagination.exists()).toBe(true);
    pagination.vm.$emit('current-change', 2);
    await flushPromises();
    expect(api.capability).toHaveBeenLastCalledWith({
      groupId: undefined,
      fromUtc: undefined,
      toUtc: undefined,
      page: 2,
      pageSize: 20
    });
  });

  it('redacts secret-shaped values from review and handoff evidence', async () => {
    const reviewApi = {
      listCandidates: vi.fn().mockResolvedValue({ items: [{ id: 'c1', question: 'Q', status: 'pending_review', version: 1, updatedAtUtc: '2026-07-22T00:00:00Z' }], total: 1, page: 1, pageSize: 20 }),
      getCandidate: vi.fn().mockResolvedValue({ id: 'c1', question: 'Q', answer: 'A', evidenceJson: '{"score":0.9,"apiKey":"sk-review-secret"}', status: 'pending_review', version: 1 }),
      reviewCandidate: vi.fn()
    };
    const review = mount(KnowledgeReviewView, { props: { api: reviewApi } });
    await flushPromises();
    await review.get('[data-testid="candidate-c1"]').trigger('click');
    await flushPromises();
    expect(review.text()).toContain('0.9');
    expect(review.text()).not.toContain('sk-review-secret');

    const handoff = { id: 'h1', state: 'WaitingHuman', reasonCode: 'low_confidence', evidenceJson: '{"authorization":"Bearer handoff-secret","score":0.5}', version: 1, updatedAtUtc: '2026-07-22T00:00:00Z' };
    const handoffApi = {
      assignees: vi.fn().mockResolvedValue([]),
      list: vi.fn().mockResolvedValue({ items: [handoff], total: 1, page: 1, pageSize: 20 }),
      detail: vi.fn().mockResolvedValue(handoff),
      messages: vi.fn().mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 20 }),
      transitions: vi.fn().mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 20 }),
      assign: vi.fn(), resolve: vi.fn(), restore: vi.fn()
    };
    const queue = mount(HandoffQueueView, { props: { api: handoffApi } });
    await flushPromises();
    await queue.get('[data-testid="handoff-h1"]').trigger('click');
    await flushPromises();
    expect(queue.text()).toContain('0.5');
    expect(queue.text()).not.toContain('Bearer handoff-secret');
  });

  it('recursively redacts credentials and secret-shaped free text without hiding harmless metrics', () => {
    const rendered = safeEvidence({
      nested: {
        credential: 'db-password',
        clientSecret: 'client-secret',
        accessToken: 'token-value',
        clientPrivateKey: 'client-private-key',
        private_key: 'private-key',
        pwd: 'short-password',
        passphrase: 'key-passphrase',
        accessKey: 'cloud-access-key',
        secretKey: 'cloud-secret-key',
        cookie: 'sid=cookie-value',
        session: 'session-value',
        tokenCount: 128,
        retryCount: 3,
        note: 'Authorization: Basic dXNlcjpwYXNz; Bearer abc.def; AKIAABCDEFGHIJKLMNOP; glpat-abcdefghijklmnop; ghp_abcdefghijklmnopqrstuvwxyz; github_pat_11AAabcdefghijklmnopqrstuvwxyz; sk-live-secret; Pwd=short-free-secret; password=plain-secret; credential:client-login',
        pem: '-----BEGIN PRIVATE KEY-----\nprivate-material\n-----END PRIVATE KEY-----',
        harmless: 'tokenization metrics and password policy are safe prose',
        url: 'https://example.test/callback?access_token=url-secret&safe=shown&api_key=query-secret&session=query-session'
      }
    });

    expect(rendered).not.toContain('db-password');
    expect(rendered).not.toContain('client-secret');
    expect(rendered).not.toContain('token-value');
    expect(rendered).not.toContain('client-private-key');
    expect(rendered).not.toContain('private-key');
    expect(rendered).not.toContain('short-password');
    expect(rendered).not.toContain('key-passphrase');
    expect(rendered).not.toContain('cloud-access-key');
    expect(rendered).not.toContain('cloud-secret-key');
    expect(rendered).not.toContain('cookie-value');
    expect(rendered).not.toContain('session-value');
    expect(rendered).not.toContain('abc.def');
    expect(rendered).not.toContain('dXNlcjpwYXNz');
    expect(rendered).not.toContain('AKIAABCDEFGHIJKLMNOP');
    expect(rendered).not.toContain('glpat-abcdefghijklmnop');
    expect(rendered).not.toContain('ghp_abcdefghijklmnopqrstuvwxyz');
    expect(rendered).not.toContain('github_pat_11AAabcdefghijklmnopqrstuvwxyz');
    expect(rendered).not.toContain('sk-live-secret');
    expect(rendered).not.toContain('short-free-secret');
    expect(rendered).not.toContain('private-material');
    expect(rendered).not.toContain('url-secret');
    expect(rendered).not.toContain('query-secret');
    expect(rendered).not.toContain('query-session');
    expect(rendered).not.toContain('plain-secret');
    expect(rendered).not.toContain('client-login');
    expect(rendered).toContain('"tokenCount": 128');
    expect(rendered).toContain('"retryCount": 3');
    expect(rendered).toContain('safe=shown');
    expect(rendered).toContain('tokenization metrics and password policy are safe prose');
  });

  it('saves the current draft before splitting at the operator-selected index', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    vi.spyOn(window, 'prompt').mockReturnValue('6');
    const original = { id: 'p1', sequence: 1, text: 'old content', status: 'draft' };
    const edited = { ...original, text: 'fresh draft' };
    const status = {
      documentId: 'doc-1', activeVersionId: null, documentStatus: 'draft', collectionName: null,
      approvedChunkCount: 0, activePointCount: 0, consistency: 'inactive', driftDetails: [], jobs: []
    };
    const api = {
      ...createDocumentAdministrationApiStubs(),
      upload: vi.fn(),
      getPreviews: vi.fn().mockResolvedValue({ versionId: 'ver-1', revision: 2, items: [original] }),
      getIndexStatus: vi.fn().mockResolvedValue(status),
      editPreview: vi.fn().mockResolvedValue({ versionId: 'ver-1', revision: 3, items: [edited] }),
      splitPreview: vi.fn().mockResolvedValue({ versionId: 'ver-1', revision: 4, items: [edited] }),
      mergePreviews: vi.fn(), retryIndex: vi.fn(), generatePreviews: vi.fn(), approvePreviews: vi.fn(), queueIndex: vi.fn()
    };
    const wrapper = mount(DocumentDetailView, { props: { documentId: 'doc-1', versionId: 'ver-1', api } });
    await flushPromises();
    await wrapper.get('[data-testid="text-p1"]').setValue('fresh draft');
    await wrapper.get('[data-testid="split-p1"]').trigger('click');
    await flushPromises();

    expect(api.editPreview).toHaveBeenCalledWith('ver-1', 'p1', 'fresh draft', 2);
    expect(api.splitPreview).toHaveBeenCalledWith('ver-1', 'p1', 6, 3);
    expect(api.editPreview.mock.invocationCallOrder[0]).toBeLessThan(api.splitPreview.mock.invocationCallOrder[0]);
  });

  it('requires confirmation before regenerate, merge, and split mutations', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(false);
    const first = { id: 'p1', sequence: 1, text: 'first', status: 'draft' };
    const second = { id: 'p2', sequence: 2, text: 'second', status: 'draft' };
    const api = {
      ...createDocumentAdministrationApiStubs(),
      upload: vi.fn(),
      getPreviews: vi.fn().mockResolvedValue({ versionId: 'ver-1', revision: 2, items: [first, second] }),
      getIndexStatus: vi.fn().mockResolvedValue({
        documentId: 'doc-1', activeVersionId: null, documentStatus: 'draft', collectionName: null,
        approvedChunkCount: 0, activePointCount: 0, consistency: 'inactive', driftDetails: [], jobs: []
      }),
      editPreview: vi.fn(), splitPreview: vi.fn(), mergePreviews: vi.fn(), retryIndex: vi.fn(),
      generatePreviews: vi.fn(), approvePreviews: vi.fn(), queueIndex: vi.fn()
    };
    const wrapper = mount(DocumentDetailView, { props: { documentId: 'doc-1', versionId: 'ver-1', api } });
    await flushPromises();
    await wrapper.get('[data-testid="select-p1"]').setValue(true);
    await wrapper.get('[data-testid="select-p2"]').setValue(true);
    await wrapper.get('[data-testid="split-p1"]').trigger('click');
    await wrapper.get('[data-testid="merge-selected"]').trigger('click');
    await wrapper.get('[data-testid="generate-previews"]').trigger('click');

    expect(api.splitPreview).not.toHaveBeenCalled();
    expect(api.mergePreviews).not.toHaveBeenCalled();
    expect(api.generatePreviews).not.toHaveBeenCalled();
  });

  it('reloads the handoff after candidate-returning resolve and never restores with the candidate id', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const handoff = { id: 'h1', state: 'WaitingHuman', reasonCode: 'low_confidence', version: 1, updatedAtUtc: '2026-07-22T00:00:00Z' };
    const handling = { ...handoff, state: 'HumanHandling', version: 2 };
    const resolved = { ...handoff, state: 'Resolved', version: 3, finalAnswer: '人工答案' };
    const api = {
      assignees: vi.fn().mockResolvedValue([{
        id: '11111111-1111-1111-1111-111111111111',
        displayName: '客服甲',
        email: 'agent@example.test',
        roles: ['HumanAgent'],
        isEnabled: true
      }]),
      list: vi.fn().mockResolvedValue({ items: [handoff], total: 1, page: 1, pageSize: 20 }),
      detail: vi.fn()
        .mockResolvedValueOnce(handoff)
        .mockResolvedValueOnce(handling)
        .mockResolvedValueOnce(resolved)
        .mockResolvedValueOnce({ ...resolved, state: 'AIActive', version: 4 }),
      messages: vi.fn().mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 20 }),
      transitions: vi.fn().mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 20 }),
      assign: vi.fn().mockResolvedValue({ id: 'h1', state: 'HumanHandling', assigneeUserId: '11111111-1111-1111-1111-111111111111', version: 2 }),
      resolve: vi.fn().mockResolvedValue({ id: 'c1', handoffCaseId: 'h1', question: 'Q', answer: '人工答案', status: 'pending', version: 1 }),
      restore: vi.fn().mockResolvedValue({ id: 'h1', state: 'AIActive', assigneeUserId: null, version: 4 })
    };
    const wrapper = mount(HandoffQueueView, { props: { api } });
    await flushPromises();
    await wrapper.get('[data-testid="handoff-h1"]').trigger('click');
    await flushPromises();
    const assigneeSelector = wrapper.findAllComponents({ name: 'ElSelect' })
      .find(component => component.attributes('data-testid') === 'assignee');
    expect(assigneeSelector).toBeDefined();
    assigneeSelector!.vm.$emit('update:modelValue', '11111111-1111-1111-1111-111111111111');
    await wrapper.vm.$nextTick();
    await wrapper.get('[data-testid="assign-handoff"]').trigger('click');
    await flushPromises();
    await wrapper.get('[data-testid="final-answer"]').setValue('人工答案');
    await wrapper.get('[data-testid="resolve-handoff"]').trigger('click');
    await flushPromises();
    await wrapper.get('[data-testid="restore-handoff"]').trigger('click');
    await flushPromises();
    expect(api.assign).toHaveBeenCalled();
    expect(api.resolve).toHaveBeenCalled();
    expect(api.restore).toHaveBeenCalledWith('h1', 3);
  });

  it('paginates handoff messages and transitions independently with complete metadata', async () => {
    const handoff = { id: 'h1', state: 'HumanHandling', reasonCode: 'manual', version: 2, updatedAtUtc: '2026-07-22T00:00:00Z' };
    const api = {
      assignees: vi.fn().mockResolvedValue([]),
      list: vi.fn().mockResolvedValue({ items: [handoff], total: 1, page: 1, pageSize: 20 }),
      detail: vi.fn().mockResolvedValue(handoff),
      messages: vi.fn()
        .mockResolvedValueOnce({ items: [{ id: 'm1', senderDisplayName: '客服', text: '答案', authenticationKind: 'authenticated_api', createdAtUtc: '2026-07-22T00:00:00Z' }], total: 11, page: 1, pageSize: 10 })
        .mockResolvedValueOnce({ items: [{ id: 'm2', senderDisplayName: '用户', text: '追问', authenticationKind: 'worktool_display_name_unverified', createdAtUtc: '2026-07-22T00:01:00Z' }], total: 11, page: 2, pageSize: 10 }),
      transitions: vi.fn()
        .mockResolvedValueOnce({ items: [{ id: 't1', actorUserId: 'u1', sequence: 1, fromState: 'WaitingHuman', toState: 'HumanHandling', reasonCode: 'assignment', createdAtUtc: '2026-07-22T00:00:00Z' }], total: 11, page: 1, pageSize: 10 })
        .mockResolvedValueOnce({ items: [{ id: 't2', actorUserId: 'u2', sequence: 2, fromState: 'HumanHandling', toState: 'Resolved', reasonCode: 'authenticated_resolution', createdAtUtc: '2026-07-22T00:01:00Z' }], total: 11, page: 2, pageSize: 10 }),
      assign: vi.fn(), resolve: vi.fn(), restore: vi.fn()
    };
    const wrapper = mount(HandoffQueueView, { props: { api } });
    await flushPromises();
    await wrapper.get('[data-testid="handoff-h1"]').trigger('click');
    await flushPromises();
    expect(wrapper.text()).toContain('客服');
    expect(wrapper.text()).toContain('authenticated_api');
    expect(wrapper.text()).toContain('2026/07/22 08:00');
    expect(wrapper.text()).toContain('#1');
    expect(wrapper.text()).toContain('u1');
    await wrapper.get('[data-testid="messages-next"]').trigger('click');
    await wrapper.get('[data-testid="transitions-next"]').trigger('click');
    await flushPromises();
    expect(api.messages).toHaveBeenLastCalledWith('h1', 2, 10);
    expect(api.transitions).toHaveBeenLastCalledWith('h1', 2, 10);
  });

  it('masks model secrets and tests a saved connection without revealing the key', async () => {
    const configured = { id: 'm1', name: 'chat-default', provider: 'openai-compatible', configurationType: 'chat' as const, baseUrl: 'https://example.test/v1', model: 'gpt-test', timeoutSeconds: 60, maxRetries: 2, isEnabled: true, isDefault: true, connectionStatus: 'Succeeded' as const, hasApiKey: true, lastFour: '1234', version: 1 };
    const api = {
      list: vi.fn().mockResolvedValue([configured]),
      create: vi.fn(),
      update: vi.fn(),
      testConnection: vi.fn().mockResolvedValue(configured),
      setEnabled: vi.fn(),
      setDefault: vi.fn(),
      clearApiKey: vi.fn(),
      delete: vi.fn()
    };
    const wrapper = mount(ModelSettingsView, { props: { api } });
    await flushPromises();
    expect(wrapper.text()).toContain('••••1234');
    expect(wrapper.text()).not.toContain('sk-');
    await wrapper.get('[data-testid="test-m1"]').trigger('click');
    expect(api.testConnection).toHaveBeenCalledWith('m1');
  });

  it('exposes implemented user administration while keeping unavailable system settings honest', async () => {
    const users = mount(UserRolesView, { props: { api: {
      list: vi.fn().mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 20 }),
      roles: vi.fn().mockResolvedValue(['Admin', 'KnowledgeOperator', 'HumanAgent']),
      create: vi.fn(), setEnabled: vi.fn(), setRoles: vi.fn(),
      setWorkToolDisplayName: vi.fn(), clearWorkToolDisplayName: vi.fn()
    } } });
    await flushPromises();
    expect(users.text()).not.toContain('后端暂未提供');
    expect(users.find('[data-testid="create-user"]').exists()).toBe(true);
    expect(mount(SystemSettingsView).text()).toContain('后端暂未提供');
  });
});
