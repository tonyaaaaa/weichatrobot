import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const confirmAction = vi.hoisted(() => vi.fn());
vi.mock('../../utils/dialogs', () => ({ confirmAction }));

import SendQueueView from './SendQueueView.vue';

describe('SendQueueView', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    confirmAction.mockResolvedValue(true);
  });

  it('shows safe queue metadata and actions only for allowed states', async () => {
    const api = createApi();
    const wrapper = mount(SendQueueView, { props: { api } });
    await flushPromises();

    expect(wrapper.text()).toContain('发送队列');
    expect(wrapper.text()).toContain('待发送');
    expect(wrapper.text()).toContain('投递结果未知');
    expect(wrapper.text()).toContain('发送中');
    expect(wrapper.text()).not.toContain('secret message');
    expect(wrapper.findAll('[data-testid^="cancel-command-"]')).toHaveLength(1);
    expect(wrapper.findAll('[data-testid^="acknowledge-command-"]')).toHaveLength(1);
    expect(wrapper.find('[data-testid="cancel-command-dispatching-1"]').exists()).toBe(false);
  });

  it('confirms, mutates with the visible version, and refreshes', async () => {
    const api = createApi();
    const wrapper = mount(SendQueueView, { props: { api } });
    await flushPromises();

    await wrapper.get('[data-testid="cancel-command-pending-1"]').trigger('click');
    await flushPromises();
    expect(confirmAction).toHaveBeenCalled();
    expect(api.cancel).toHaveBeenCalledWith('pending-1', 2);

    await wrapper.get('[data-testid="acknowledge-command-unknown-1"]').trigger('click');
    await flushPromises();
    expect(api.acknowledgeUnknown).toHaveBeenCalledWith('unknown-1', 5);
    expect(api.list).toHaveBeenCalledTimes(3);
  });

  it('reloads and explains a concurrent state change', async () => {
    const api = createApi();
    api.cancel.mockRejectedValueOnce({ response: { status: 409 } });
    const wrapper = mount(SendQueueView, { props: { api } });
    await flushPromises();

    await wrapper.get('[data-testid="cancel-command-pending-1"]').trigger('click');
    await flushPromises();

    expect(wrapper.text()).toContain('记录状态已变化');
    expect(api.list).toHaveBeenCalledTimes(2);
  });
});

function createApi() {
  return {
    listRobots: vi.fn().mockResolvedValue([
      { id: 'robot-1', name: '默认机器人', isEnabled: true }
    ]),
    list: vi.fn().mockResolvedValue({
      items: [
        item('pending-1', 'pending', 2),
        item('unknown-1', 'deliveryUnknown', 5),
        item('dispatching-1', 'dispatching', 1)
      ],
      total: 3,
      page: 1,
      pageSize: 20
    }),
    cancel: vi.fn().mockResolvedValue({ id: 'pending-1', status: 'cancelled', version: 3 }),
    acknowledgeUnknown: vi.fn().mockResolvedValue({
      id: 'unknown-1',
      status: 'deliveryUnknownResolved',
      version: 6
    })
  };
}

function item(id: string, status: string, version: number) {
  return {
    id,
    robotConfigId: 'robot-1',
    robotName: '默认机器人',
    groupName: '机器人测试群1',
    status,
    attemptCount: 0,
    createdAtUtc: '2026-07-28T01:00:00Z',
    externalDispatchStartedAtUtc: null,
    completedAtUtc: null,
    reason: status === 'deliveryUnknown' ? 'delivery_outcome_unknown' : null,
    version,
    messageLength: 'secret message'.length
  };
}
