import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiClient = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn()
}));

vi.mock('./http', () => ({ apiClient }));

import { knowledgeApi, type KnowledgeDocumentDetail, type KnowledgeDocumentPage } from './knowledge';

const documentId = 'document/unsafe';
const encodedDocumentId = 'document%2Funsafe';

describe('knowledgeApi document administration', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    for (const method of Object.values(apiClient)) {
      method.mockResolvedValue({ data: {} });
    }
  });

  it('uses the persisted document query contracts and encoded IDs', async () => {
    const page: KnowledgeDocumentPage = {
      items: [],
      total: 0,
      page: 2,
      pageSize: 25
    };
    apiClient.get.mockResolvedValueOnce({ data: page });

    await expect(knowledgeApi.listDocuments({
      query: '产品',
      status: 'failed',
      page: 2,
      pageSize: 25
    })).resolves.toEqual(page);
    expect(apiClient.get).toHaveBeenCalledWith('/api/knowledge/documents', {
      params: {
        query: '产品',
        status: 'failed',
        page: 2,
        pageSize: 25
      }
    });

    await knowledgeApi.getDocument(documentId);
    expect(apiClient.get).toHaveBeenLastCalledWith(
      `/api/knowledge/documents/${encodedDocumentId}`
    );

    await knowledgeApi.getDocumentVersions(documentId);
    expect(apiClient.get).toHaveBeenLastCalledWith(
      `/api/knowledge/documents/${encodedDocumentId}/versions`
    );
  });

  it('places expectedStateVersion only in the documented body or query', async () => {
    await knowledgeApi.retryDocumentUpload(documentId, 4);
    expect(apiClient.post).toHaveBeenCalledWith(
      `/api/knowledge/documents/${encodedDocumentId}/retry-upload`,
      { expectedStateVersion: 4 }
    );

    await knowledgeApi.disableDocument(documentId, 5);
    expect(apiClient.post).toHaveBeenCalledWith(
      `/api/knowledge/documents/${encodedDocumentId}/disable`,
      { expectedStateVersion: 5 }
    );

    await knowledgeApi.requestPhysicalDelete(documentId, 6);
    expect(apiClient.delete).toHaveBeenCalledWith(
      `/api/knowledge/documents/${encodedDocumentId}/physical`,
      { params: { expectedStateVersion: 6 } }
    );
  });

  it('models safe document administration responses without secret-bearing fields', () => {
    const detail: KnowledgeDocumentDetail = {
      document: {
        id: '11111111-1111-1111-1111-111111111111',
        title: '产品文档',
        status: 'failed',
        stateVersion: 3,
        activeVersionId: null,
        versionCount: 1,
        latestVersionId: '22222222-2222-2222-2222-222222222222',
        latestVersion: 1,
        latestVersionStatus: 'failed',
        latestFailureReason: 'Object storage upload failed; retry is available.',
        canRetryUpload: true,
        createdAtUtc: '2026-07-25T00:00:00Z',
        updatedAtUtc: '2026-07-25T00:01:00Z'
      },
      versions: []
    };

    const serialized = JSON.stringify(detail);
    expect(serialized).not.toMatch(/stagedContent|objectKey|payloadJson|authorization|credential|secret/i);
  });
});
