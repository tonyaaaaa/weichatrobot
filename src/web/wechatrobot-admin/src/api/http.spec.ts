import { AxiosError, type AxiosRequestConfig } from 'axios';
import { describe, expect, it, vi } from 'vitest';
import { createApiClient } from './http';

describe('API HTTP client', () => {
  it('adds the bearer token to authenticated API requests', async () => {
    const client = createApiClient(() => 'access-token', vi.fn());
    let request: AxiosRequestConfig | undefined;

    await client.get('/api/auth/me', {
      adapter: async config => {
        request = config;
        return { config, data: {}, headers: {}, status: 200, statusText: 'OK' };
      }
    });

    expect(request?.headers?.Authorization).toBe('Bearer access-token');
  });

  it('notifies the session owner after a 401 response', async () => {
    const onUnauthorized = vi.fn();
    const client = createApiClient(() => 'access-token', onUnauthorized);

    await expect(client.get('/api/auth/me', {
      adapter: async config => {
        const response = { config, data: {}, headers: {}, status: 401, statusText: 'Unauthorized' };
        throw new AxiosError('Unauthorized', 'ERR_BAD_REQUEST', config, undefined, response);
      }
    })).rejects.toMatchObject({ response: { status: 401 } });

    expect(onUnauthorized).toHaveBeenCalledOnce();
  });
});
