import { describe, expect, it } from 'vitest';
import { getVisibleNavigation } from './index';

describe('role-aware navigation', () => {
  it.each([
    ['Admin', ['工作台', '知识库', '知识库标签', '群管理', '人工转接', '会话审计', '模型配置', '用户与角色', '系统设置'], []],
    ['KnowledgeOperator', ['工作台', '知识库', '知识库标签', '会话审计'], ['群管理', '人工转接', '模型配置', '用户与角色', '系统设置']],
    ['HumanAgent', ['工作台', '人工转接'], ['知识库', '知识库标签', '群管理', '会话审计', '模型配置', '用户与角色', '系统设置']]
  ])('%s sees only the navigation granted by route metadata', (role, visible, hidden) => {
    const labels = getVisibleNavigation([role]).map(item => item.label);

    expect(labels).toEqual(visible);
    for (const label of hidden) {
      expect(labels).not.toContain(label);
    }
  });
});
