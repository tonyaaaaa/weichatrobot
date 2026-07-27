import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import ConversationAuditView from './ConversationAuditView.vue';

describe('ConversationAuditView filters', () => {
  it('converts local datetimes to UTC and resets pagination when applying filters', async () => {
    const api = {
      capability: vi.fn().mockResolvedValue({
        available: true, items: [], total: 0, page: 1, pageSize: 20
      })
    };
    const wrapper = mount(ConversationAuditView, { props: { api } });
    await flushPromises();

    await wrapper.get('[data-testid="audit-group-id"]').setValue(
      '00000000-0000-0000-0000-000000000801');
    await wrapper.get('[data-testid="audit-from"]').setValue('2026-07-24T08:00');
    await wrapper.get('[data-testid="audit-to"]').setValue('2026-07-25T08:00');
    await wrapper.get('[data-testid="apply-audit-filters"]').trigger('click');
    await flushPromises();

    expect(api.capability).toHaveBeenLastCalledWith({
      groupId: '00000000-0000-0000-0000-000000000801',
      fromUtc: new Date('2026-07-24T08:00').toISOString(),
      toUtc: new Date('2026-07-25T08:00').toISOString(),
      page: 1,
      pageSize: 20
    });
    expect(wrapper.text()).toContain('开始时间包含，结束时间不包含');
  });
});
