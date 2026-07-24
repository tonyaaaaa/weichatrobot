import { expect, test } from '@playwright/test';
import { classifyRequest } from './request-classifier.mjs';
import { requiredRoles, userFromAuthorization } from './server-auth.mjs';
import { auditPage, createInitialState } from './server-fixtures.mjs';

test('request classifier fails closed for WorkTool and external-provider shaped paths', () => {
  expect(classifyRequest('/api/worktool/callback/robot')).toBe('worktool');
  expect(classifyRequest('/api/admin/worktool/robots')).toBe('worktool');
  expect(classifyRequest('/api/admin/worktool/group-operations/preview')).toBe('worktool');
  expect(classifyRequest('/wework/sendRawMessage')).toBe('worktool');
  expect(classifyRequest('/__fake/chat')).toBe('external-provider');
  expect(classifyRequest('/v1/chat/completions')).toBe('external-provider');
  expect(classifyRequest('/api/knowledge/documents')).toBe('application');
});

test('controlled server rejects representative forbidden paths and derives counters from requests', async ({ request }) => {
  await request.post('/__e2e/reset');
  expect((await request.get('/api/admin/worktool/robots')).status()).toBe(500);
  expect((await request.post('/api/worktool/callback/robot')).status()).toBe(500);
  expect((await request.post('/v1/chat/completions')).status()).toBe(500);
  const evidence = await request.get('/__e2e/evidence');
  expect(await evidence.json()).toMatchObject({ workToolRequests: 2, externalProviderCalls: 1 });
});

test('auth policy and paged audit fixtures are deterministic units', () => {
  expect(requiredRoles('/api/audit/conversations')).toEqual(['Admin', 'KnowledgeOperator']);
  expect(requiredRoles('/api/handoffs/')).toEqual(['Admin', 'HumanAgent']);
  expect(userFromAuthorization('Bearer e2e-token:knowledge@e2e.local')?.roles).toEqual(['KnowledgeOperator']);
  const state = createInitialState();
  expect(auditPage(state, 1, 20)).toMatchObject({ total: 21, page: 1, pageSize: 20 });
  expect(auditPage(state, 2, 20).items[0]).toMatchObject({ question: '第二页审计问题', sources: [] });
});
