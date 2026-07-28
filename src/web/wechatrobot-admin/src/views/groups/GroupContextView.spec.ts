import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import GroupContextView from './GroupContextView.vue';

vi.mock('../../utils/dialogs', () => ({
  confirmAction: vi.fn().mockResolvedValue(true)
}));

const routerLinkStub = {
  props: ['to'],
  template: '<a :data-to="JSON.stringify(to)"><slot /></a>'
};

describe('GroupContextView', () => {
  it('shows effective context previews and clears with the current configuration version', async () => {
    const api = {
      getContext: vi.fn().mockResolvedValue({
        groupId: 'group-1',
        configurationVersion: 6,
        total: 1,
        page: 1,
        pageSize: 20,
        items: [{
          sessionId: 'session-1',
          senderDisplayName: '客户甲',
          scope: 'sender:abc123',
          summary: '较早对话摘要',
          clearedThroughSequence: 0,
          lastActivityAtUtc: '2026-07-28T01:00:00Z',
          version: 2,
          messages: [
            { role: 'user', content: '如何办理？', createdAtUtc: '2026-07-28T01:00:00Z' },
            { role: 'assistant', content: '请准备材料。', createdAtUtc: '2026-07-28T01:00:01Z' }
          ],
          wasIdleReset: false,
          wasTokenLimited: false,
          contextTokenCount: 24
        }]
      }),
      clearContext: vi.fn().mockResolvedValue({
        clearedSessions: 1,
        configurationVersion: 6
      })
    };
    const wrapper = mount(GroupContextView, {
      props: { id: 'group-1', api },
      global: { stubs: { RouterLink: routerLinkStub } }
    });
    await flushPromises();

    expect(wrapper.text()).toContain('客户甲');
    expect(wrapper.text()).toContain('较早对话摘要');
    expect(wrapper.text()).toContain('如何办理？');
    expect(wrapper.text()).toContain('请准备材料。');

    await wrapper.get('[data-testid="clear-group-context"]').trigger('click');
    await flushPromises();

    expect(api.clearContext).toHaveBeenCalledWith('group-1', 6);
    expect(wrapper.text()).toContain('已清空 1 个会话的短期上下文');
  });
});
