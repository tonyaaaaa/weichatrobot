import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import GroupAdvancedSettingsPanel from '../../components/groups/GroupAdvancedSettingsPanel.vue';
import GroupContextMemoryPanel from '../../components/groups/GroupContextMemoryPanel.vue';
import GroupKnowledgeAnswerPanel from '../../components/groups/GroupKnowledgeAnswerPanel.vue';
import GroupRulesView from './GroupRulesView.vue';

vi.mock('../../utils/dialogs', () => ({
  confirmAction: vi.fn().mockResolvedValue(true)
}));

const groupId = '00000000-0000-0000-0000-000000000801';
const effective = {
  senderIsolated: false,
  historyTurns: 6,
  idleTimeoutMinutes: 30,
  tokenCap: 3000,
  summaryEnabled: true,
  includeBotHistory: true
};
const answerFallback = {
  webSearchEnabled: false,
  modelKnowledgeFallbackEnabled: false,
  webSearchShowSources: false,
  webSearchResultCount: 5,
  webSearchRecency: 'NoLimit',
  webSearchDomainFilter: null,
  webSearchContentSize: 'Medium',
  finalNoEvidencePolicy: 'InsufficientEvidence'
};

function configuration(overrides: Record<string, unknown> = {}) {
  return {
    id: groupId,
    name: '技术支持群',
    identity: {
      robotName: '默认机器人',
      workToolGroupRemark: '售后支持',
      registrationSource: 'WorkToolImport',
      state: 'enabled',
      isEnabled: true,
      stateVersion: 2
    },
    rules: { include: [], exclude: [] },
    boundTagIds: [],
    allowedTagIds: [],
    availableTags: [],
    tagVisibility: 'any-bound-tag-or-global-public',
    context: { configured: {}, effective },
    answerFallback,
    defaultChatModel: {
      isConfigured: true,
      configurationName: 'glm',
      connectionStatus: 'Succeeded',
      webSearchMode: 'ZaiChatCompletions',
      canUseWebSearch: true,
      unavailableReason: 'none'
    },
    memorySummary: {
      activeGroupMemoryCount: 2,
      activeMemberMemoryCount: 5,
      pendingCandidateCount: 3,
      pendingOrRunningJobCount: 1
    },
    clearedContextSessions: 0,
    configurationVersion: 4,
    ...overrides
  };
}

function api(initial = configuration()) {
  return {
    getConfiguration: vi.fn().mockResolvedValue(initial),
    updateConfiguration: vi.fn().mockImplementation(async (_id, request) =>
      configuration({
        rules: { include: request.includeRules, exclude: request.excludeRules },
        boundTagIds: request.boundTagIds,
        context: { configured: request.context, effective },
        answerFallback: request.answerFallback,
        configurationVersion: request.expectedConfigurationVersion + 1
      })),
    previewRules: vi.fn().mockResolvedValue({
      results: [{ groupName: '技术测试群', isMatch: false, isExcluded: true }]
    }),
    clearContext: vi.fn().mockResolvedValue({
      clearedSessions: 2,
      configurationVersion: 5
    })
  };
}

const routerLinkStub = {
  props: ['to'],
  template: '<a><slot /></a>'
};

function mountView(service = api()) {
  return mount(GroupRulesView, {
    props: { id: groupId, api: service },
    global: { stubs: { RouterLink: routerLinkStub } }
  });
}

async function openTab(wrapper: ReturnType<typeof mount>, label: string) {
  const tab = wrapper.findAll('[role="tab"]').find(item => item.text() === label);
  if (!tab) throw new Error(`Tab not found: ${label}`);
  await tab.trigger('click');
  await flushPromises();
}

async function addRule(
  wrapper: ReturnType<typeof mount>,
  direction: 'include' | 'exclude',
  kind: 'exact' | 'contains' | 'regex'
) {
  wrapper.getComponent(GroupAdvancedSettingsPanel).vm.$emit('add', direction, kind);
  await wrapper.vm.$nextTick();
}

describe('GroupRulesView', () => {
  beforeEach(() => vi.clearAllMocks());

  it('renders the approved business-first group detail layout without a raw id', async () => {
    const wrapper = mountView();
    await flushPromises();

    expect(wrapper.text()).toContain('技术支持群');
    expect(wrapper.text()).toContain('默认机器人');
    expect(wrapper.text()).toContain('售后支持');
    expect(wrapper.text()).toContain('WorkTool 导入');
    expect(wrapper.findAll('[role="tab"]').map(tab => tab.text())).toEqual([
      '知识与回答',
      '上下文与记忆',
      '运行记录',
      '高级设置'
    ]);
    expect(wrapper.get('[role="tab"][aria-selected="true"]').text()).toBe('知识与回答');
    expect(wrapper.find('[aria-label="群配置 ID"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="save-configuration"]').exists()).toBe(false);
    expect(wrapper.text()).not.toContain('群人工客服');
  });

  it('keeps one dirty draft across tabs and shows the save bar only after editing', async () => {
    const wrapper = mountView();
    await flushPromises();
    await openTab(wrapper, '高级设置');
    await addRule(wrapper, 'include', 'exact');

    expect(wrapper.find('[data-testid="save-configuration"]').exists()).toBe(true);
    expect(wrapper.getComponent(GroupAdvancedSettingsPanel).props('includeRules')).toHaveLength(1);

    await openTab(wrapper, '知识与回答');
    await openTab(wrapper, '高级设置');
    expect(wrapper.getComponent(GroupAdvancedSettingsPanel).props('includeRules')).toHaveLength(1);
  });

  it('saves the complete draft with the loaded version and becomes clean', async () => {
    const service = api();
    const wrapper = mountView(service);
    await flushPromises();
    await openTab(wrapper, '高级设置');
    await addRule(wrapper, 'exclude', 'contains');
    await wrapper.get('[data-testid="save-configuration"]').trigger('click');
    await flushPromises();

    expect(service.updateConfiguration).toHaveBeenCalledWith(
      groupId,
      expect.objectContaining({
        expectedConfigurationVersion: 4,
        clearContext: false,
        excludeRules: [expect.objectContaining({ patternKind: 'contains' })]
      })
    );
    expect(wrapper.find('[data-testid="save-configuration"]').exists()).toBe(false);
  });

  it('reloads authoritative configuration after a version conflict', async () => {
    const latest = configuration({ name: '并发更新后的群', configurationVersion: 8 });
    const service = api();
    service.updateConfiguration.mockRejectedValueOnce({
      response: { status: 409, data: { error: 'group-configuration-conflict' } }
    });
    service.getConfiguration
      .mockResolvedValueOnce(configuration())
      .mockResolvedValueOnce(latest);
    const wrapper = mountView(service);
    await flushPromises();
    await openTab(wrapper, '高级设置');
    await addRule(wrapper, 'include', 'exact');
    await wrapper.get('[data-testid="save-configuration"]').trigger('click');
    await flushPromises();

    expect(service.getConfiguration).toHaveBeenCalledTimes(2);
    expect(wrapper.text()).toContain('并发更新后的群');
    expect(wrapper.find('[data-testid="save-configuration"]').exists()).toBe(false);
  });

  it('clears context through the dedicated endpoint without saving the draft', async () => {
    const service = api();
    const wrapper = mountView(service);
    await flushPromises();
    await openTab(wrapper, '高级设置');
    await addRule(wrapper, 'include', 'regex');
    await openTab(wrapper, '上下文与记忆');
    wrapper.getComponent(GroupContextMemoryPanel).vm.$emit('clear-context');
    await flushPromises();

    expect(service.clearContext).toHaveBeenCalledWith(groupId, 4);
    expect(service.updateConfiguration).not.toHaveBeenCalled();
    expect(wrapper.find('[data-testid="save-configuration"]').exists()).toBe(true);
  });

  it('keeps disabled bound tags removable and disables unavailable tags', async () => {
    const wrapper = mountView(api(configuration({
      boundTagIds: ['bound'],
      availableTags: [
        { id: 'bound', name: '历史标签', isGlobalPublic: false, isEnabled: false, isBound: true },
        { id: 'unbound', name: '停用标签', isGlobalPublic: false, isEnabled: false, isBound: false }
      ]
    })));
    await flushPromises();

    const tags = wrapper.getComponent(GroupKnowledgeAnswerPanel).props('availableTags') as Array<{
      id: string;
      isEnabled: boolean;
      isBound: boolean;
    }>;
    expect(tags.find(tag => tag.id === 'bound')).toMatchObject({ isEnabled: false, isBound: true });
    expect(tags.find(tag => tag.id === 'unbound')).toMatchObject({ isEnabled: false, isBound: false });
  });

  it('keeps advanced matching and preview separate from the default business tab', async () => {
    const service = api();
    const wrapper = mountView(service);
    await flushPromises();

    expect(wrapper.get('[role="tab"][aria-selected="true"]').text()).toBe('知识与回答');
    await openTab(wrapper, '高级设置');
    expect(wrapper.get('[role="tab"][aria-selected="true"]').text()).toBe('高级设置');
    expect(wrapper.getComponent(GroupAdvancedSettingsPanel).props('includeRules')).toEqual([]);
    wrapper.getComponent(GroupAdvancedSettingsPanel).vm.$emit('preview');
    await flushPromises();
    expect(service.previewRules).toHaveBeenCalledOnce();
    expect(wrapper.getComponent(GroupAdvancedSettingsPanel).props('previewResults')).toEqual([
      { groupName: '技术测试群', isMatch: false, isExcluded: true }
    ]);
  });

  it('does not render editable configuration when the group is unavailable', async () => {
    const service = api();
    service.getConfiguration.mockRejectedValueOnce({ response: { status: 404 } });
    const wrapper = mountView(service);
    await flushPromises();

    expect(wrapper.text()).toContain('群不存在或已删除');
    expect(wrapper.find('.group-detail-tabs').exists()).toBe(false);
  });

  it('offers a retry action after a transient load failure', async () => {
    const service = api();
    service.getConfiguration
      .mockRejectedValueOnce({ response: { status: 503 } })
      .mockResolvedValueOnce(configuration());
    const wrapper = mountView(service);
    await flushPromises();

    expect(wrapper.text()).toContain('群配置加载失败');
    await wrapper.get('[data-testid="retry-group-configuration"]').trigger('click');
    await flushPromises();

    expect(service.getConfiguration).toHaveBeenCalledTimes(2);
    expect(wrapper.text()).toContain('技术支持群');
  });
});
