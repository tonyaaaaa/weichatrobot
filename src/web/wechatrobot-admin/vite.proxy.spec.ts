import { createServer as createHttpServer, type Server as HttpServer } from 'node:http';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { createServer as createViteServer, type ViteDevServer } from 'vite';

describe('development API proxy', () => {
  let backend: HttpServer | undefined;
  let vite: ViteDevServer | undefined;
  const originalTarget = process.env.VITE_API_PROXY_TARGET;

  afterEach(async () => {
    await vite?.close();
    if (backend) {
      await new Promise<void>((resolve, reject) =>
        backend!.close(error => error ? reject(error) : resolve()));
    }
    if (originalTarget === undefined) {
      delete process.env.VITE_API_PROXY_TARGET;
    } else {
      process.env.VITE_API_PROXY_TARGET = originalTarget;
    }
  });

  it('forwards relative API requests to the configured backend', async () => {
    let receivedPath = '';
    backend = createHttpServer((request, response) => {
      receivedPath = request.url ?? '';
      response.writeHead(401, { 'Content-Type': 'application/json' });
      response.end('{"error":"unauthorized"}');
    });
    await new Promise<void>(resolve => backend!.listen(0, '127.0.0.1', resolve));
    const backendAddress = backend.address();
    if (!backendAddress || typeof backendAddress === 'string') {
      throw new Error('Backend test server did not expose a TCP port.');
    }
    process.env.VITE_API_PROXY_TARGET = `http://127.0.0.1:${backendAddress.port}`;

    vite = await createViteServer({
      configFile: path.resolve(process.cwd(), 'vite.config.ts'),
      server: { host: '127.0.0.1', port: 0 }
    });
    await vite.listen();
    const viteAddress = vite.httpServer?.address();
    if (!viteAddress || typeof viteAddress === 'string') {
      throw new Error('Vite test server did not expose a TCP port.');
    }

    const response = await fetch(`http://127.0.0.1:${viteAddress.port}/api/auth/login`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: '{"email":"nobody@example.invalid","password":"invalid"}'
    });

    expect(response.status).toBe(401);
    expect(receivedPath).toBe('/api/auth/login');
  });
});
