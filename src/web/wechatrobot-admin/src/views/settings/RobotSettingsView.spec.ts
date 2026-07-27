import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import RobotSettingsView from './RobotSettingsView.vue';
import type { RobotApi } from '../../api/robots';

describe('RobotSettingsView', () => {
  it('tests connection and exposes separate callback controls without showing credentials', async () => {
    const api: RobotApi = {
      list: vi.fn().mockResolvedValue([{
        id: 'robot-1', name: '客服机器人', robotReference: 'configured',
        hasWorkToolRobotId: true, isEnabled: false, sendRateLimitPerMinute: 50,
        updatedAtUtc: '2026-07-25T07:00:00Z'
      }]),
      save: vi.fn(),
      probe: vi.fn().mockResolvedValue({
        reachable: true, online: true, messageCallbackEnabled: true,
        replyAllEnabled: true, enableConfirmationToken: 'enable-token'
      }),
      configureMessageCallback: vi.fn(),
      configureCommandResultCallback: vi.fn(),
      getCallbacks: vi.fn().mockResolvedValue({
        messageCallbackConfigured: true, commandResultCallbackConfigured: true,
        replyAll: true, checkedAtUtc: '2026-07-25T07:00:00Z'
      })
    };
    const wrapper = mount(RobotSettingsView, { props: { api } });
    await flushPromises();

    expect(wrapper.text()).toContain('已配置');
    expect(wrapper.text()).not.toContain('plaintext');
    await wrapper.get('[data-testid="probe-robot-1"]').trigger('click');
    await flushPromises();
    expect(wrapper.text()).toContain('可达');
    expect(wrapper.text()).toContain('在线');
    expect(wrapper.find('[data-testid="message-callback-robot-1"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="result-callback-robot-1"]').exists()).toBe(true);
  });
});
