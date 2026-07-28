import { describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import GroupRulesView from './GroupRulesView.vue';

const api = {
  getConfiguration: vi.fn(),
  updateConfiguration: vi.fn(),
  previewRules: vi.fn()
};
const routerLinkStub = {
  props: ['to'],
  template: '<a><slot /></a>'
};

describe('GroupRulesView', () => {
  it('organizes the whole page into compact primary and secondary configuration regions', async () => {
    api.getConfiguration.mockResolvedValue({
      rules: { include: [], exclude: [] },
      boundTagIds: [],
      availableTags: [],
      context: { configured: {}, effective: { senderIsolated: false, historyTurns: 6, idleTimeoutMinutes: 30, tokenCap: 3000, summaryEnabled: true, includeBotHistory: true } }
    });
    const wrapper = mount(GroupRulesView, { props: { id: '00000000-0000-0000-0000-000000000801', api } });
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
    const wrapper = mount(GroupRulesView, { props: { id: '00000000-0000-0000-0000-000000000801', api } });
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
    const wrapper = mount(GroupRulesView, { props: { id: '00000000-0000-0000-0000-000000000801', api } });
    await flushPromises();

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
    const wrapper = mount(GroupRulesView, { props: { id: '00000000-0000-0000-0000-000000000801', api } });
    await Promise.resolve();
    await wrapper.vm.$nextTick();
    await wrapper.get(`[data-testid="tag-${disabledTagId}"]`).setValue(false);
    await wrapper.get('[data-testid="save-configuration"]').trigger('click');

    expect(wrapper.text()).toContain('已禁用，移除后不可重新添加');
    expect(api.updateConfiguration).toHaveBeenCalledWith(
      '00000000-0000-0000-0000-000000000801',
      expect.objectContaining({ boundTagIds: [], clearContext: false })
    );
    expect(wrapper.find('[aria-label="群配置 ID"]').exists()).toBe(false);
  });

  it('keeps a disabled bound tag removable but prevents selecting a disabled unbound tag', async () => {
    api.getConfiguration.mockResolvedValue({
      rules: { include: [], exclude: [] },
      boundTagIds: ['disabled-bound'],
      availableTags: [
        { id: 'disabled-bound', name: '历史标签', isGlobalPublic: false, isEnabled: false, isBound: true },
        { id: 'disabled-unbound', name: '停用标签', isGlobalPublic: false, isEnabled: false, isBound: false }
      ],
      context: { configured: {}, effective: { senderIsolated: false, historyTurns: 6, idleTimeoutMinutes: 30, tokenCap: 3000, summaryEnabled: true, includeBotHistory: true } }
    });
    const wrapper = mount(GroupRulesView, {
      props: { id: '00000000-0000-0000-0000-000000000801', api }
    });
    await Promise.resolve();
    await wrapper.vm.$nextTick();

    expect(wrapper.get('[data-testid="tag-disabled-bound"]').attributes('disabled')).toBeUndefined();
    expect(wrapper.get('[data-testid="tag-disabled-unbound"]').attributes('disabled')).toBeDefined();
  });

  it('shows the loaded group name without exposing an editable internal id', async () => {
    api.getConfiguration.mockResolvedValue({
      name: '技术群',
      rules: { include: [], exclude: [] },
      boundTagIds: [],
      availableTags: [],
      context: { configured: {}, effective: { senderIsolated: false, historyTurns: 6, idleTimeoutMinutes: 30, tokenCap: 3000, summaryEnabled: true, includeBotHistory: true } }
    });
    const wrapper = mount(GroupRulesView, {
      props: { id: '00000000-0000-0000-0000-000000000801', api },
      global: { stubs: { RouterLink: routerLinkStub } }
    });
    await flushPromises();

    expect(wrapper.text()).toContain('技术群');
    expect(wrapper.text()).toContain('返回群列表');
    expect(wrapper.find('[aria-label="群配置 ID"]').exists()).toBe(false);
    expect(api.getConfiguration).toHaveBeenCalledWith('00000000-0000-0000-0000-000000000801');
  });

  it('does not render an editable form when the route group is unavailable', async () => {
    const unavailableApi = {
      getConfiguration: vi.fn().mockRejectedValue({ response: { status: 404 } }),
      updateConfiguration: vi.fn(),
      previewRules: vi.fn()
    };
    const wrapper = mount(GroupRulesView, {
      props: { id: '00000000-0000-0000-0000-000000000801', api: unavailableApi },
      global: { stubs: { RouterLink: true } }
    });
    await flushPromises();

    expect(wrapper.text()).toContain('群不存在或已删除');
    expect(wrapper.find('[data-testid="save-configuration"]').exists()).toBe(false);
  });

  it('sends and advances the loaded configuration version and reloads a conflict', async () => {
    const configuration = {
      name: '并发测试群',
      rules: { include: [], exclude: [] },
      boundTagIds: [],
      availableTags: [],
      configurationVersion: 4,
      context: { configured: {}, effective: { senderIsolated: false, historyTurns: 6, idleTimeoutMinutes: 30, tokenCap: 3000, summaryEnabled: true, includeBotHistory: true } }
    };
    const concurrentApi = {
      getConfiguration: vi.fn()
        .mockResolvedValueOnce(configuration)
        .mockResolvedValueOnce({ ...configuration, configurationVersion: 6 }),
      updateConfiguration: vi.fn()
        .mockResolvedValueOnce({ ...configuration, configurationVersion: 5, clearedContextSessions: 0 })
        .mockRejectedValueOnce({ response: { status: 409, data: { error: 'group-configuration-conflict', currentVersion: 6 } } }),
      previewRules: vi.fn()
    };
    const wrapper = mount(GroupRulesView, {
      props: { id: 'group-1', api: concurrentApi },
      global: { stubs: { RouterLink: routerLinkStub } }
    });
    await flushPromises();

    await wrapper.get('[data-testid="save-configuration"]').trigger('click');
    await flushPromises();
    expect(concurrentApi.updateConfiguration).toHaveBeenNthCalledWith(
      1, 'group-1', expect.objectContaining({ expectedConfigurationVersion: 4 }));

    await wrapper.get('[data-testid="save-configuration"]').trigger('click');
    await flushPromises();
    expect(concurrentApi.updateConfiguration).toHaveBeenNthCalledWith(
      2, 'group-1', expect.objectContaining({ expectedConfigurationVersion: 5 }));
    expect(concurrentApi.getConfiguration).toHaveBeenCalledTimes(2);
    expect(wrapper.text()).toContain('群配置已被其他操作员修改，已加载最新版本');
  });

  it('does not expose retired group human-agent configuration', async () => {
    const gatedApi = {
      ...api,
      getConfiguration: vi.fn().mockResolvedValue({
        name: '客服群',
        rules: { include: [], exclude: [] },
        boundTagIds: [],
        availableTags: [],
        configurationVersion: 1,
        context: { configured: {}, effective: { senderIsolated: false, historyTurns: 6, idleTimeoutMinutes: 30, tokenCap: 3000, summaryEnabled: true, includeBotHistory: true } }
      }),
      getEligibleHumanAgents: vi.fn().mockResolvedValue({
        candidates: [{
          userId: 'agent-1',
          displayName: '客服甲',
          workToolDisplayName: '企微客服甲',
          verificationStatus: 'Stale',
          isEnabled: false,
          isDefault: false
        }],
        canConfigure: false,
        gateMessage: '需要先完成 WorkTool 群成员昵称结果验证，当前不能启用群客服。'
      })
    };
    const wrapper = mount(GroupRulesView, {
      props: { id: 'group-1', api: gatedApi },
      global: { stubs: { RouterLink: routerLinkStub } }
    });
    await flushPromises();

    expect(gatedApi.getEligibleHumanAgents).not.toHaveBeenCalled();
    expect(wrapper.text()).not.toContain('群人工客服');
    expect(wrapper.find('[data-testid="save-human-agents"]').exists()).toBe(false);
  });
});
