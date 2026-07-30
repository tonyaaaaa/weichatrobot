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
    expect(wrapper.get('[data-testid="generate-previews"]').attributes('disabled')).toBeDefined();
    expect(wrapper.get('[data-testid="edit-preview-1"]').attributes('disabled')).toBeDefined();
    expect(wrapper.get('[data-testid="delete-preview-1"]').attributes('disabled')).toBeDefined();
  });

  it('preselects persisted tags and keeps automatic-source content read-only while allowing reindex', async () => {
    const api = createApi();
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
    expect(wrapper.get('[data-testid="knowledge-tag-tag-bound"]')
      .attributes('checked')).toBeDefined();
    expect(wrapper.get('[data-testid="text-preview-1"]').attributes('readonly')).toBeDefined();
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
});
