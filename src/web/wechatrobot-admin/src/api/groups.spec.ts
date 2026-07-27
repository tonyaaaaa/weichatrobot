import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiClient = vi.hoisted(() => ({
  get: vi.fn(),
  put: vi.fn(),
  post: vi.fn()
}));
vi.mock('./http', () => ({ apiClient }));

import { groupApi } from './groups';

describe('groupApi concurrency contract', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    for (const method of Object.values(apiClient)) method.mockResolvedValue({ data: {} });
  });

  it('sends the loaded configuration version on every update', async () => {
    await groupApi.updateConfiguration('group/unsafe', {
      includeRules: [],
      excludeRules: [],
      boundTagIds: [],
      context: {},
      clearContext: true,
      expectedConfigurationVersion: 7
    });

    expect(apiClient.put).toHaveBeenCalledWith(
      '/api/groups/group%2Funsafe/configuration',
      expect.objectContaining({
        clearContext: true,
        expectedConfigurationVersion: 7
      })
    );
  });
});
