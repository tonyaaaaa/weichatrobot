import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import AdministrationAuditView from './AdministrationAuditView.vue';

describe('AdministrationAuditView', () => {
  it('filters and renders sanitized administration details', async () => {
    const api = {
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

    await wrapper.get('[data-testid="administration-audit-actor"]').setValue('admin');
    await wrapper.get('[data-testid="apply-administration-audit-filters"]').trigger('click');
    await flushPromises();
    expect(api.list).toHaveBeenLastCalledWith(expect.objectContaining({
      actor: 'admin',
      page: 1,
      pageSize: 20
    }));
  });
});
