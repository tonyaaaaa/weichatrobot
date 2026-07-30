import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import GroupFixedRepliesPanel from './GroupFixedRepliesPanel.vue';
import {
  fixedReplyApi,
  type FixedReplyTemplate,
  type FixedReplyTemplateDraft
} from '../../api/fixedReplies';
import FixedReplyTemplateDialog from '../../views/fixed-replies/FixedReplyTemplateDialog.vue';

const { confirmAction, routerPush } = vi.hoisted(() => ({
  confirmAction: vi.fn().mockResolvedValue(true),
  routerPush: vi.fn()
}));

vi.mock('vue-router', () => ({
  useRouter: () => ({ push: routerPush })
}));

vi.mock('../../utils/dialogs', () => ({ confirmAction }));

vi.mock('../../api/groupOptions', () => ({
  groupOptionApi: {
    list: vi.fn().mockResolvedValue([
      {
        id: 'group-1',
        name: '机器人测试群1',
        robotName: '默认机器人',
        state: 'enabled',
        isEnabled: true
      }
    ])
  }
}));

vi.mock('../../api/fixedReplies', async importOriginal => {
  const actual = await importOriginal<typeof import('../../api/fixedReplies')>();
  return {
    ...actual,
    fixedReplyApi: {
      ...actual.fixedReplyApi,
      list: vi.fn(),
      listForGroup: vi.fn(),
      create: vi.fn(),
      update: vi.fn(),
      setEnabled: vi.fn(),
      preview: vi.fn(),
      includeForGroup: vi.fn(),
      excludeForGroup: vi.fn(),
      removeIncludeForGroup: vi.fn(),
      removeExcludeForGroup: vi.fn()
    }
  };
});

function template(overrides: Partial<FixedReplyTemplate> = {}): FixedReplyTemplate {
  return {
    id: '11111111-1111-1111-1111-111111111111',
    name: '签证进度',
    intentDescription: '询问签证办理进度',
    replyText: '请提供申请编号。',
    scopeType: 'Global',
    priority: 10,
    isEnabled: true,
    version: 2,
    examples: ['签证还有多久出来'],
    groupRules: [],
    updatedAtUtc: '2026-07-29T00:00:00Z',
    ...overrides
  };
}

describe('GroupFixedRepliesPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(fixedReplyApi.list).mockResolvedValue([]);
    vi.mocked(fixedReplyApi.listForGroup).mockResolvedValue([]);
    vi.mocked(fixedReplyApi.preview).mockResolvedValue({
      matched: false,
      decision: 'ContinueKnowledgeAnswer',
      reasonCode: 'no_exact_match'
    });
  });

  it('shows all templates and lets the group exclude an effective global template', async () => {
    const global = template();
    vi.mocked(fixedReplyApi.list).mockResolvedValue([global]);
    vi.mocked(fixedReplyApi.listForGroup).mockResolvedValue([{
      id: global.id,
      version: global.version,
      name: global.name,
      intentDescription: global.intentDescription,
      examples: global.examples,
      priority: global.priority,
      isGroupSpecific: false
    }]);
    vi.mocked(fixedReplyApi.excludeForGroup).mockResolvedValue({
      ...global,
      version: 3,
      groupRules: [{ groupProfileId: 'group-1', effect: 'Exclude' }]
    });

    const wrapper = mount(GroupFixedRepliesPanel, {
      props: { groupId: 'group-1' },
      global: { stubs: { RouterLink: true, FixedReplyTemplateDialog: true } }
    });
    await flushPromises();

    expect(wrapper.text()).toContain('全局模板');
    await wrapper.get(`[data-testid="exclude-${global.id}"]`).trigger('click');
    await flushPromises();
    expect(fixedReplyApi.excludeForGroup).toHaveBeenCalledWith('group-1', global.id, 2);
  });

  it('lets the group include a selected-groups template that is not active yet', async () => {
    const selected = template({
      id: '22222222-2222-2222-2222-222222222222',
      name: '材料清单',
      scopeType: 'SelectedGroups'
    });
    vi.mocked(fixedReplyApi.list).mockResolvedValue([selected]);
    vi.mocked(fixedReplyApi.includeForGroup).mockResolvedValue({
      ...selected,
      version: 3,
      groupRules: [{ groupProfileId: 'group-1', effect: 'Include' }]
    });

    const wrapper = mount(GroupFixedRepliesPanel, {
      props: { groupId: 'group-1' },
      global: { stubs: { RouterLink: true, FixedReplyTemplateDialog: true } }
    });
    await flushPromises();

    expect(wrapper.text()).toContain('未在本群启用');
    await wrapper.get(`[data-testid="include-${selected.id}"]`).trigger('click');
    await flushPromises();
    expect(fixedReplyApi.includeForGroup).toHaveBeenCalledWith('group-1', selected.id, 2);
  });

  it('creates a selected-groups template already bound to the current group', async () => {
    const created = template({
      id: '33333333-3333-3333-3333-333333333333',
      name: '当前群签证进度',
      scopeType: 'SelectedGroups',
      groupRules: [{ groupProfileId: 'group-1', effect: 'Include' }]
    });
    vi.mocked(fixedReplyApi.create).mockResolvedValue(created);
    const wrapper = mount(GroupFixedRepliesPanel, {
      props: { groupId: 'group-1', groupName: '机器人测试群1' },
      global: {
        stubs: {
          RouterLink: true,
          FixedReplyTemplateDialog: true,
          teleport: true
        }
      }
    });
    await flushPromises();

    await wrapper.get('[data-testid="create-group-fixed-reply"]').trigger('click');
    const dialog = wrapper.getComponent(FixedReplyTemplateDialog);
    expect(dialog.props('initialGroupId')).toBe('group-1');
    const draft: FixedReplyTemplateDraft = {
      name: '当前群签证进度',
      intentDescription: '询问签证进度',
      replyText: '签证结果会第一时间通知。',
      scopeType: 'SelectedGroups',
      priority: 0,
      isEnabled: true,
      examples: ['签证出了吗'],
      groupRules: [{ groupProfileId: 'group-1', effect: 'Include' }]
    };
    dialog.vm.$emit('save', draft);
    await flushPromises();

    expect(fixedReplyApi.create).toHaveBeenCalledWith(draft);
  });

  it('edits and disables a template from the group detail with an impact confirmation', async () => {
    const global = template({
      groupRules: [
        { groupProfileId: 'group-2', effect: 'Exclude' },
        { groupProfileId: 'group-3', effect: 'Exclude' }
      ]
    });
    vi.mocked(fixedReplyApi.list).mockResolvedValue([global]);
    vi.mocked(fixedReplyApi.update).mockResolvedValue({ ...global, version: 3 });
    vi.mocked(fixedReplyApi.setEnabled).mockResolvedValue({
      ...global,
      isEnabled: false,
      version: 3
    });
    const wrapper = mount(GroupFixedRepliesPanel, {
      props: { groupId: 'group-1', groupName: '机器人测试群1' },
      global: {
        stubs: {
          RouterLink: true,
          FixedReplyTemplateDialog: true,
          teleport: true
        }
      }
    });
    await flushPromises();

    await wrapper.get(`[data-testid="edit-${global.id}"]`).trigger('click');
    expect(confirmAction).toHaveBeenCalled();
    const draft: FixedReplyTemplateDraft = {
      name: global.name,
      intentDescription: global.intentDescription,
      replyText: '新的固定回复',
      scopeType: global.scopeType,
      priority: global.priority,
      isEnabled: true,
      examples: global.examples,
      groupRules: global.groupRules
    };
    wrapper.getComponent(FixedReplyTemplateDialog).vm.$emit('save', draft);
    await flushPromises();
    expect(fixedReplyApi.update).toHaveBeenCalledWith(
      global.id,
      global.version,
      draft
    );

    await wrapper.get(`[data-testid="disable-${global.id}"]`).trigger('click');
    await flushPromises();
    expect(fixedReplyApi.setEnabled).toHaveBeenCalledWith(
      global.id,
      global.version,
      false
    );
  });

  it('tests a question against the current group and displays the matched reply', async () => {
    vi.mocked(fixedReplyApi.preview).mockResolvedValue({
      matched: true,
      decision: 'MatchFixedTemplate',
      templateName: '签证进度',
      replyText: '签证结果会第一时间通知。'
    });
    const wrapper = mount(GroupFixedRepliesPanel, {
      props: { groupId: 'group-1', groupName: '机器人测试群1' },
      global: {
        stubs: {
          RouterLink: true,
          FixedReplyTemplateDialog: true,
          teleport: true
        }
      }
    });
    await flushPromises();

    await wrapper.get('[data-testid="test-group-fixed-reply"]').trigger('click');
    await wrapper.get('[data-testid="group-fixed-reply-question"]')
      .setValue('签证出了吗');
    await wrapper.get('[data-testid="run-group-fixed-reply-test"]').trigger('click');
    await flushPromises();

    expect(fixedReplyApi.preview).toHaveBeenCalledWith(
      'group-1',
      '签证出了吗'
    );
    expect(wrapper.text()).toContain('签证结果会第一时间通知');
  });

  it('opens the full template page with the current group filter', async () => {
    const wrapper = mount(GroupFixedRepliesPanel, {
      props: { groupId: 'group-1', groupName: '机器人测试群1' },
      global: { stubs: { RouterLink: true, FixedReplyTemplateDialog: true } }
    });
    await flushPromises();

    await wrapper.get('[data-testid="manage-all-fixed-replies"]').trigger('click');
    expect(routerPush).toHaveBeenCalledWith({
      name: 'fixed-replies',
      query: {
        groupId: 'group-1',
        groupName: '机器人测试群1'
      }
    });
  });

  it('keeps disabled templates visible so an administrator can enable them again', async () => {
    const disabled = template({ isEnabled: false });
    vi.mocked(fixedReplyApi.list).mockResolvedValue([disabled]);
    vi.mocked(fixedReplyApi.setEnabled).mockResolvedValue({
      ...disabled,
      isEnabled: true,
      version: 3
    });
    const wrapper = mount(GroupFixedRepliesPanel, {
      props: { groupId: 'group-1' },
      global: { stubs: { RouterLink: true, FixedReplyTemplateDialog: true } }
    });
    await flushPromises();

    expect(wrapper.text()).toContain('已停用');
    await wrapper.get(`[data-testid="enable-${disabled.id}"]`).trigger('click');
    await flushPromises();
    expect(fixedReplyApi.setEnabled).toHaveBeenCalledWith(
      disabled.id,
      disabled.version,
      true
    );
    expect(fixedReplyApi.list).toHaveBeenCalledWith({});
  });
});
