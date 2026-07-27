import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiClient = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn()
}));
vi.mock('./http', () => ({ apiClient }));

import { handoffApi } from './handoffs';

describe('handoffApi assignee options', () => {
  beforeEach(() => vi.clearAllMocks());

  it('loads assignable backend users', async () => {
    apiClient.get.mockResolvedValueOnce({
      data: [{
        id: 'user-1',
        displayName: '客服甲',
        email: 'agent@example.test',
        roles: ['HumanAgent'],
        isEnabled: true
      }]
    });

    await expect(handoffApi.assignees()).resolves.toEqual([
      expect.objectContaining({ id: 'user-1', displayName: '客服甲' })
    ]);
    expect(apiClient.get).toHaveBeenCalledWith('/api/handoffs/assignees');
  });
});
