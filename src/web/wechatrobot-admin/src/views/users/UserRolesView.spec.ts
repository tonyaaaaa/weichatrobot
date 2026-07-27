import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import UserRolesView from './UserRolesView.vue';
import type {
  ManagedUser,
  SystemRole,
  UserAdministrationApi
} from '../../api/users';

function user(overrides: Partial<ManagedUser> = {}): ManagedUser {
  return {
    id: 'user-1',
    email: 'admin@example.test',
    displayName: '管理员',
    isEnabled: true,
    roles: ['Admin'],
    ...overrides
  };
}

function createApi(initial = [user()]): UserAdministrationApi {
  let items = initial.map(item => ({ ...item, roles: [...item.roles] }));
  return {
    list: vi.fn(async params => {
      const filtered = items.filter(item =>
        (!params.q || item.email.includes(params.q) || item.displayName.includes(params.q))
        && (params.state === 'all' || params.state === undefined
          || item.isEnabled === (params.state === 'enabled')));
      const start = (params.page - 1) * params.pageSize;
      return {
        items: filtered.slice(start, start + params.pageSize),
        total: filtered.length,
        page: params.page,
        pageSize: params.pageSize
      };
    }),
    roles: vi.fn(async (): Promise<SystemRole[]> =>
      ['Admin', 'KnowledgeOperator', 'HumanAgent']),
    create: vi.fn(async request => {
      const created = user({
        id: `user-${items.length + 1}`,
        email: request.email,
        displayName: request.displayName,
        roles: [...request.roles]
      });
      items.push(created);
      return created;
    }),
    setEnabled: vi.fn(async (id, isEnabled) => {
      const target = items.find(item => item.id === id)!;
      target.isEnabled = isEnabled;
      return { ...target };
    }),
    setRoles: vi.fn(async (id, roles) => {
      const target = items.find(item => item.id === id)!;
      target.roles = [...roles];
      return { ...target };
    }),
    setWorkToolDisplayName: vi.fn(async (id, displayName) => {
      const target = items.find(item => item.id === id)!;
      target.workToolDisplayName = displayName;
      return { ...target };
    }),
    clearWorkToolDisplayName: vi.fn(async id => {
      const target = items.find(item => item.id === id)!;
      target.workToolDisplayName = null;
      return { ...target };
    })
  };
}

describe('UserRolesView', () => {
  it('creates a user with a write-only temporary password and selected roles', async () => {
    const api = createApi();
    const wrapper = mount(UserRolesView, { props: { api } });
    await flushPromises();

    expect(wrapper.text()).not.toContain('后端暂未提供用户与角色管理 API');
    await wrapper.get('[data-testid="create-user"]').trigger('click');
    await wrapper.get('[data-testid="user-email"]').setValue('agent@example.test');
    await wrapper.get('[data-testid="user-display-name"]').setValue('客服一号');
    await wrapper.get('[data-testid="user-temporary-password"]').setValue('Temporary1!Password');
    await wrapper.get('[data-testid="create-role-HumanAgent"]').setValue(true);
    await wrapper.get('[data-testid="save-user"]').trigger('click');
    await flushPromises();

    expect(api.create).toHaveBeenCalledWith({
      email: 'agent@example.test',
      displayName: '客服一号',
      temporaryPassword: 'Temporary1!Password',
      roles: ['HumanAgent']
    });
  });

  it('confirms enable changes and saves role selections', async () => {
    const api = createApi();
    const confirmAction = vi.fn().mockResolvedValue(true);
    const wrapper = mount(UserRolesView, { props: { api, confirmAction } });
    await flushPromises();

    await wrapper.get('[data-testid="toggle-user-user-1"]').trigger('click');
    await flushPromises();
    expect(confirmAction).toHaveBeenCalledWith('确认停用“管理员”？该账号现有登录令牌将失效。');
    expect(api.setEnabled).toHaveBeenCalledWith('user-1', false);

    await wrapper.get('[data-testid="edit-roles-user-1"]').trigger('click');
    await wrapper.get('[data-testid="role-KnowledgeOperator"]').setValue(true);
    await wrapper.get('[data-testid="save-roles"]').trigger('click');
    await flushPromises();
    expect(api.setRoles).toHaveBeenCalledWith(
      'user-1',
      ['Admin', 'KnowledgeOperator']
    );
  });

  it('resets pagination on filters and explains last administrator protection', async () => {
    const api = createApi(Array.from({ length: 21 }, (_, index) =>
      user({ id: `user-${index + 1}`, email: `admin${index + 1}@example.test` })));
    api.setEnabled = vi.fn().mockRejectedValue({
      response: { data: { error: 'last-enabled-admin' } }
    });
    const wrapper = mount(UserRolesView, {
      props: { api, confirmAction: () => true }
    });
    await flushPromises();

    await wrapper.get('[data-testid="next-page"]').trigger('click');
    await flushPromises();
    expect(api.list).toHaveBeenLastCalledWith(expect.objectContaining({ page: 2 }));

    await wrapper.get('[data-testid="user-state-filter"]').setValue('disabled');
    await flushPromises();
    expect(api.list).toHaveBeenLastCalledWith(expect.objectContaining({
      state: 'disabled',
      page: 1
    }));

    await wrapper.get('[data-testid="user-state-filter"]').setValue('all');
    await flushPromises();
    await wrapper.get('[data-testid="toggle-user-user-1"]').trigger('click');
    await flushPromises();
    expect(wrapper.text()).toContain('系统必须保留至少一个已启用的管理员。');
  });

  it('binds an explicit WorkTool nickname only for eligible users', async () => {
    const api = createApi([
      user({
        id: 'agent-1',
        displayName: '后台客服名称',
        roles: ['HumanAgent'],
        workToolDisplayName: null
      }),
      user({
        id: 'operator-1',
        displayName: '知识运营',
        roles: ['KnowledgeOperator']
      })
    ]);
    const wrapper = mount(UserRolesView, { props: { api } });
    await flushPromises();

    expect(wrapper.text()).toContain('仅 Admin 或 HumanAgent 可绑定');
    await wrapper.get('[data-testid="bind-worktool-agent-1"]').trigger('click');
    await wrapper.get('[data-testid="worktool-display-name"]').setValue('  企微客服甲  ');
    await wrapper.get('[data-testid="save-worktool-display-name"]').trigger('click');
    await flushPromises();

    expect(api.setWorkToolDisplayName).toHaveBeenCalledWith('agent-1', '企微客服甲');
    expect(wrapper.text()).toContain('企微客服甲');
    expect(api.setWorkToolDisplayName).not.toHaveBeenCalledWith(
      'agent-1',
      '后台客服名称'
    );
  });
});
