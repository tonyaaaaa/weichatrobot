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

  it.each(['/api/auth/me', 'https://api.example.test/api/auth/me'])(
    'adds the bearer token to a configured API destination: %s',
    async url => {
      const client = createApiClient(() => 'access-token', vi.fn(), 'https://api.example.test');
      let request: AxiosRequestConfig | undefined;

      await client.get(url, {
        adapter: async config => {
          request = config;
          return { config, data: {}, headers: {}, status: 200, statusText: 'OK' };
        }
      });

      expect(request?.headers?.Authorization).toBe('Bearer access-token');
    });

  it('does not leak the bearer token to an arbitrary absolute URL', async () => {
    const client = createApiClient(() => 'access-token', vi.fn());
    let request: AxiosRequestConfig | undefined;

    await client.get('https://untrusted.example.test/collect', {
      adapter: async config => {
        request = config;
        return { config, data: {}, headers: {}, status: 200, statusText: 'OK' };
      }
    });

    expect(request?.headers?.Authorization).toBeUndefined();
  });

  it('does not leak the bearer token when a relative API URL overrides its base URL', async () => {
    const client = createApiClient(() => 'access-token', vi.fn());
    let request: AxiosRequestConfig | undefined;

    await client.get('/api/auth/me', {
      baseURL: 'https://untrusted.example.test',
      adapter: async config => {
        request = config;
        return { config, data: {}, headers: {}, status: 200, statusText: 'OK' };
      }
    });

    expect(request?.headers?.Authorization).toBeUndefined();
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
