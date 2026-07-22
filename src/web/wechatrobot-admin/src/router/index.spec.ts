import { describe, expect, it } from 'vitest';
import { getVisibleNavigation } from './index';

describe('role-aware navigation', () => {
  it('hides admin-only menu entries from a human agent', () => {
    const labels = getVisibleNavigation(['HumanAgent']).map(item => item.label);

    expect(labels).toContain('工作台');
    expect(labels).toContain('人工转接');
    expect(labels).not.toContain('模型配置');
    expect(labels).not.toContain('用户与角色');
  });
});
