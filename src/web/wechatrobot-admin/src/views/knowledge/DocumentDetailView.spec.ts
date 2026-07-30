import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import type { KnowledgeApi } from '../../api/knowledge';
import DocumentDetailView from './DocumentDetailView.vue';

function createApi(): KnowledgeApi {
  return {
    upload: vi.fn(),
    listDocuments: vi.fn(),
    getDocument: vi.fn(),
    getDocumentVersions: vi.fn().mockResolvedValue([
      {
        id: 'version-1', version: 1, originalFileName: 'source.docx', safeFileName: 'source.docx',
        contentType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', sizeBytes: 1,
        status: 'preview', failureReason: null, isPublished: false, hasPublicObject: true,
        previewRevision: 3, previewCount: 1, approvedChunkCount: 0, ocrPageCount: 0, ocrFailedPageCount: 0,
        sourceKind: 'DocumentUpload', sourceActorDisplayName: '系统管理员', tags: [],
        uploadAndParseJobs: [], indexJobs: [], createdAtUtc: '', updatedAtUtc: ''
      }
    ]),
    getWorkbench: vi.fn().mockResolvedValue({
      documentId: 'document-1',
      documentTitle: '产品手册',
      documentStatus: 'preview',
      documentStateVersion: 3,
      activeVersionId: null,
      version: {
        id: 'version-1', version: 1, status: 'preview', isPublished: false,
        sourceKind: 'DocumentUpload', sourceActorDisplayName: '系统管理员',
        sourceBatchId: null, changeKind: 'New', supersedesVersionId: null,
        tags: [], indexJobs: [], createdAtUtc: '', updatedAtUtc: ''
      },
      chunks: [],
      sourceEvidence: null,
      sourceEvidenceUnavailableReason: null,
      editableRevision: null,
      canCreateRevision: false
    }),
    createRevision: vi.fn().mockResolvedValue({
      documentId: 'document-1', versionId: 'revision-2',
      version: 2, previewRevision: 1
    }),
    retryDocumentUpload: vi.fn(),
    disableDocument: vi.fn(),
    requestPhysicalDelete: vi.fn(),
    getPreviews: vi.fn().mockResolvedValue({
      versionId: 'version-1',
      revision: 3,
      items: [{ id: 'preview-1', sequence: 0, text: '第一段' }]
    }),
    generatePreviews: vi.fn().mockResolvedValue({
      versionId: 'version-1', revision: 4, items: []
    }),
    editPreview: vi.fn(),
    splitPreview: vi.fn(),
    mergePreviews: vi.fn(),
    deletePreview: vi.fn().mockResolvedValue({
      versionId: 'version-1', revision: 4, items: []
    }),
    approvePreviews: vi.fn(),
    getIndexStatus: vi.fn().mockResolvedValue({
      documentId: 'document-1',
      documentStatus: 'preview',
      approvedChunkCount: 0,
      consistency: 'not-checked',
      driftDetails: [],
      jobs: []
    }),
    queueIndex: vi.fn(),
    retryIndex: vi.fn()
  };
}

describe('DocumentDetailView advanced chunk controls', () => {
  it('generates separator previews with explicit length and overlap settings', async () => {
    const api = createApi();
    const confirmAction = vi.fn().mockResolvedValue(true);
    const wrapper = mount(DocumentDetailView, {
      props: {
        documentId: 'document-1',
        versionId: 'version-1',
        api,
        tagApi: { options: vi.fn().mockResolvedValue([]) },
        confirmAction
      }
    });
    await flushPromises();

    await wrapper.get('[data-testid="chunk-policy-kind"]').setValue('separator');
    await wrapper.get('[data-testid="chunk-target-tokens"]').setValue('600');
    await wrapper.get('[data-testid="chunk-overlap-tokens"]').setValue('80');
    await wrapper.get('[data-testid="chunk-maximum-tokens"]').setValue('800');
    await wrapper.get('[data-testid="chunk-separator"]').setValue('\\n---\\n');
    await wrapper.get('[data-testid="generate-previews"]').trigger('click');
    await flushPromises();

    expect(api.generatePreviews).toHaveBeenCalledWith('version-1', 3, {
      kind: 'separator',
      targetTokens: 600,
      overlapTokens: 80,
      maximumTokens: 800,
      separator: '\n---\n'
    });
  });

  it('confirms and deletes an individual preview using the current revision', async () => {
    const api = createApi();
    const confirmAction = vi.fn().mockResolvedValue(true);
    const wrapper = mount(DocumentDetailView, {
      props: {
        documentId: 'document-1',
        versionId: 'version-1',
        api,
        tagApi: { options: vi.fn().mockResolvedValue([]) },
        confirmAction
      }
    });
    await flushPromises();

    await wrapper.get('[data-testid="delete-preview-1"]').trigger('click');
    await flushPromises();

    expect(confirmAction).toHaveBeenCalledWith('确认删除第 1 段预览？删除后需要重新审核分段。');
    expect(api.deletePreview).toHaveBeenCalledWith('version-1', 'preview-1', 3);
  });

  it('locks preview mutations when the exact version is indexing', async () => {
    const api = createApi();
    const workbench = await api.getWorkbench('document-1', 'version-1');
    vi.mocked(api.getWorkbench).mockResolvedValue({
      ...workbench,
      version: { ...workbench.version, status: 'indexing' }
    });
    vi.mocked(api.getWorkbench).mockClear();
    vi.mocked(api.getDocumentVersions).mockResolvedValue([
      {
        id: 'version-1', version: 1, originalFileName: 'source.docx', safeFileName: 'source.docx',
        contentType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', sizeBytes: 1,
        status: 'indexing', failureReason: null, isPublished: false, hasPublicObject: true,
        previewRevision: 3, previewCount: 1, approvedChunkCount: 1, ocrPageCount: 0, ocrFailedPageCount: 0,
        sourceKind: 'DocumentUpload', sourceActorDisplayName: '系统管理员', tags: [],
        uploadAndParseJobs: [], indexJobs: [], createdAtUtc: '', updatedAtUtc: ''
      }
    ]);
    const wrapper = mount(DocumentDetailView, {
      props: {
        documentId: 'document-1', versionId: 'version-1', api,
        tagApi: { options: vi.fn().mockResolvedValue([]) }
      }
    });
    await flushPromises();

    expect(wrapper.text()).toContain('分段内容已锁定');
    expect(wrapper.find('[data-testid="generate-previews"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="edit-preview-1"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="delete-preview-1"]').exists()).toBe(false);
  });

  it('preselects persisted tags and keeps automatic-source content read-only while allowing reindex', async () => {
    const api = createApi();
    vi.mocked(api.getWorkbench).mockResolvedValue({
      documentId: 'document-1',
      documentTitle: '签证知识',
      documentStatus: 'active',
      documentStateVersion: 5,
      activeVersionId: 'version-1',
      version: {
        id: 'version-1', version: 1, status: 'active', isPublished: true,
        sourceKind: 'PrivateChatDirect', sourceActorDisplayName: '张伟',
        sourceBatchId: null, changeKind: 'New', supersedesVersionId: null,
        tags: [{ id: 'tag-bound', name: '加拿大签证' }],
        indexJobs: [], createdAtUtc: '2026-07-30T01:00:00Z',
        updatedAtUtc: '2026-07-30T01:00:00Z'
      },
      chunks: [{
        id: 'chunk-1', sequence: 0, text: '签证批准后会立即通知。',
        pageNumber: null, question: '签证多久出？', synonyms: ['签证出了吗'],
        answer: '签证批准后会立即通知。', status: 'approved'
      }],
      sourceEvidence: {
        channelType: 'PrivateChat', roomType: 2, actorDisplayName: '张伟',
        text: '加拿大签证批准后会立即通知。', receivedAtUtc: '2026-07-30T00:59:00Z'
      },
      sourceEvidenceUnavailableReason: null,
      editableRevision: null,
      canCreateRevision: true
    });
    vi.mocked(api.getDocumentVersions).mockResolvedValue([
      {
        id: 'version-1', version: 1, originalFileName: '私聊入库', safeFileName: 'private-chat.txt',
        contentType: 'text/plain', sizeBytes: 12, status: 'active', failureReason: null,
        isPublished: true, hasPublicObject: false, previewRevision: 3, previewCount: 1,
        approvedChunkCount: 1, ocrPageCount: 0, ocrFailedPageCount: 0,
        sourceKind: 'PrivateChatDirect', sourceActorDisplayName: '张伟',
        changeKind: 'New', tags: [{ id: 'tag-bound', name: '加拿大签证' }],
        uploadAndParseJobs: [], indexJobs: [], createdAtUtc: '', updatedAtUtc: ''
      }
    ]);
    vi.mocked(api.getIndexStatus).mockResolvedValue({
      documentId: 'document-1',
      activeVersionId: 'version-1',
      documentStatus: 'active',
      approvedChunkCount: 1,
      consistency: 'consistent',
      driftDetails: [],
      jobs: []
    });
    vi.mocked(api.queueIndex).mockResolvedValue({ jobId: 'job-1' });
    const wrapper = mount(DocumentDetailView, {
      props: {
        documentId: 'document-1',
        versionId: 'version-1',
        api,
        tagApi: {
          options: vi.fn().mockResolvedValue([
            { id: 'tag-bound', name: '加拿大签证', isGlobalPublic: false },
            { id: 'tag-other', name: '澳洲签证', isGlobalPublic: false }
          ])
        }
      }
    });
    await flushPromises();

    expect(wrapper.text()).toContain('入库内容与索引');
    expect(wrapper.text()).toContain('私聊直接入库');
    expect(wrapper.text()).toContain('张伟');
    expect(wrapper.text()).toContain('签证批准后会立即通知。');
    expect(api.getPreviews).not.toHaveBeenCalled();
    expect(wrapper.get('[data-testid="knowledge-tag-tag-bound"]')
      .attributes('checked')).toBeDefined();
    expect(wrapper.find('[data-testid="text-chunk-1"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="generate-previews"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="edit-preview-1"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="delete-preview-1"]').exists()).toBe(false);

    await wrapper.get('[data-testid="queue-index"]').trigger('click');
    await flushPromises();
    expect(api.queueIndex).toHaveBeenCalledWith(
      'document-1',
      'version-1',
      ['tag-bound'],
      true
    );
  });

  it('shows source evidence, version history, and an explicit missing-evidence state', async () => {
    const api = createApi();
    vi.mocked(api.getWorkbench).mockResolvedValue({
      documentId: 'document-1',
      documentTitle: '审核知识',
      documentStatus: 'active',
      documentStateVersion: 2,
      activeVersionId: 'version-1',
      version: {
        id: 'version-1', version: 1, status: 'active', isPublished: true,
        sourceKind: 'ConversationReview', sourceActorDisplayName: '李四',
        sourceBatchId: null, changeKind: 'New', supersedesVersionId: null,
        tags: [], indexJobs: [], createdAtUtc: '', updatedAtUtc: ''
      },
      chunks: [{
        id: 'chunk-1', sequence: 0, text: '审核后的答案', pageNumber: null,
        question: null, synonyms: [], answer: null, status: 'approved'
      }],
      sourceEvidence: null,
      sourceEvidenceUnavailableReason: 'source-message-missing',
      editableRevision: null,
      canCreateRevision: true
    });
    const wrapper = mount(DocumentDetailView, {
      props: {
        documentId: 'document-1', versionId: 'version-1', api,
        tagApi: { options: vi.fn().mockResolvedValue([]) }
      }
    });
    await flushPromises();

    await wrapper.get('[data-testid="tab-source"]').trigger('click');
    await flushPromises();
    expect(wrapper.text()).toContain('历史来源证据不完整');

    await wrapper.get('[data-testid="tab-history"]').trigger('click');
    await flushPromises();
    expect(wrapper.text()).toContain('source.docx');
  });

  it('creates a revision and routes to the editable version', async () => {
    const api = createApi();
    vi.mocked(api.getWorkbench).mockResolvedValue({
      documentId: 'document-1', documentTitle: '私聊知识',
      documentStatus: 'active', documentStateVersion: 6,
      activeVersionId: 'version-1',
      version: {
        id: 'version-1', version: 1, status: 'active', isPublished: true,
        sourceKind: 'PrivateChatDirect', sourceActorDisplayName: '张伟',
        sourceBatchId: null, changeKind: 'New', supersedesVersionId: null,
        tags: [], indexJobs: [], createdAtUtc: '', updatedAtUtc: ''
      },
      chunks: [{
        id: 'chunk-1', sequence: 0, text: '已批准正文', pageNumber: null,
        question: null, synonyms: [], answer: null, status: 'approved'
      }],
      sourceEvidence: null,
      sourceEvidenceUnavailableReason: 'source-message-missing',
      editableRevision: null,
      canCreateRevision: true
    });
    const navigate = vi.fn();
    const confirmAction = vi.fn().mockResolvedValue(true);
    const wrapper = mount(DocumentDetailView, {
      props: {
        documentId: 'document-1', versionId: 'version-1', api, navigate,
        confirmAction, tagApi: { options: vi.fn().mockResolvedValue([]) }
      }
    });
    await flushPromises();

    await wrapper.get('[data-testid="create-revision"]').trigger('click');
    await flushPromises();

    expect(api.createRevision).toHaveBeenCalledWith('document-1', 'version-1', 6);
    expect(navigate).toHaveBeenCalledWith(
      '/knowledge/documents/document-1/versions/revision-2'
    );
  });

  it('continues an existing revision without creating another one', async () => {
    const api = createApi();
    const base = await api.getWorkbench('document-1', 'version-1');
    vi.mocked(api.getWorkbench).mockResolvedValue({
      ...base,
      version: { ...base.version, status: 'active' },
      editableRevision: { versionId: 'revision-3', version: 3, previewRevision: 2 },
      canCreateRevision: false
    });
    vi.mocked(api.getWorkbench).mockClear();
    const navigate = vi.fn();
    const wrapper = mount(DocumentDetailView, {
      props: {
        documentId: 'document-1', versionId: 'version-1', api, navigate,
        tagApi: { options: vi.fn().mockResolvedValue([]) }
      }
    });
    await flushPromises();

    await wrapper.get('[data-testid="continue-revision"]').trigger('click');

    expect(api.createRevision).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(
      '/knowledge/documents/document-1/versions/revision-3'
    );
  });

  it('allows administrator revision editing without source regeneration', async () => {
    const api = createApi();
    const base = await api.getWorkbench('document-1', 'version-1');
    vi.mocked(api.getWorkbench).mockResolvedValue({
      ...base,
      version: {
        ...base.version,
        sourceKind: 'AdministrationRevision',
        status: 'preview',
        changeKind: 'Correction',
        supersedesVersionId: 'version-1'
      }
    });
    vi.mocked(api.getWorkbench).mockClear();
    const wrapper = mount(DocumentDetailView, {
      props: {
        documentId: 'document-1', versionId: 'version-1', api,
        tagApi: { options: vi.fn().mockResolvedValue([]) }
      }
    });
    await flushPromises();

    expect(wrapper.text()).toContain('管理员修订');
    expect(wrapper.find('[data-testid="generate-previews"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="edit-preview-1"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="approve-previews"]').exists()).toBe(true);
  });

  it('distinguishes unchanged and changed tag reindex actions and blocks duplicate jobs', async () => {
    const api = createApi();
    const base = await api.getWorkbench('document-1', 'version-1');
    vi.mocked(api.getWorkbench).mockResolvedValue({
      ...base,
      documentStatus: 'active',
      activeVersionId: 'version-1',
      version: {
        ...base.version,
        status: 'active',
        tags: [{ id: 'tag-bound', name: '加拿大签证' }]
      }
    });
    vi.mocked(api.getIndexStatus).mockResolvedValue({
      documentId: 'document-1',
      activeVersionId: 'version-1',
      documentStatus: 'active',
      approvedChunkCount: 1,
      consistency: 'consistent',
      driftDetails: [],
      jobs: []
    });
    const wrapper = mount(DocumentDetailView, {
      props: {
        documentId: 'document-1', versionId: 'version-1', api,
        tagApi: {
          options: vi.fn().mockResolvedValue([
            { id: 'tag-bound', name: '加拿大签证', isGlobalPublic: false },
            { id: 'tag-new', name: '澳洲签证', isGlobalPublic: false }
          ])
        }
      }
    });
    await flushPromises();

    expect(wrapper.get('[data-testid="queue-index"]').text())
      .toContain('重新索引当前版本');
    await wrapper.get('[data-testid="knowledge-tag-tag-new"]').setValue(true);
    expect(wrapper.get('[data-testid="queue-index"]').text())
      .toContain('保存标签并重新索引');

    vi.mocked(api.getIndexStatus).mockResolvedValue({
      documentId: 'document-1',
      activeVersionId: 'version-1',
      documentStatus: 'active',
      approvedChunkCount: 1,
      consistency: 'consistent',
      driftDetails: [],
      jobs: [{
        id: 'job-running', versionId: 'version-1', operation: 'reindex',
        status: 'leased', attemptCount: 0
      }]
    });
    await wrapper.get('button').trigger('click');
    await flushPromises();
    expect(wrapper.text()).toContain('索引任务正在处理中');
    expect(wrapper.get('[data-testid="queue-index"]').attributes('disabled'))
      .toBeDefined();
  });
});
