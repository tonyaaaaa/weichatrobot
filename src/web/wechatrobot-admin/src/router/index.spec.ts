import { createPinia, setActivePinia } from 'pinia';
import { describe, expect, it } from 'vitest';
import { getVisibleNavigation, router, routes } from './index';
import { useAuthStore } from '../stores/auth';

describe('role-aware navigation', () => {
  it.each([
    ['Admin', ['工作台', '知识库', '知识库标签', '知识审核', '群管理', '人工转接', '会话审计', '管理审计', '模型配置', '机器人设置', '用户与角色', '系统设置'], []],
    ['KnowledgeOperator', ['工作台', '知识库', '知识库标签', '知识审核', '会话审计'], ['群管理', '人工转接', '管理审计', '模型配置', '机器人设置', '用户与角色', '系统设置']],
    ['HumanAgent', ['工作台', '人工转接'], ['知识库', '知识库标签', '知识审核', '群管理', '会话审计', '管理审计', '模型配置', '机器人设置', '用户与角色', '系统设置']]
  ])('%s sees only the navigation granted by route metadata', (role, visible, hidden) => {
    const labels = getVisibleNavigation([role]).map(item => item.label);

    expect(labels).toEqual(visible);
    for (const label of hidden) {
      expect(labels).not.toContain(label);
    }
  });

  it('denies an authenticated HumanAgent who navigates directly to the admin model URL', async () => {
    setActivePinia(createPinia());
    const auth = useAuthStore();
    auth.accessToken = 'test-token';
    auth.user = { id: 'u1', email: 'agent@example.test', displayName: 'Agent', roles: ['HumanAgent'] };
    await router.push('/login');
    await router.push('/models');
    expect(router.currentRoute.value.name).toBe('dashboard');
  });

  it('redirects an unauthenticated direct operational URL to login', async () => {
    setActivePinia(createPinia());
    await router.push('/knowledge/review');
    expect(router.currentRoute.value.name).toBe('login');
  });

  it('registers document management separately from the version chunk workflow', () => {
    const admin = routes.find(route => route.path === '/');
    const management = admin?.children?.find(
      route => route.path === 'knowledge/documents/:documentId');
    const chunks = admin?.children?.find(
      route => route.path === 'knowledge/documents/:documentId/versions/:versionId');

    expect(management?.name).toBe('knowledge-document-management');
    expect(management?.props).toBe(true);
    expect(management?.meta?.roles).toEqual(['Admin', 'KnowledgeOperator']);
    expect(chunks?.name).toBe('knowledge-document-detail');
  });

  it('registers a group list separately from GUID-backed configuration details', () => {
    const admin = routes.find(route => route.path === '/');
    const groupList = admin?.children?.find(route => route.path === 'groups');
    const configuration = admin?.children?.find(
      route => route.path === 'groups/:id/configuration');

    expect(groupList?.name).toBe('group-list');
    expect(configuration?.name).toBe('group-configuration');
    expect(configuration?.props).toBe(true);
    expect(configuration?.meta?.roles).toEqual(['Admin']);
  });

  it('registers administration audit as an Admin-only route', () => {
    const admin = routes.find(route => route.path === '/');
    const audit = admin?.children?.find(route => route.path === 'administration-audits');
    expect(audit?.name).toBe('administration-audit');
    expect(audit?.meta?.roles).toEqual(['Admin']);
  });
});
