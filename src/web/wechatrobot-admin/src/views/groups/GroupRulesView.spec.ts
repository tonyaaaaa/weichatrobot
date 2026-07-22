import { describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import GroupRulesView from './GroupRulesView.vue';

const api = {
  getConfiguration: vi.fn(),
  updateConfiguration: vi.fn(),
  previewRules: vi.fn()
};

describe('GroupRulesView', () => {
  it('edits exact, contains and regex include/exclude rules, previews hits, and clears context through the API', async () => {
    api.getConfiguration.mockResolvedValue({
      rules: { include: [], exclude: [] },
      tags: [],
      context: { configured: {}, effective: { senderIsolated: false, historyTurns: 6, idleTimeoutMinutes: 30, tokenCap: 3000, summaryEnabled: true, includeBotHistory: true } }
    });
    api.previewRules.mockResolvedValue({ results: [{ groupName: '技术测试群', isMatch: false, isExcluded: true }] });
    api.updateConfiguration.mockResolvedValue({
      clearedContextMessages: 1,
      context: { configured: {}, effective: { senderIsolated: false, historyTurns: 6, idleTimeoutMinutes: 30, tokenCap: 3000, summaryEnabled: true, includeBotHistory: true } }
    });
    const wrapper = mount(GroupRulesView, { props: { groupId: '00000000-0000-0000-0000-000000000801', api } });

    await wrapper.get('[data-testid="add-exact-include"]').trigger('click');
    await wrapper.get('[data-testid="add-contains-exclude"]').trigger('click');
    await wrapper.get('[data-testid="add-regex-include"]').trigger('click');
    await wrapper.get('[data-testid="preview-rules"]').trigger('click');
    await wrapper.get('[data-testid="clear-context"]').trigger('click');

    expect(api.previewRules).toHaveBeenCalledOnce();
    expect(api.updateConfiguration).toHaveBeenCalledWith(expect.any(String), expect.objectContaining({ clearContext: true }));
    expect(wrapper.text()).toContain('技术测试群');
    expect(wrapper.text()).toContain('已排除');
    expect(wrapper.text()).toContain('按成员隔离');
  });
});
