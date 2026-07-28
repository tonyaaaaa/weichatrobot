import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiClient = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }));
vi.mock('./http', () => ({ apiClient }));

import { memoryApi } from './memory';

describe('memoryApi', () => {
  beforeEach(() => vi.clearAllMocks());

  it('passes the group filter to each bounded memory list', async () => {
    apiClient.get.mockResolvedValue({ data: { items: [], total: 0, page: 1, pageSize: 20 } });
    const query = { groupProfileId: 'group-1', status: 'active', page: 1, pageSize: 20 };

    await memoryApi.listCandidates(query);
    await memoryApi.listEntries(query);
    await memoryApi.listJobs(query);

    expect(apiClient.get).toHaveBeenNthCalledWith(1, '/api/admin/memory/candidates', {
      params: expect.objectContaining({ groupProfileId: 'group-1', status: 'active', page: 1, pageSize: 20 })
    });
    expect(apiClient.get).toHaveBeenNthCalledWith(2, '/api/admin/memory/entries', expect.anything());
    expect(apiClient.get).toHaveBeenNthCalledWith(3, '/api/admin/memory/jobs', expect.anything());
  });

  it('uses expected versions for important mutations', async () => {
    apiClient.post.mockResolvedValue({ data: {} });

    await memoryApi.promoteCandidate('candidate-1', 3);
    await memoryApi.forgetEntry('entry-1', 5);
    await memoryApi.retryJob('job-1', 7);

    expect(apiClient.post).toHaveBeenNthCalledWith(
      1, '/api/admin/memory/candidates/candidate-1/promote', { expectedVersion: 3 });
    expect(apiClient.post).toHaveBeenNthCalledWith(
      2, '/api/admin/memory/entries/entry-1/forget', { expectedVersion: 5 });
    expect(apiClient.post).toHaveBeenNthCalledWith(
      3, '/api/admin/memory/jobs/job-1/retry', { expectedVersion: 7 });
  });
});
