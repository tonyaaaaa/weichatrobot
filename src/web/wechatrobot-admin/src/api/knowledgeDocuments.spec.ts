import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiClient = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn()
}));

vi.mock('./http', () => ({ apiClient }));

import {
  knowledgeApi,
  type ChunkPolicy,
  type KnowledgeDocumentDetail,
  type KnowledgeDocumentPage
} from './knowledge';

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
      sourceKind: 'PrivateChatDirect',
      tagId: 'tag-id',
      page: 2,
      pageSize: 25
    })).resolves.toEqual(page);
    expect(apiClient.get).toHaveBeenCalledWith('/api/knowledge/documents', {
      params: {
        query: '产品',
        status: 'failed',
        sourceKind: 'PrivateChatDirect',
        tagId: 'tag-id',
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

  it('uses encoded workbench and revision routes', async () => {
    const versionId = 'version/unsafe';

    await knowledgeApi.getWorkbench(documentId, versionId);
    expect(apiClient.get).toHaveBeenCalledWith(
      `/api/knowledge/documents/${encodedDocumentId}/versions/version%2Funsafe/workbench`
    );

    await knowledgeApi.createRevision(documentId, versionId, 9);
    expect(apiClient.post).toHaveBeenCalledWith(
      `/api/knowledge/documents/${encodedDocumentId}/versions/version%2Funsafe/revisions`,
      { expectedDocumentStateVersion: 9 }
    );
  });

  it('adds documentId only when uploading a new version', async () => {
    const file = new File(['version content'], '产品手册-v2.pdf', {
      type: 'application/pdf'
    });

    await knowledgeApi.upload(file, vi.fn());
    const newDocumentForm = apiClient.post.mock.calls[0]?.[1] as FormData;
    expect(newDocumentForm.get('file')).toBe(file);
    expect(newDocumentForm.has('documentId')).toBe(false);

    await knowledgeApi.upload(file, vi.fn(), documentId);
    const newVersionForm = apiClient.post.mock.calls[1]?.[1] as FormData;
    expect(newVersionForm.get('file')).toBe(file);
    expect(newVersionForm.get('documentId')).toBe(documentId);
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
        isDeleteRequested: false,
        canRetryPhysicalDelete: false,
        sourceKind: 'DocumentUpload',
        sourceActorDisplayName: '系统管理员',
        tags: [],
        createdAtUtc: '2026-07-25T00:00:00Z',
        updatedAtUtc: '2026-07-25T00:01:00Z'
      },
      versions: []
    };

    const serialized = JSON.stringify(detail);
    expect(serialized).not.toMatch(/stagedContent|objectKey|payloadJson|authorization|credential|secret/i);
  });

  it('sends a discriminated chunk policy and an encoded preview delete request', async () => {
    const policy: ChunkPolicy = {
      kind: 'separator',
      targetTokens: 600,
      overlapTokens: 80,
      maximumTokens: 800,
      separator: '\n---\n'
    };
    await knowledgeApi.generatePreviews('version/unsafe', 7, policy);
    expect(apiClient.post).toHaveBeenCalledWith(
      '/api/knowledge/versions/version%2Funsafe/previews/generate',
      { expectedRevision: 7, policy }
    );

    await knowledgeApi.deletePreview('version/unsafe', 'preview/unsafe', 8);
    expect(apiClient.delete).toHaveBeenCalledWith(
      '/api/knowledge/versions/version%2Funsafe/previews/preview%2Funsafe',
      { params: { expectedRevision: 8 } }
    );
  });
});
