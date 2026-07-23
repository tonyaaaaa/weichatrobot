import { createReadStream, existsSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { classifyRequest } from './request-classifier.mjs';

const root = resolve(fileURLToPath(new URL('../../src/web/wechatrobot-admin/dist', import.meta.url)));
const port = 4178;
const ids = {
  document: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
  version: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
  chunk: 'cccccccc-cccc-cccc-cccc-cccccccccccc'
};

let state;
function resetState() {
  state = {
    documentIndexed: false,
    approvedChunks: 0,
    externalProviderCalls: 0,
    workToolRequests: 0,
    ruleKinds: { include: [], exclude: [] },
    handoffState: 'WaitingHuman',
    handoffVersion: 1,
    candidateStatus: 'pending',
    finalAnswer: '由人工确认的安全答案。',
    robot: { id: 'robot-e2e', name: 'E2E 机器人', isEnabled: true, sendRateLimitPerMinute: 50, updatedAtUtc: '2026-07-23T00:00:00Z' },
    model: {
      id: 'model-e2e', name: 'e2e-chat', provider: 'fake-local', configurationType: 'chat',
      baseUrl: 'http://127.0.0.1:4178/__fake/chat', model: 'safe-chat-v1',
      timeoutSeconds: 5, maxRetries: 0, isEnabled: true, isDefault: true,
      hasApiKey: true, lastFour: '1234'
    }
  };
}
resetState();

const users = {
  'admin@e2e.local': { password: 'Safe-E2E-Admin-1!', roles: ['Admin'], displayName: 'E2E 管理员' },
  'knowledge@e2e.local': { password: 'Safe-E2E-Knowledge-1!', roles: ['KnowledgeOperator'], displayName: 'E2E 知识运营' },
  'human@e2e.local': { password: 'Safe-E2E-Human-1!', roles: ['HumanAgent'], displayName: 'E2E 人工客服' }
};

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

function userFromRequest(request) {
  const token = request.headers.authorization?.replace(/^Bearer /, '');
  const email = token?.replace(/^e2e-token:/, '');
  return email ? users[email] : undefined;
}

function requiredRoles(pathname) {
  if (pathname.startsWith('/api/admin/') || pathname.startsWith('/api/groups/') || pathname === '/api/group-rules/preview') return ['Admin'];
  if (pathname.startsWith('/api/knowledge/') || pathname.startsWith('/api/audit/')) return ['Admin', 'KnowledgeOperator'];
  if (pathname.startsWith('/api/handoffs/')) return ['Admin', 'HumanAgent'];
  return [];
}

function page(items) {
  return { items, total: items.length, page: 1, pageSize: 20 };
}

function groupConfiguration() {
  return {
    id: 'group-e2e', name: '技术部',
    rules: {
      include: [{ id: 'rule-include', pattern: '^技术部', patternKind: 'regex', ignoreCase: true }],
      exclude: [{ id: 'rule-exclude', pattern: '禁用', patternKind: 'contains', ignoreCase: true }]
    },
    boundTagIds: ['11111111-1111-1111-1111-111111111111'],
    allowedTagIds: ['11111111-1111-1111-1111-111111111111'],
    availableTags: [{ id: '11111111-1111-1111-1111-111111111111', name: '安全测试', isGlobalPublic: false, isEnabled: true, isBound: true }],
    tagVisibility: 'any-bound-tag-or-global-public',
    context: {
      configured: {},
      effective: { senderIsolated: false, historyTurns: 6, idleTimeoutMinutes: 30, tokenCap: 3000, summaryEnabled: true, includeBotHistory: true }
    },
    clearedContextSessions: 0
  };
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

  if (url.pathname === '/api/admin/model-configurations' && request.method === 'GET') return sendJson(response, 200, [state.model]);
  if (url.pathname === '/api/admin/robots/' && request.method === 'GET') return sendJson(response, 200, [state.robot]);
  if (url.pathname === '/api/admin/robots/robot-e2e' && request.method === 'PUT') {
    const body = await readJson(request);
    state.robot = { ...state.robot, ...body, updatedAtUtc: '2026-07-23T00:01:00Z' };
    return sendJson(response, 200, state.robot);
  }
  if (url.pathname === '/api/admin/model-configurations/e2e-chat' && request.method === 'PUT') {
    const body = await readJson(request);
    state.model = { ...state.model, ...body, hasApiKey: true, lastFour: '1234' };
    delete state.model.apiKey;
    return sendJson(response, 200, state.model);
  }
  if (url.pathname === '/api/admin/model-configurations/e2e-chat/test-connection' && request.method === 'POST') {
    return sendJson(response, 200, { succeeded: true });
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
    return sendJson(response, 200, {
      items: [{
        id: 'audit-e2e', groupProfileId: 'group-e2e', workToolMessageId: 'recorded-e2e-message',
        question: '如何重置密码？', answer: '请使用安全重置页面。', decision: 'Answer',
        createdAtUtc: '2026-07-23T00:00:00Z', sources: ['安全手册'],
        evidence: [{ documentId: 'safe-document', chunkId: 'safe-chunk', title: '安全手册' }],
        inputSummary: { promptTemplateVersion: 'grounded-v2' },
        send: { status: 'completed', attemptCount: 1 },
        handoff: null, knowledgeCandidate: null
      }],
      total: 1, page: 1, pageSize: 20
    });
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

function serveStatic(response, pathname) {
  const requested = pathname === '/' ? '/index.html' : pathname;
  const relative = normalize(requested).replace(/^([/\\])+/, '');
  let path = join(root, relative);
  if (!path.startsWith(root) || !existsSync(path) || statSync(path).isDirectory()) path = join(root, 'index.html');
  const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml' };
  response.writeHead(200, { 'content-type': types[extname(path)] ?? 'application/octet-stream' });
  createReadStream(path).pipe(response);
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
    return serveStatic(response, url.pathname);
  } catch (error) {
    return sendJson(response, 500, { error: error instanceof Error ? error.message : 'unknown error' });
  }
}).listen(port, '127.0.0.1', () => {
  process.stdout.write(`WechatRobot E2E server listening on http://127.0.0.1:${port}\n`);
});
