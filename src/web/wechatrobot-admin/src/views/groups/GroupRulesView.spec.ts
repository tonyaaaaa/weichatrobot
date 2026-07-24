import { describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import GroupRulesView from './GroupRulesView.vue';

const api = {
  getConfiguration: vi.fn(),
  updateConfiguration: vi.fn(),
  previewRules: vi.fn()
};

describe('GroupRulesView', () => {
  it('organizes the whole page into compact primary and secondary configuration regions', async () => {
    api.getConfiguration.mockResolvedValue({
      rules: { include: [], exclude: [] },
      boundTagIds: [],
      availableTags: [],
      context: { configured: {}, effective: { senderIsolated: false, historyTurns: 6, idleTimeoutMinutes: 30, tokenCap: 3000, summaryEnabled: true, includeBotHistory: true } }
    });
    const wrapper = mount(GroupRulesView, { props: { groupId: '00000000-0000-0000-0000-000000000801', api } });
    await Promise.resolve();
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.group-page-header').exists()).toBe(true);
    expect(wrapper.find('.group-identity-bar').exists()).toBe(true);
    expect(wrapper.find('.group-layout').exists()).toBe(true);
    expect(wrapper.find('.group-primary-column').find('[aria-label="群匹配规则"]').exists()).toBe(true);
    expect(wrapper.find('.group-secondary-column').find('[aria-label="上下文策略"]').exists()).toBe(true);
    expect(wrapper.find('.group-save-bar [data-testid="save-configuration"]').exists()).toBe(true);
  });

  it('renders each matching rule as one compact and accessible editing row', async () => {
    api.getConfiguration.mockResolvedValue({
      rules: { include: [], exclude: [] },
      boundTagIds: [],
      availableTags: [],
      context: { configured: {}, effective: { senderIsolated: false, historyTurns: 6, idleTimeoutMinutes: 30, tokenCap: 3000, summaryEnabled: true, includeBotHistory: true } }
    });
    const wrapper = mount(GroupRulesView, { props: { groupId: '00000000-0000-0000-0000-000000000801', api } });
    await Promise.resolve();
    await wrapper.vm.$nextTick();
    await wrapper.get('[data-testid="add-exact-include"]').trigger('click');

    const row = wrapper.get('.rule-row');
    expect(row.find('select').exists()).toBe(true);
    expect(row.find('input[type="text"]').exists()).toBe(true);
    expect(row.find('.rule-case-toggle').exists()).toBe(true);
    expect(row.find('.rule-remove').attributes('aria-label')).toContain('删除包含规则');
    expect(wrapper.find('.rule-section-heading [data-testid="add-exact-include"]').exists()).toBe(true);
    expect(wrapper.find('.context-policy-grid').exists()).toBe(true);
  });

  it('edits exact, contains and regex include/exclude rules, previews hits, and clears context through the API', async () => {
    api.getConfiguration.mockResolvedValue({
      rules: { include: [], exclude: [] },
      boundTagIds: [],
      availableTags: [],
      context: { configured: {}, effective: { senderIsolated: false, historyTurns: 6, idleTimeoutMinutes: 30, tokenCap: 3000, summaryEnabled: true, includeBotHistory: true } }
    });
    api.previewRules.mockResolvedValue({ results: [{ groupName: '技术测试群', isMatch: false, isExcluded: true }] });
    api.updateConfiguration.mockResolvedValue({
      clearedContextSessions: 1,
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

  it('shows a disabled existing binding so an administrator can remove it and save other changes', async () => {
    const disabledTagId = '00000000-0000-0000-0000-000000000802';
    api.getConfiguration.mockResolvedValue({
      rules: { include: [], exclude: [] }, boundTagIds: [disabledTagId],
      availableTags: [{ id: disabledTagId, name: '已停用技术', isGlobalPublic: false, isEnabled: false, isBound: true }],
      context: { configured: {}, effective: { senderIsolated: false, historyTurns: 6, idleTimeoutMinutes: 30, tokenCap: 3000, summaryEnabled: true, includeBotHistory: true } }
    });
    api.updateConfiguration.mockResolvedValue({
      clearedContextSessions: 0,
      context: { configured: {}, effective: { senderIsolated: false, historyTurns: 6, idleTimeoutMinutes: 30, tokenCap: 3000, summaryEnabled: true, includeBotHistory: true } }
    });
    const wrapper = mount(GroupRulesView, { props: { groupId: '00000000-0000-0000-0000-000000000801', api } });
    await Promise.resolve();
    await wrapper.vm.$nextTick();
    await wrapper.get(`[data-testid="tag-${disabledTagId}"]`).setValue(false);
    await wrapper.get('[data-testid="save-configuration"]').trigger('click');

    expect(wrapper.text()).toContain('已禁用，移除后不可重新添加');
    expect(api.updateConfiguration).toHaveBeenCalledWith(expect.any(String), expect.objectContaining({ boundTagIds: [] }));
  });
});
