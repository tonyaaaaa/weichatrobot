import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from './http';
import { groupOptionApi } from './groupOptions';

vi.mock('./http', () => ({
  apiClient: { get: vi.fn() }
}));

describe('groupOptionApi', () => {
  beforeEach(() => vi.clearAllMocks());

  it('loads the shared authenticated group options route', async () => {
    vi.mocked(apiClient.get).mockResolvedValue({ data: [{ id: 'group-1', name: '技术支持群' }] });

    await expect(groupOptionApi.list()).resolves.toEqual([{ id: 'group-1', name: '技术支持群' }]);

    expect(apiClient.get).toHaveBeenCalledWith('/api/group-options');
  });
});
