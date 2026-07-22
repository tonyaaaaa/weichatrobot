import { describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import GroupOperationsView from './GroupOperationsView.vue';

describe('GroupOperationsView', () => {
  it('requires an explicit manual invitation acknowledgement before registering an existing group', async () => {
    const api = { listGroups: vi.fn().mockResolvedValue([]), registerExistingGroup: vi.fn(), preview: vi.fn(), execute: vi.fn(), listOperations: vi.fn().mockResolvedValue([]) };
    const wrapper = mount(GroupOperationsView, { props: { api } });
    await wrapper.get('[data-testid="register-existing-group"]').trigger('click');
    expect(wrapper.text()).toContain('先由人工在企业微信中邀请机器人入群');
    await wrapper.get('[data-testid="manual-invitation-completed"]').setValue(true);
    await wrapper.get('[data-testid="register-existing-group"]').trigger('click');
    expect(api.registerExistingGroup).toHaveBeenCalledWith(expect.objectContaining({ manualInvitationCompleted: true }));
  });

  it('loads registered groups and selecting one populates the operation form', async () => {
    const api = {
      listGroups: vi.fn().mockResolvedValue([{ id: 'known-1', robotConfigId: 'robot-config-1', externalGroupId: 'group-reference-1', name: '技术支持群' }]),
      registerExistingGroup: vi.fn(), preview: vi.fn(), execute: vi.fn(), listOperations: vi.fn().mockResolvedValue([])
    };
    const wrapper = mount(GroupOperationsView, { props: { api } });
    await Promise.resolve(); await wrapper.vm.$nextTick();

    expect(wrapper.text()).toContain('技术支持群');
    await wrapper.get('[data-testid="select-known-group-known-1"]').trigger('click');
    expect((wrapper.get('[data-testid="operation-robot-config-id"]').element as HTMLInputElement).value).toBe('robot-config-1');
    expect((wrapper.get('[data-testid="operation-group-name"]').element as HTMLInputElement).value).toBe('技术支持群');
  });
});
