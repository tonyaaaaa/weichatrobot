import { flushPromises, shallowMount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import type { FixedReplyTemplateDraft } from '../../api/fixedReplies';
import FixedReplyTemplateDialog from './FixedReplyTemplateDialog.vue';

const { groupList } = vi.hoisted(() => ({
  groupList: vi.fn().mockResolvedValue([
    {
      id: 'group-1',
      name: '机器人测试群1',
      robotName: '默认机器人',
      state: 'enabled',
      isEnabled: true
    }
  ])
}));

vi.mock('../../api/groupOptions', () => ({
  groupOptionApi: {
    list: groupList
  }
}));

describe('FixedReplyTemplateDialog', () => {
  it('defaults a group-detail creation to a selected-groups template for that group', async () => {
    const wrapper = shallowMount(FixedReplyTemplateDialog, {
      props: {
        modelValue: true,
        initialGroupId: 'group-1'
      },
      global: { stubs: { teleport: true } }
    });
    await flushPromises();

    const vm = wrapper.vm as unknown as {
      form: FixedReplyTemplateDraft;
    };
    expect(vm.form.scopeType).toBe('SelectedGroups');
    expect(vm.form.groupRules).toEqual([
      { groupProfileId: 'group-1', effect: 'Include' }
    ]);
    expect(wrapper.findComponent({ name: 'ElDialog' }).classes())
      .toContain('fixed-reply-template-dialog');
  });

  it('does not load group options before the dialog is opened', async () => {
    groupList.mockClear();
    shallowMount(FixedReplyTemplateDialog, {
      props: { modelValue: false }
    });
    await flushPromises();

    expect(groupList).not.toHaveBeenCalled();
  });
});
