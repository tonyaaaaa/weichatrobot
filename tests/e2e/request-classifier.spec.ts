import { expect, test } from '@playwright/test';
import { classifyRequest } from './request-classifier.mjs';

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
