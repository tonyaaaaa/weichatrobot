import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import type { KnowledgeApi } from '../../api/knowledge';
import DocumentDetailView from './DocumentDetailView.vue';

function createApi(): KnowledgeApi {
  return {
    upload: vi.fn(),
    listDocuments: vi.fn(),
    getDocument: vi.fn(),
    getDocumentVersions: vi.fn(),
    retryDocumentUpload: vi.fn(),
    disableDocument: vi.fn(),
    requestPhysicalDelete: vi.fn(),
    getPreviews: vi.fn().mockResolvedValue({
      versionId: 'version-1',
      revision: 3,
      items: [{ id: 'preview-1', sequence: 1, text: '第一段' }]
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
    const wrapper = mount(DocumentDetailView, {
      props: {
        documentId: 'document-1',
        versionId: 'version-1',
        api,
        tagApi: { options: vi.fn().mockResolvedValue([]) }
      }
    });
    await flushPromises();

    await wrapper.get('[data-testid="chunk-policy-kind"]').setValue('separator');
    await wrapper.get('[data-testid="chunk-target-tokens"]').setValue('600');
    await wrapper.get('[data-testid="chunk-overlap-tokens"]').setValue('80');
    await wrapper.get('[data-testid="chunk-maximum-tokens"]').setValue('800');
    await wrapper.get('[data-testid="chunk-separator"]').setValue('\\n---\\n');
    vi.spyOn(window, 'confirm').mockReturnValueOnce(true);
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
    const wrapper = mount(DocumentDetailView, {
      props: {
        documentId: 'document-1',
        versionId: 'version-1',
        api,
        tagApi: { options: vi.fn().mockResolvedValue([]) }
      }
    });
    await flushPromises();
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(true);

    await wrapper.get('[data-testid="delete-preview-1"]').trigger('click');
    await flushPromises();

    expect(confirm).toHaveBeenCalledWith('确认删除第 1 段预览？删除后需要重新审核分段。');
    expect(api.deletePreview).toHaveBeenCalledWith('version-1', 'preview-1', 3);
  });
});
