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
});
