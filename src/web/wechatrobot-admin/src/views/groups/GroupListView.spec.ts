import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import GroupListView from './GroupListView.vue';

const routerLinkStub = {
  props: ['to'],
  template: '<a :data-to="JSON.stringify(to)"><slot /></a>'
};

describe('GroupListView', () => {
  it('lists registered groups and opens configuration with the generated id', async () => {
    const id = '00000000-0000-0000-0000-000000000801';
    const api = {
      listGroups: vi.fn().mockResolvedValue([{
        id,
        robotConfigId: '00000000-0000-0000-0000-000000000901',
        robotName: '客服机器人',
        name: '技术群',
        workToolGroupRemark: 'tech-east',
        isEnabled: true,
        updatedAtUtc: '2026-07-25T01:02:03Z'
      }])
    };
    const wrapper = mount(GroupListView, {
      props: { api },
      global: { stubs: { RouterLink: routerLinkStub } }
    });
    await flushPromises();

    expect(wrapper.text()).toContain('技术群');
    expect(wrapper.text()).toContain('客服机器人');
    expect(wrapper.text()).toContain('启用');
    expect(wrapper.get('[data-testid="configure-group"]').attributes('data-to')).toContain(id);
    expect(wrapper.find('[aria-label="群配置 ID"]').exists()).toBe(false);
  });

  it('shows an actionable empty state when no group is registered', async () => {
    const wrapper = mount(GroupListView, {
      props: { api: { listGroups: vi.fn().mockResolvedValue([]) } },
      global: { stubs: { RouterLink: routerLinkStub } }
    });
    await flushPromises();

    expect(wrapper.text()).toContain('暂无已登记群');
    expect(wrapper.text()).toContain('前往群操作登记');
  });

  it('shows a failure state without stale rows when loading fails', async () => {
    const wrapper = mount(GroupListView, {
      props: { api: { listGroups: vi.fn().mockRejectedValue(new Error('network')) } },
      global: { stubs: { RouterLink: routerLinkStub } }
    });
    await flushPromises();

    expect(wrapper.text()).toContain('群列表加载失败，请稍后重试');
    expect(wrapper.find('[data-testid="group-row"]').exists()).toBe(false);
  });
});
