import { beforeEach, describe, expect, it, vi } from 'vitest';

const apiClient = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  delete: vi.fn()
}));

vi.mock('./http', () => ({ apiClient }));

import { modelApi, type ModelConfigurationDraft } from './models';

const id = '11111111-1111-1111-1111-111111111111';
const draft: ModelConfigurationDraft = {
  name: 'Renamed',
  provider: 'OpenAI compatible',
  configurationType: 'chat',
  baseUrl: 'http://127.0.0.1:11434',
  model: 'qwen',
  apiKey: undefined,
  timeoutSeconds: 30,
  maxRetries: 0,
  version: 2
};

describe('modelApi', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    for (const method of Object.values(apiClient)) method.mockResolvedValue({ data: {} });
  });

  it('uses ID routes and complete concurrency payloads', async () => {
    await modelApi.create(draft);
    expect(apiClient.post).toHaveBeenCalledWith('/api/admin/model-configurations', draft);

    await modelApi.update(id, draft);
    expect(apiClient.put).toHaveBeenCalledWith(
      `/api/admin/model-configurations/${id}`,
      expect.objectContaining({ name: 'Renamed', version: 2 })
    );

    await modelApi.testConnection(id);
    expect(apiClient.post).toHaveBeenCalledWith(`/api/admin/model-configurations/${id}/test-connection`);

    await modelApi.testAgentCapabilities(id);
    expect(apiClient.post).toHaveBeenCalledWith(
      `/api/admin/model-configurations/${id}/test-agent-capabilities`
    );

    await modelApi.setEnabled(id, true, 3);
    expect(apiClient.post).toHaveBeenCalledWith(
      `/api/admin/model-configurations/${id}/enabled`,
      { enabled: true, version: 3 }
    );

    await modelApi.setDefault(id, false, 4);
    expect(apiClient.post).toHaveBeenCalledWith(
      `/api/admin/model-configurations/${id}/default`,
      { isDefault: false, version: 4 }
    );
  });

  it('clears keys and deletes by ID without placing secrets in URLs', async () => {
    await modelApi.clearApiKey(id, 5);
    expect(apiClient.delete).toHaveBeenCalledWith(
      `/api/admin/model-configurations/${id}/api-key`,
      { params: { version: 5 } }
    );

    await modelApi.delete(id, 6);
    expect(apiClient.delete).toHaveBeenCalledWith(
      `/api/admin/model-configurations/${id}`,
      { params: { version: 6 } }
    );

    const urls = [...apiClient.post.mock.calls, ...apiClient.put.mock.calls, ...apiClient.delete.mock.calls]
      .map(call => String(call[0]));
    expect(urls.join('\n')).not.toContain('apiKey');
    expect(urls.join('\n')).not.toContain('provider-secret');
  });
});
