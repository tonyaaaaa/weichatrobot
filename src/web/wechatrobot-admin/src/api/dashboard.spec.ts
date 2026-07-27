import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiClient = vi.hoisted(() => ({
  get: vi.fn()
}));

vi.mock('./http', () => ({ apiClient }));

import { dashboardApi } from './dashboard';

describe('dashboardApi', () => {
  beforeEach(() => {
    apiClient.get.mockReset();
  });

  it('loads the single administration summary endpoint', async () => {
    const summary = {
      checkedAtUtc: '2026-07-25T07:00:00Z',
      robots: {},
      knowledge: {},
      operations: {},
      readiness: {}
    };
    apiClient.get.mockResolvedValue({ data: summary });

    await expect(dashboardApi.getSummary()).resolves.toBe(summary);
    expect(apiClient.get).toHaveBeenCalledWith('/api/admin/dashboard/summary');
  });
});
