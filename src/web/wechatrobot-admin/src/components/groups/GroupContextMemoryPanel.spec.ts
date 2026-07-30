import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import GroupContextMemoryPanel from './GroupContextMemoryPanel.vue';

describe('GroupContextMemoryPanel', () => {
  it('renders effective context values and real memory summary counts', async () => {
    const wrapper = mount(GroupContextMemoryPanel, {
      props: {
        configured: {},
        effective: {
          senderIsolated: false,
          historyTurns: 6,
          idleTimeoutMinutes: 30,
          tokenCap: 3000,
          summaryEnabled: true,
          includeBotHistory: true
        },
        memorySummary: {
          activeGroupMemoryCount: 2,
          activeMemberMemoryCount: 5,
          pendingCandidateCount: 3,
          pendingOrRunningJobCount: 1
        },
        groupId: 'group-1'
      },
      global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } }
    });

    expect(wrapper.text()).toContain('有效值：6');
    expect(wrapper.text()).toContain('有效值：3000');
    expect(wrapper.get('[data-testid="memory-group-count"]').text()).toContain('2');
    expect(wrapper.get('[data-testid="memory-member-count"]').text()).toContain('5');
    expect(wrapper.get('[data-testid="memory-candidate-count"]').text()).toContain('3');
    expect(wrapper.get('[data-testid="memory-job-count"]').text()).toContain('1');
    await wrapper.get('[data-testid="clear-context"]').trigger('click');
    expect(wrapper.emitted('clear-context')).toHaveLength(1);
  });
});
