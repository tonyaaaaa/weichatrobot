import { createServer } from 'node:http';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { classifyRequest } from './request-classifier.mjs';
import { requiredRoles, userFromRequest, users } from './server-auth.mjs';
import { auditPage, createInitialState, groupConfiguration, ids, page } from './server-fixtures.mjs';
import { serveStatic } from './server-static.mjs';

const root = resolve(fileURLToPath(new URL('../../src/web/wechatrobot-admin/dist', import.meta.url)));
const port = 4178;
let state;
function resetState() {
  state = createInitialState();
  state.models = [];
}
resetState();

function sendJson(response, status, value) {
  const body = JSON.stringify(value);
  response.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'content-length': Buffer.byteLength(body) });
  response.end(body);
}

async function readJson(request) {
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  if (!chunks.length) return {};
  const text = Buffer.concat(chunks).toString('utf8');
  return request.headers['content-type']?.includes('application/json') ? JSON.parse(text) : {};
}


async function handleApi(request, response, url) {
  const classification = classifyRequest(url.pathname);
  if (classification === 'worktool') {
    state.workToolRequests += 1;
    return sendJson(response, 500, { error: 'Default E2E forbids WorkTool requests.' });
  }
  if (classification === 'external-provider') {
    state.externalProviderCalls += 1;
    return sendJson(response, 500, { error: 'Default E2E forbids external provider requests.' });
  }

  if (url.pathname === '/api/auth/login' && request.method === 'POST') {
    const body = await readJson(request);
    const user = users[body.email];
    if (!user || user.password !== body.password) return sendJson(response, 401, { error: 'unauthorized' });
    return sendJson(response, 200, {
      accessToken: `e2e-token:${body.email}`, tokenType: 'Bearer', expiresInSeconds: 3600,
      user: { id: `${body.email}-id`, email: body.email, displayName: user.displayName, roles: user.roles }
    });
  }

  const user = userFromRequest(request);
  if (!user) return sendJson(response, 401, { error: 'unauthorized' });
  if (url.pathname === '/api/auth/me') {
    const email = request.headers.authorization.replace(/^Bearer e2e-token:/, '');
    return sendJson(response, 200, { id: `${email}-id`, email, displayName: user.displayName, roles: user.roles });
  }
  const roles = requiredRoles(url.pathname);
  if (roles.length && !user.roles.some(role => roles.includes(role))) return sendJson(response, 403, { error: 'forbidden' });

  if (url.pathname === '/api/admin/model-configurations' && request.method === 'GET') return sendJson(response, 200, state.models);
  if (url.pathname === '/api/admin/model-configurations' && request.method === 'POST') {
    const body = await readJson(request);
    const hasApiKey = typeof body.apiKey === 'string' && body.apiKey.trim().length > 0;
    const created = {
      id: '33333333-3333-3333-3333-333333333333',
      name: body.name.trim(),
      provider: body.provider.trim(),
      configurationType: body.configurationType,
      baseUrl: body.baseUrl.replace(/\/+$/, ''),
      model: body.model.trim(),
      timeoutSeconds: body.timeoutSeconds,
      maxRetries: body.maxRetries,
      isEnabled: false,
      isDefault: false,
      connectionStatus: 'Untested',
      hasApiKey,
      lastFour: hasApiKey ? body.apiKey.slice(-4) : null,
      version: 0
    };
    state.models.push(created);
    return sendJson(response, 201, created);
  }
  if (url.pathname === '/api/admin/robots/' && request.method === 'GET') return sendJson(response, 200, [state.robot]);
  if (url.pathname === '/api/admin/robots/robot-e2e' && request.method === 'PUT') {
    const body = await readJson(request);
    state.robot = { ...state.robot, ...body, updatedAtUtc: '2026-07-23T00:01:00Z' };
    return sendJson(response, 200, state.robot);
  }
  const modelRoute = url.pathname.match(/^\/api\/admin\/model-configurations\/([^/]+)(?:\/(test-connection|enabled|default|api-key))?$/);
  if (modelRoute) {
    const id = decodeURIComponent(modelRoute[1]);
    const action = modelRoute[2];
    const index = state.models.findIndex(model => model.id === id);
    if (index < 0) return sendJson(response, 404, { code: 'model_not_found' });
    const current = state.models[index];

    if (!action && request.method === 'PUT') {
      const body = await readJson(request);
      if (body.version !== current.version) return sendJson(response, 409, { code: 'model_concurrency_conflict' });
      const hasReplacementKey = typeof body.apiKey === 'string' && body.apiKey.trim().length > 0;
      const updated = {
        ...current,
        ...body,
        name: body.name.trim(),
        baseUrl: body.baseUrl.replace(/\/+$/, ''),
        model: body.model.trim(),
        hasApiKey: hasReplacementKey || current.hasApiKey,
        lastFour: hasReplacementKey ? body.apiKey.slice(-4) : current.lastFour,
        version: current.version + 1
      };
      delete updated.apiKey;
      state.models[index] = updated;
      return sendJson(response, 200, updated);
    }
    if (action === 'test-connection' && request.method === 'POST') {
      state.models[index] = { ...current, connectionStatus: 'Succeeded', lastTestedAtUtc: '2026-07-24T02:00:00Z', version: current.version + 1 };
      return sendJson(response, 200, state.models[index]);
    }
    if (action === 'enabled' && request.method === 'POST') {
      const body = await readJson(request);
      if (body.version !== current.version) return sendJson(response, 409, { code: 'model_concurrency_conflict' });
      if (body.enabled && current.connectionStatus !== 'Succeeded') return sendJson(response, 409, { code: 'model_test_required' });
      state.models[index] = { ...current, isEnabled: body.enabled, version: current.version + 1 };
      return sendJson(response, 200, state.models[index]);
    }
    if (action === 'default' && request.method === 'POST') {
      const body = await readJson(request);
      if (body.version !== current.version) return sendJson(response, 409, { code: 'model_concurrency_conflict' });
      if (body.isDefault && current.connectionStatus !== 'Succeeded') return sendJson(response, 409, { code: 'model_test_required' });
      state.models = state.models.map(model =>
        model.id !== id && model.configurationType === current.configurationType
          ? { ...model, isDefault: false }
          : model);
      state.models[index] = { ...state.models[index], isEnabled: body.isDefault ? true : current.isEnabled, isDefault: body.isDefault, version: current.version + 1 };
      return sendJson(response, 200, state.models[index]);
    }
    if (action === 'api-key' && request.method === 'DELETE') {
      if (Number(url.searchParams.get('version')) !== current.version) return sendJson(response, 409, { code: 'model_concurrency_conflict' });
      state.models[index] = {
        ...current,
        hasApiKey: false,
        lastFour: null,
        connectionStatus: 'Untested',
        isEnabled: false,
        isDefault: false,
        version: current.version + 1
      };
      return sendJson(response, 200, state.models[index]);
    }
    if (!action && request.method === 'DELETE') {
      if (Number(url.searchParams.get('version')) !== current.version) return sendJson(response, 409, { code: 'model_concurrency_conflict' });
      if (current.isDefault) return sendJson(response, 409, { code: 'model_default_delete_blocked' });
      if (id === '44444444-4444-4444-4444-444444444444') {
        return sendJson(response, 409, { code: 'model_reference_delete_blocked', retrievalAuditCount: 2 });
      }
      state.models.splice(index, 1);
      response.writeHead(204);
      return response.end();
    }
  }

  if (/^\/api\/groups\/[^/]+\/configuration$/.test(url.pathname)) return sendJson(response, 200, groupConfiguration());
  if (url.pathname === '/api/group-rules/preview' && request.method === 'POST') {
    const body = await readJson(request);
    state.ruleKinds = {
      include: [...new Set(body.includeRules.map(rule => rule.patternKind))].sort(),
      exclude: [...new Set(body.excludeRules.map(rule => rule.patternKind))].sort()
    };
    const matches = (rule, groupName) => {
      const source = rule.ignoreCase ? groupName.toLowerCase() : groupName;
      const pattern = rule.ignoreCase ? rule.pattern.toLowerCase() : rule.pattern;
      if (rule.patternKind === 'exact') return source === pattern;
      if (rule.patternKind === 'contains') return source.includes(pattern);
      return new RegExp(rule.pattern, rule.ignoreCase ? 'i' : '').test(groupName);
    };
    return sendJson(response, 200, {
      results: body.groupNames.map(groupName => ({
        groupName,
        isExcluded: body.excludeRules.some(rule => matches(rule, groupName)),
        isMatch: body.includeRules.some(rule => matches(rule, groupName))
          && !body.excludeRules.some(rule => matches(rule, groupName))
      }))
    });
  }

  if (url.pathname === '/api/audit/conversations' && request.method === 'GET') {
    return sendJson(response, 200, auditPage(state, Number(url.searchParams.get('page') ?? 1), Number(url.searchParams.get('pageSize') ?? 20)));
  }

  if (url.pathname === '/api/knowledge/documents' && request.method === 'POST') {
    return sendJson(response, 200, {
      documentId: ids.document, versionId: ids.version, version: 1, state: 'preview_ready',
      safeFileName: 'safe-e2e.md', publicUrl: 'http://127.0.0.1:4178/__fake/safe-e2e.md',
      publicReadWarning: 'E2E fixture only'
    });
  }
  if (url.pathname === `/api/knowledge/versions/${ids.version}/previews` && request.method === 'GET') {
    return sendJson(response, 200, {
      versionId: ids.version, revision: 1,
      items: [{ id: ids.chunk, sequence: 1, text: 'API seeded acceptance document.', status: state.approvedChunks ? 'approved' : 'draft' }]
    });
  }
  if (url.pathname === `/api/knowledge/versions/${ids.version}/previews/approve` && request.method === 'POST') {
    state.approvedChunks = 1;
    return sendJson(response, 200, [{ id: ids.chunk, sequence: 1, text: 'API seeded acceptance document.', status: 'approved' }]);
  }
  if (url.pathname === `/api/knowledge/documents/${ids.document}/index-status`) {
    return sendJson(response, 200, {
      documentId: ids.document, activeVersionId: state.documentIndexed ? ids.version : null,
      documentStatus: state.documentIndexed ? 'active' : 'preview_ready',
      approvedChunkCount: state.approvedChunks, activePointCount: state.documentIndexed ? 1 : 0,
      consistency: state.documentIndexed ? 'consistent' : 'not-checked', driftDetails: [], jobs: []
    });
  }
  if (url.pathname === `/api/knowledge/documents/${ids.document}/versions/${ids.version}/index` && request.method === 'POST') {
    state.documentIndexed = true;
    return sendJson(response, 200, { jobId: 'index-job-e2e' });
  }

  if (url.pathname === '/api/handoffs/' && request.method === 'GET') {
    return sendJson(response, 200, page([{ id: 'handoff-e2e', state: state.handoffState, reasonCode: 'explicit-transfer', version: state.handoffVersion, updatedAtUtc: '2026-07-23T00:00:00Z' }]));
  }
  if (url.pathname === '/api/handoffs/handoff-e2e' && request.method === 'GET') {
    return sendJson(response, 200, { id: 'handoff-e2e', state: state.handoffState, reasonCode: 'explicit-transfer', version: state.handoffVersion, updatedAtUtc: '2026-07-23T00:00:00Z', evidenceJson: '{"reason":"用户明确要求人工"}', finalAnswer: state.handoffState === 'Resolved' ? state.finalAnswer : '' });
  }
  if (url.pathname === '/api/handoffs/handoff-e2e/messages') {
    return sendJson(response, 200, page([{ id: 'message-e2e', senderDisplayName: '安全测试用户', authenticationKind: 'recorded-fixture', text: '请转人工', createdAtUtc: '2026-07-23T00:00:00Z' }]));
  }
  if (url.pathname === '/api/handoffs/handoff-e2e/transitions') {
    return sendJson(response, 200, page([{ id: 'transition-e2e', sequence: 1, fromState: 'AIActive', toState: state.handoffState, reasonCode: 'explicit-transfer', createdAtUtc: '2026-07-23T00:00:00Z' }]));
  }
  if (url.pathname === '/api/handoffs/handoff-e2e/assign' && request.method === 'POST') {
    state.handoffState = 'HumanHandling'; state.handoffVersion += 1;
    return sendJson(response, 200, { id: 'handoff-e2e', state: state.handoffState, version: state.handoffVersion });
  }
  if (url.pathname === '/api/handoffs/handoff-e2e/resolve' && request.method === 'POST') {
    const body = await readJson(request);
    state.finalAnswer = body.finalAnswer; state.handoffState = 'Resolved'; state.handoffVersion += 1;
    return sendJson(response, 200, { id: 'candidate-e2e', handoffCaseId: 'handoff-e2e', question: '如何处理安全测试？', answer: state.finalAnswer, status: 'pending', version: 1 });
  }

  if (url.pathname === '/api/knowledge/candidates/' && request.method === 'GET') {
    return sendJson(response, 200, page([{ id: 'candidate-e2e', question: '如何处理安全测试？', status: state.candidateStatus, version: 1, updatedAtUtc: '2026-07-23T00:00:00Z' }]));
  }
  if (url.pathname === '/api/knowledge/candidates/candidate-e2e' && request.method === 'GET') {
    return sendJson(response, 200, { id: 'candidate-e2e', question: '如何处理安全测试？', answer: state.finalAnswer, evidenceJson: '{"source":"resolved-handoff"}', status: state.candidateStatus, version: 1, updatedAtUtc: '2026-07-23T00:00:00Z' });
  }
  if (url.pathname === '/api/knowledge/candidates/candidate-e2e/reviews' && request.method === 'POST') {
    state.candidateStatus = 'approved_pending_index';
    return sendJson(response, 200, { status: state.candidateStatus });
  }

  return sendJson(response, 404, { error: `No E2E route for ${request.method} ${url.pathname}` });
}

createServer(async (request, response) => {
  try {
    const url = new URL(request.url, `http://${request.headers.host}`);
    if (url.pathname === '/__e2e/health') return sendJson(response, 200, { ready: true });
    if (url.pathname === '/__e2e/reset' && request.method === 'POST') { resetState(); return sendJson(response, 200, { reset: true }); }
    if (url.pathname === '/__e2e/evidence') return sendJson(response, 200, {
      documentIndexed: state.documentIndexed, approvedChunks: state.approvedChunks,
      externalProviderCalls: state.externalProviderCalls, workToolRequests: state.workToolRequests,
      ruleKinds: state.ruleKinds
    });
    if (url.pathname.startsWith('/api/') || url.pathname.startsWith('/wework/') || url.pathname.startsWith('/v1/')
      || url.pathname.startsWith('/__fake/')) return await handleApi(request, response, url);
    return serveStatic(root, response, url.pathname);
  } catch (error) {
    return sendJson(response, 500, { error: error instanceof Error ? error.message : 'unknown error' });
  }
}).listen(port, '127.0.0.1', () => {
  process.stdout.write(`WechatRobot E2E server listening on http://127.0.0.1:${port}\n`);
});
