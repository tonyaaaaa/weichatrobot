import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { KnowledgeApi, KnowledgeDocumentPage, KnowledgeDocumentSummary } from '../../api/knowledge';
import KnowledgeDocumentsView from './KnowledgeDocumentsView.vue';

const failedDocument: KnowledgeDocumentSummary = {
  id: '11111111-1111-1111-1111-111111111111',
  title: '产品手册.pdf',
  status: 'failed',
  stateVersion: 4,
  activeVersionId: null,
  versionCount: 2,
  latestVersionId: '22222222-2222-2222-2222-222222222222',
  latestVersion: 2,
  latestVersionStatus: 'failed',
  latestFailureReason: 'Object storage upload failed; retry is available.',
  canRetryUpload: true,
  createdAtUtc: '2026-07-24T00:00:00Z',
  updatedAtUtc: '2026-07-25T00:00:00Z'
};

const activeDocument: KnowledgeDocumentSummary = {
  ...failedDocument,
  id: '33333333-3333-3333-3333-333333333333',
  title: '售后知识.md',
  status: 'active',
  stateVersion: 8,
  activeVersionId: '44444444-4444-4444-4444-444444444444',
  latestVersionId: '44444444-4444-4444-4444-444444444444',
  latestVersion: 1,
  latestVersionStatus: 'active',
  latestFailureReason: null,
  canRetryUpload: false,
  versionCount: 1
};

function page(items = [failedDocument, activeDocument]): KnowledgeDocumentPage {
  return { items, total: items.length, page: 1, pageSize: 20 };
}

function createApi() {
  return {
    upload: vi.fn(),
    listDocuments: vi.fn().mockResolvedValue(page()),
    getDocument: vi.fn(),
    getDocumentVersions: vi.fn(),
    retryDocumentUpload: vi.fn().mockResolvedValue({
      documentId: failedDocument.id,
      versionId: failedDocument.latestVersionId,
      version: 2,
      state: 'uploaded',
      safeFileName: 'source.pdf',
      publicUrl: 'https://public.example.test/source.pdf',
      publicReadWarning: '公共读 OSS'
    }),
    disableDocument: vi.fn(),
    requestPhysicalDelete: vi.fn(),
    getPreviews: vi.fn(),
    generatePreviews: vi.fn(),
    editPreview: vi.fn(),
    splitPreview: vi.fn(),
    mergePreviews: vi.fn(),
    approvePreviews: vi.fn(),
    getIndexStatus: vi.fn(),
    queueIndex: vi.fn(),
    retryIndex: vi.fn()
  } satisfies KnowledgeApi;
}

describe('KnowledgeDocumentsView', () => {
  beforeEach(() => vi.clearAllMocks());

  it('loads persisted documents and exposes retry only from server retryability', async () => {
    const api = createApi();
    const wrapper = mount(KnowledgeDocumentsView, { props: { api } });
    await flushPromises();

    expect(api.listDocuments).toHaveBeenCalledWith({
      query: '',
      status: '',
      page: 1,
      pageSize: 20
    });
    expect(wrapper.get(`[data-testid="document-row-${failedDocument.id}"]`).text())
      .toContain('产品手册.pdf');
    expect(wrapper.get(`[data-testid="document-row-${failedDocument.id}"]`).text())
      .toContain('Object storage upload failed');
    expect(wrapper.get(`[data-testid="open-document-${failedDocument.id}"]`).attributes('href'))
      .toBe(`/knowledge/documents/${failedDocument.id}`);
    expect(wrapper.find(`[data-testid="retry-document-${failedDocument.id}"]`).exists()).toBe(true);
    expect(wrapper.find(`[data-testid="retry-document-${activeDocument.id}"]`).exists()).toBe(false);
  });

  it('applies search and exact persisted status filters from page one', async () => {
    const api = createApi();
    const wrapper = mount(KnowledgeDocumentsView, { props: { api } });
    await flushPromises();

    await wrapper.get('[data-testid="document-query-filter"]').setValue('产品');
    await wrapper.get('[data-testid="document-status-filter"]').setValue('failed');
    await wrapper.get('[data-testid="apply-document-filters"]').trigger('click');
    await flushPromises();

    expect(api.listDocuments).toHaveBeenLastCalledWith({
      query: '产品',
      status: 'failed',
      page: 1,
      pageSize: 20
    });
  });

  it('retries with the row state version and refreshes persisted truth', async () => {
    const api = createApi();
    const wrapper = mount(KnowledgeDocumentsView, { props: { api } });
    await flushPromises();

    await wrapper.get(`[data-testid="retry-document-${failedDocument.id}"]`).trigger('click');
    await flushPromises();

    expect(api.retryDocumentUpload).toHaveBeenCalledWith(failedDocument.id, 4);
    expect(api.listDocuments).toHaveBeenCalledTimes(2);
    expect(wrapper.text()).toContain('已重新提交并刷新文档状态');
  });

  it('merges a concurrency response into the stale row without hiding list errors', async () => {
    const api = createApi();
    api.retryDocumentUpload.mockRejectedValue({
      response: {
        status: 409,
        data: {
          error: 'document-concurrency-conflict',
          current: {
            id: failedDocument.id,
            status: 'disabled',
            stateVersion: 5
          }
        }
      }
    });
    const wrapper = mount(KnowledgeDocumentsView, { props: { api } });
    await flushPromises();

    await wrapper.get(`[data-testid="retry-document-${failedDocument.id}"]`).trigger('click');
    await flushPromises();

    const row = wrapper.get(`[data-testid="document-row-${failedDocument.id}"]`);
    expect(row.text()).toContain('disabled');
    expect(row.find(`[data-testid="retry-document-${failedDocument.id}"]`).exists()).toBe(false);
    expect(wrapper.get('[data-testid="document-list-notice"]').text())
      .toContain('已被其他操作员修改');
  });
});
