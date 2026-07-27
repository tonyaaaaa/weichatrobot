import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiClient = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn()
}));
vi.mock('./http', () => ({ apiClient }));

import { workToolOperationsApi } from './worktool';

describe('workToolOperationsApi audit scope', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    apiClient.get.mockResolvedValue({ data: {} });
  });

  it('fetches the documented scope from the backend', async () => {
    await workToolOperationsApi.getAuditScope();
    expect(apiClient.get).toHaveBeenCalledWith(
      '/api/admin/worktool/group-operations/audit-scope');
  });

  it('loads robot options for group forms', async () => {
    apiClient.get.mockResolvedValueOnce({
      data: [{ id: 'robot-1', name: '客服机器人', isEnabled: true }]
    });

    await expect(workToolOperationsApi.listRobots()).resolves.toEqual([
      expect.objectContaining({ id: 'robot-1', name: '客服机器人' })
    ]);
    expect(apiClient.get).toHaveBeenCalledWith('/api/admin/worktool/robots');
  });
});
