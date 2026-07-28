import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiClient = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn()
}));
vi.mock('./http', () => ({ apiClient }));

import { sendCommandsApi } from './sendCommands';

describe('sendCommandsApi', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('queries the bounded send-command projection', async () => {
    apiClient.get.mockResolvedValueOnce({ data: { items: [], total: 0, page: 1, pageSize: 20 } });

    await sendCommandsApi.list({
      robotConfigId: 'robot-1',
      group: '测试群',
      status: 'pending',
      page: 1,
      pageSize: 20
    });

    expect(apiClient.get).toHaveBeenCalledWith(
      '/api/admin/operations/send-commands',
      { params: expect.objectContaining({ robotConfigId: 'robot-1', group: '测试群', status: 'pending' }) }
    );
  });

  it('uses versioned mutations for cancellation and unknown-delivery acknowledgement', async () => {
    apiClient.post.mockResolvedValue({ data: { id: 'command-1', status: 'cancelled', version: 4 } });

    await sendCommandsApi.cancel('command-1', 3);
    await sendCommandsApi.acknowledgeUnknown('command-2', 7);

    expect(apiClient.post).toHaveBeenNthCalledWith(
      1,
      '/api/admin/operations/send-commands/command-1/cancel',
      { expectedVersion: 3 }
    );
    expect(apiClient.post).toHaveBeenNthCalledWith(
      2,
      '/api/admin/operations/send-commands/command-2/acknowledge-unknown',
      { expectedVersion: 7 }
    );
  });
});
