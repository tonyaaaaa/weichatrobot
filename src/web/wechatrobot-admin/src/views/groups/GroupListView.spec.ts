import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import GroupListView from './GroupListView.vue';

vi.mock('../../utils/dialogs', () => ({
  confirmAction: vi.fn().mockResolvedValue(true)
}));

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
        state: 'enabled',
        stateVersion: 2,
        configurationVersion: 3,
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
    expect(wrapper.text()).toContain('停用');
    expect(wrapper.text()).toContain('会话');
    expect(wrapper.text()).toContain('上下文');
    expect(wrapper.text()).toContain('记忆');
    expect(wrapper.get('[data-testid="configure-group"]').attributes('data-to')).toContain(id);
    expect(wrapper.find('[aria-label="群配置 ID"]').exists()).toBe(false);
  });

  it('changes lifecycle state with the current state version and reloads the current filter', async () => {
    const id = '00000000-0000-0000-0000-000000000802';
    const api = {
      listGroups: vi.fn().mockResolvedValue([{
        id,
        robotConfigId: 'robot-1',
        robotName: '客服机器人',
        name: '停用测试群',
        isEnabled: true,
        state: 'enabled',
        stateVersion: 4,
        configurationVersion: 1,
        updatedAtUtc: '2026-07-25T01:02:03Z'
      }])
    };
    const lifecycleApi = {
      changeState: vi.fn().mockResolvedValue({
        id,
        state: 'disabled',
        isEnabled: false,
        stateVersion: 5
      })
    };
    const wrapper = mount(GroupListView, {
      props: { api, lifecycleApi },
      global: { stubs: { RouterLink: routerLinkStub } }
    });
    await flushPromises();

    await wrapper.get('[data-testid="disable-group"]').trigger('click');
    await flushPromises();

    expect(lifecycleApi.changeState).toHaveBeenCalledWith(id, 'disable', 4);
    expect(api.listGroups).toHaveBeenLastCalledWith('current');
  });

  it('hides archived groups by default and can explicitly display them', async () => {
    const api = {
      listGroups: vi.fn()
        .mockResolvedValueOnce([])
        .mockResolvedValueOnce([{
          id: 'archived-1',
          robotConfigId: 'robot-1',
          robotName: '客服机器人',
          name: '已归档群',
          isEnabled: false,
          state: 'archived',
          stateVersion: 7,
          configurationVersion: 2,
          archivedAtUtc: '2026-07-25T01:02:03Z',
          updatedAtUtc: '2026-07-25T01:02:03Z'
        }])
    };
    const wrapper = mount(GroupListView, {
      props: { api, lifecycleApi: { changeState: vi.fn() } },
      global: { stubs: { RouterLink: routerLinkStub } }
    });
    await flushPromises();

    await wrapper.get('[data-testid="group-status-filter"]').setValue('archived');
    await flushPromises();

    expect(api.listGroups).toHaveBeenLastCalledWith('archived');
    expect(wrapper.text()).toContain('已归档群');
    expect(wrapper.text()).toContain('恢复');
    expect(wrapper.find('[data-testid="configure-group"]').exists()).toBe(false);
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

  it('loads remote groups by selected robot and imports only checked available rows', async () => {
    const api = {
      listGroups: vi.fn().mockResolvedValue([]),
      listRobots: vi.fn().mockResolvedValue([
        { id: 'robot-1', name: '客服机器人', isEnabled: true }
      ]),
      listRemoteGroups: vi.fn().mockResolvedValue({
        pageNumber: 1,
        pageSize: 50,
        totalPages: 1,
        total: 2,
        items: [
          {
            groupName: '可导入群',
            masterName: '群主甲',
            membersCount: 8,
            importState: 'Available'
          },
          {
            groupName: '已登记群',
            masterName: '群主乙',
            membersCount: 5,
            importState: 'Imported'
          }
        ]
      }),
      importRemoteGroups: vi.fn().mockResolvedValue([
        {
          groupName: '可导入群',
          status: 'Imported',
          groupProfileId: 'group-1'
        }
      ])
    };
    const wrapper = mount(GroupListView, {
      props: { api },
      global: { stubs: { RouterLink: routerLinkStub } }
    });
    await flushPromises();

    await wrapper.get('[data-testid="remote-robot"]').setValue('robot-1');
    await wrapper.get('[data-testid="load-remote-groups"]').trigger('click');
    await flushPromises();
    expect(wrapper.text()).toContain('WorkTool 已将该群列表接口标记为将废弃');
    expect(wrapper.get('[data-testid="select-remote-可导入群"]').attributes('disabled'))
      .toBeUndefined();
    expect(wrapper.get('[data-testid="select-remote-已登记群"]').attributes('disabled'))
      .toBeDefined();

    await wrapper.get('[data-testid="select-remote-可导入群"]').setValue(true);
    await wrapper.get('[data-testid="import-remote-groups"]').trigger('click');
    await flushPromises();

    expect(api.importRemoteGroups).toHaveBeenCalledWith(
      'robot-1',
      [{ groupName: '可导入群', expectedImportState: 'Available' }]
    );
  });
});
