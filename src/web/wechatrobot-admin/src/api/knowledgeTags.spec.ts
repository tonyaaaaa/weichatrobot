import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiClient = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  patch: vi.fn(),
  delete: vi.fn()
}));

vi.mock('./http', () => ({ apiClient }));

import { knowledgeTagApi } from './knowledgeTags';

describe('knowledgeTagApi', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    for (const method of Object.values(apiClient)) {
      method.mockResolvedValue({ data: {} });
    }
  });

  it('maps UI filters to the actual backend query contract', async () => {
    await knowledgeTagApi.list({
      q: '产品',
      state: 'disabled',
      global: 'global',
      page: 2,
      pageSize: 25
    });

    expect(apiClient.get).toHaveBeenCalledWith('/api/knowledge/tags', {
      params: {
        query: '产品',
        isEnabled: false,
        isGlobalPublic: true,
        page: 2,
        pageSize: 25
      }
    });

    await knowledgeTagApi.list({
      state: 'all',
      global: 'all',
      page: 1,
      pageSize: 20
    });
    expect(apiClient.get).toHaveBeenLastCalledWith('/api/knowledge/tags', {
      params: {
        query: undefined,
        isEnabled: undefined,
        isGlobalPublic: undefined,
        page: 1,
        pageSize: 20
      }
    });
  });

  it('uses versioned mutation routes', async () => {
    const id = 'tag/unsafe';
    await knowledgeTagApi.setEnabled(id, { isEnabled: false, expectedVersion: 4 });
    expect(apiClient.patch).toHaveBeenCalledWith(
      '/api/knowledge/tags/tag%2Funsafe/enabled',
      { isEnabled: false, expectedVersion: 4 }
    );

    await knowledgeTagApi.delete(id, 5);
    expect(apiClient.delete).toHaveBeenCalledWith(
      '/api/knowledge/tags/tag%2Funsafe',
      { params: { expectedVersion: 5 } }
    );
  });
});
