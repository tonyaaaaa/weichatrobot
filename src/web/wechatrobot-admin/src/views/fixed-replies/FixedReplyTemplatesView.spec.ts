import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fixedReplyApi } from '../../api/fixedReplies';
import FixedReplyTemplatesView from './FixedReplyTemplatesView.vue';
import FixedReplyTemplateDialog from './FixedReplyTemplateDialog.vue';

const routeQuery = vi.hoisted(() => ({
  groupId: 'group-1',
  groupName: '机器人测试群1'
}));

vi.mock('vue-router', () => ({
  useRoute: () => ({ query: routeQuery })
}));

vi.mock('../../api/fixedReplies', async importOriginal => {
  const actual = await importOriginal<typeof import('../../api/fixedReplies')>();
  return {
    ...actual,
    fixedReplyApi: {
      ...actual.fixedReplyApi,
      list: vi.fn().mockResolvedValue([])
    }
  };
});

vi.mock('../../api/groupOptions', () => ({
  groupOptionApi: {
    list: vi.fn().mockResolvedValue([])
  }
}));

describe('FixedReplyTemplatesView', () => {
  beforeEach(() => vi.clearAllMocks());

  it('loads the full template page with the group filter from navigation', async () => {
    const wrapper = mount(FixedReplyTemplatesView, {
      global: {
        stubs: {
          FixedReplyTemplateDialog: true,
          teleport: true
        }
      }
    });
    await flushPromises();

    expect(fixedReplyApi.list).toHaveBeenCalledWith({
      search: undefined,
      groupProfileId: 'group-1'
    });
    expect(wrapper.text()).toContain('机器人测试群1');
    expect(wrapper.text()).toContain('当前群筛选');

    await wrapper.get('[data-testid="create-fixed-reply"]').trigger('click');
    expect(wrapper.getComponent(FixedReplyTemplateDialog).props('initialGroupId'))
      .toBe('group-1');
  });
});
