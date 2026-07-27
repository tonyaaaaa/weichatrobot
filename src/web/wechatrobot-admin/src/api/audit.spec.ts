import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiClient = vi.hoisted(() => ({ get: vi.fn() }));
vi.mock('./http', () => ({ apiClient }));

import { auditApi } from './audit';

describe('auditApi filters', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    apiClient.get.mockResolvedValue({
      data: { items: [], total: 0, page: 1, pageSize: 20 }
    });
  });

  it('sends group and inclusive-start exclusive-end UTC filters', async () => {
    await auditApi.capability({
      groupId: 'group-1',
      fromUtc: '2026-07-24T00:00:00.000Z',
      toUtc: '2026-07-25T00:00:00.000Z',
      page: 2,
      pageSize: 25
    });

    expect(apiClient.get).toHaveBeenCalledWith('/api/audit/conversations', {
      params: {
        groupId: 'group-1',
        fromUtc: '2026-07-24T00:00:00.000Z',
        toUtc: '2026-07-25T00:00:00.000Z',
        page: 2,
        pageSize: 25
      }
    });
  });

  it('loads readable group filter options', async () => {
    apiClient.get.mockResolvedValueOnce({
      data: [{
        id: 'group-1',
        name: '技术部',
        workToolGroupRemark: 'tech',
        robotName: '客服机器人',
        isEnabled: true
      }]
    });

    await expect(auditApi.groupOptions()).resolves.toEqual([
      expect.objectContaining({ id: 'group-1', name: '技术部' })
    ]);
    expect(apiClient.get).toHaveBeenCalledWith('/api/audit/group-options');
  });
});
