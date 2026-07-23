import { createServer } from 'node:http';

const port = Number(process.env.TASK16_MOCK_PORT ?? 5099);
const now = '2026-07-23T02:30:00Z';
const user = {
  id: '00000000-0000-0000-0000-000000000001',
  email: 'local-admin@example.test',
  displayName: '本地验收管理员',
  roles: ['Admin']
};

function send(response, status, body) {
  response.writeHead(status, {
    'Access-Control-Allow-Headers': 'Authorization, Content-Type',
    'Access-Control-Allow-Methods': 'GET, POST, PUT, DELETE, OPTIONS',
    'Access-Control-Allow-Origin': 'http://127.0.0.1:4177',
    'Content-Type': 'application/json; charset=utf-8'
  });
  response.end(JSON.stringify(body));
}

createServer((request, response) => {
  if (request.method === 'OPTIONS') return send(response, 204, {});
  const url = new URL(request.url ?? '/', `http://${request.headers.host}`);

  if (request.method === 'POST' && url.pathname === '/api/auth/login') {
    return send(response, 200, { accessToken: 'local-layout-smoke-token', tokenType: 'Bearer', expiresInSeconds: 3600, user });
  }
  if (request.method === 'GET' && url.pathname === '/api/auth/me') return send(response, 200, user);

  if (request.method === 'GET' && url.pathname === '/api/knowledge/candidates/') {
    return send(response, 200, {
      items: [{ id: '10000000-0000-0000-0000-000000000001', question: '如何申请退款？', status: 'pending', version: 1, updatedAtUtc: now }],
      total: 1,
      page: Number(url.searchParams.get('page') ?? 1),
      pageSize: Number(url.searchParams.get('pageSize') ?? 20)
    });
  }
  if (request.method === 'GET' && url.pathname === '/api/knowledge/candidates/10000000-0000-0000-0000-000000000001') {
    return send(response, 200, {
      id: '10000000-0000-0000-0000-000000000001',
      handoffCaseId: '20000000-0000-0000-0000-000000000001',
      question: '如何申请退款？',
      answer: '请联系售后并提供订单号。',
      evidenceJson: '{"source":"human"}',
      status: 'pending',
      version: 1,
      updatedAtUtc: now
    });
  }

  const handoff = {
    id: '20000000-0000-0000-0000-000000000001',
    state: 'WaitingHuman',
    reasonCode: 'low_confidence',
    version: 1,
    updatedAtUtc: now
  };
  if (request.method === 'GET' && url.pathname === '/api/handoffs/') {
    return send(response, 200, { items: [handoff], total: 1, page: 1, pageSize: 20 });
  }
  if (request.method === 'GET' && url.pathname === `/api/handoffs/${handoff.id}`) return send(response, 200, handoff);
  if (request.method === 'GET' && url.pathname === `/api/handoffs/${handoff.id}/messages`) {
    return send(response, 200, { items: [], total: 0, page: Number(url.searchParams.get('page') ?? 1), pageSize: 10 });
  }
  if (request.method === 'GET' && url.pathname === `/api/handoffs/${handoff.id}/transitions`) {
    return send(response, 200, { items: [], total: 0, page: Number(url.searchParams.get('page') ?? 1), pageSize: 10 });
  }

  if (request.method === 'GET' && url.pathname === '/api/admin/model-configurations') {
    return send(response, 200, [{
      id: '30000000-0000-0000-0000-000000000001',
      name: 'chat-default',
      provider: 'openai-compatible',
      configurationType: 'chat',
      baseUrl: 'https://provider.example.test/v1',
      model: 'local-layout-model',
      timeoutSeconds: 60,
      maxRetries: 2,
      isEnabled: true,
      isDefault: true,
      hasApiKey: true,
      lastFour: '0000'
    }]);
  }

  return send(response, 404, { error: 'not-implemented-in-layout-smoke' });
}).listen(port, '127.0.0.1', () => {
  console.log(`Task 16 mock API listening on http://127.0.0.1:${port}`);
});
