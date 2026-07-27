import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiClient = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn()
}));
vi.mock('./http', () => ({ apiClient }));
import { robotApi } from './robots';

describe('robotApi', () => {
  beforeEach(() => {
    for (const method of Object.values(apiClient)) method.mockReset().mockResolvedValue({ data: {} });
  });

  it('uses the complete WorkTool robot administration endpoints', async () => {
    const id = 'robot/id';
    await robotApi.list();
    await robotApi.save(id, { name: '客服机器人', isEnabled: false, sendRateLimitPerMinute: 40 });
    await robotApi.probe(id);
    await robotApi.configureMessageCallback(id, 'https://wxrobot.aavisa.com', true);
    await robotApi.configureCommandResultCallback(id, 'https://wxrobot.aavisa.com');
    await robotApi.getCallbacks(id);

    const encoded = encodeURIComponent(id);
    expect(apiClient.get).toHaveBeenNthCalledWith(1, '/api/admin/worktool/robots');
    expect(apiClient.put).toHaveBeenCalledWith(`/api/admin/worktool/robots/${encoded}`, {
      name: '客服机器人', isEnabled: false, sendRateLimitPerMinute: 40
    });
    expect(apiClient.post).toHaveBeenCalledWith(`/api/admin/worktool/robots/${encoded}/test-connection`);
    expect(apiClient.post).toHaveBeenCalledWith(
      `/api/admin/worktool/robots/${encoded}/message-callback/configure`,
      { publicBaseUrl: 'https://wxrobot.aavisa.com', replyAll: true }
    );
    expect(apiClient.post).toHaveBeenCalledWith(
      `/api/admin/worktool/robots/${encoded}/command-result-callback/configure`,
      { publicBaseUrl: 'https://wxrobot.aavisa.com' }
    );
    expect(apiClient.get).toHaveBeenNthCalledWith(2, `/api/admin/worktool/robots/${encoded}/callbacks`);
  });
});
