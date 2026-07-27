import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import AdministrationAuditView from './AdministrationAuditView.vue';

describe('AdministrationAuditView', () => {
  it('filters and renders sanitized administration details', async () => {
    const api = {
      filterOptions: vi.fn().mockResolvedValue({
        actors: ['admin@example.test'],
        actions: ['user_created'],
        targetTypes: ['ApplicationUser'],
        targets: [{
          targetType: 'ApplicationUser',
          targetId: 'user-1',
          label: 'ApplicationUser · user-1'
        }]
      }),
      list: vi.fn().mockResolvedValue({
        items: [{
          id: 'audit-1',
          actor: 'admin@example.test',
          action: 'user_created',
          targetType: 'ApplicationUser',
          targetId: 'user-1',
          detail: { email: 'agent@example.test', apiKey: 'should-be-redacted-client-side' },
          createdAtUtc: '2026-07-24T00:00:00Z'
        }],
        total: 1,
        page: 1,
        pageSize: 20
      })
    };
    const wrapper = mount(AdministrationAuditView, { props: { api } });
    await flushPromises();

    expect(wrapper.text()).toContain('user_created');
    expect(wrapper.text()).toContain('agent@example.test');
    expect(wrapper.text()).not.toContain('should-be-redacted-client-side');

    const selectors = wrapper.findAllComponents({ name: 'ElSelect' });
    const actor = selectors.find(component => component.attributes('data-testid') === 'administration-audit-actor');
    const targetType = selectors.find(component => component.attributes('data-testid') === 'administration-audit-target-type');
    const target = selectors.find(component => component.attributes('data-testid') === 'administration-audit-target');
    expect(actor).toBeDefined();
    expect(targetType).toBeDefined();
    expect(target).toBeDefined();
    actor!.vm.$emit('update:modelValue', 'admin@example.test');
    targetType!.vm.$emit('update:modelValue', 'ApplicationUser');
    await flushPromises();
    target!.vm.$emit('update:modelValue', 'user-1');
    await wrapper.vm.$nextTick();
    await wrapper.get('[data-testid="apply-administration-audit-filters"]').trigger('click');
    await flushPromises();
    expect(api.list).toHaveBeenLastCalledWith(expect.objectContaining({
      actor: 'admin@example.test',
      targetType: 'ApplicationUser',
      targetId: 'user-1',
      page: 1,
      pageSize: 20
    }));
    expect(wrapper.find('label input').exists()).toBe(true);
    expect(wrapper.text()).not.toContain('目标 ID');
  });
});
