import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiClient = vi.hoisted(() => ({ get: vi.fn() }));
vi.mock('./http', () => ({ apiClient }));

import { administrationAuditApi } from './administrationAudit';

describe('administrationAuditApi', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    apiClient.get.mockResolvedValue({ data: { items: [], total: 0, page: 1, pageSize: 20 } });
  });

  it('sends operational and UTC filters to the admin-only endpoint', async () => {
    await administrationAuditApi.list({
      actor: 'admin',
      action: 'user_created',
      targetType: 'ApplicationUser',
      targetId: 'user-1',
      fromUtc: '2026-07-24T00:00:00.000Z',
      toUtc: '2026-07-25T00:00:00.000Z',
      page: 2,
      pageSize: 25
    });
    expect(apiClient.get).toHaveBeenCalledWith('/api/admin/administration-audits', {
      params: {
        actor: 'admin',
        action: 'user_created',
        targetType: 'ApplicationUser',
        targetId: 'user-1',
        fromUtc: '2026-07-24T00:00:00.000Z',
        toUtc: '2026-07-25T00:00:00.000Z',
        page: 2,
        pageSize: 25
      }
    });
  });

  it('loads bounded filter options with linked target search', async () => {
    apiClient.get.mockResolvedValueOnce({
      data: {
        actors: ['admin@example.test'],
        actions: ['user_created'],
        targetTypes: ['ApplicationUser'],
        targets: [{
          targetType: 'ApplicationUser',
          targetId: 'user-1',
          label: 'ApplicationUser · user-1'
        }]
      }
    });

    await administrationAuditApi.filterOptions('ApplicationUser', 'user');

    expect(apiClient.get).toHaveBeenCalledWith(
      '/api/admin/administration-audits/filter-options',
      { params: { targetType: 'ApplicationUser', q: 'user' } }
    );
  });
});
