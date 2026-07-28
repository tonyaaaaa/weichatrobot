import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiClient = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn()
}));

vi.mock('./http', () => ({ apiClient }));

import { userAdministrationApi } from './users';

describe('userAdministrationApi', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    for (const method of Object.values(apiClient)) method.mockResolvedValue({ data: {} });
  });

  it('uses the administrator list and fixed-role contracts', async () => {
    await userAdministrationApi.list({
      q: ' agent ',
      state: 'disabled',
      page: 2,
      pageSize: 20
    });
    expect(apiClient.get).toHaveBeenCalledWith('/api/admin/users', {
      params: { q: 'agent', state: 'disabled', page: 2, pageSize: 20 }
    });

    await userAdministrationApi.roles();
    expect(apiClient.get).toHaveBeenLastCalledWith('/api/admin/users/roles');
  });

  it('keeps the temporary password write-only and encodes mutation identifiers', async () => {
    await userAdministrationApi.create({
      email: 'agent@example.test',
      displayName: 'Agent',
      temporaryPassword: 'Temporary1!Password',
      roles: ['KnowledgeOperator']
    });
    expect(apiClient.post).toHaveBeenCalledWith('/api/admin/users', {
      email: 'agent@example.test',
      displayName: 'Agent',
      temporaryPassword: 'Temporary1!Password',
      roles: ['KnowledgeOperator']
    });

    await userAdministrationApi.setEnabled('user/unsafe', false);
    expect(apiClient.put).toHaveBeenCalledWith(
      '/api/admin/users/user%2Funsafe/enabled',
      { isEnabled: false }
    );

    await userAdministrationApi.setRoles('user/unsafe', ['KnowledgeOperator']);
    expect(apiClient.put).toHaveBeenLastCalledWith(
      '/api/admin/users/user%2Funsafe/roles',
      { roles: ['KnowledgeOperator'] }
    );
  });
});
