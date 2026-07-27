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
        reachable: true, online: false, messageCallbackEnabled: true,
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
    expect(wrapper.text()).toContain('在线状态：WorkTool 官方未提供可靠结果');
    expect(wrapper.text()).not.toContain('离线');
    expect(wrapper.find('[data-testid="message-callback-robot-1"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="result-callback-robot-1"]').exists()).toBe(true);
  });

  it('renders the robot runtime state as a labeled switch with an impact description', async () => {
    const api = createApi({
      id: 'robot-1', name: '客服机器人', robotReference: 'configured',
      hasWorkToolRobotId: true, isEnabled: true, sendRateLimitPerMinute: 50,
      updatedAtUtc: '2026-07-25T07:00:00Z'
    });
    const wrapper = mount(RobotSettingsView, { props: { api } });
    await flushPromises();

    expect(wrapper.get('[data-testid="enabled-robot-1"]').classes()).toContain('el-switch');
    expect(wrapper.text()).toContain('机器人运行状态');
    expect(wrapper.text()).toContain('停用后不会用于消息发送和群操作，配置仍会保留。');
  });

  it('requires saving a missing or newly entered robot id before probing', async () => {
    const api = createApi({
      id: 'robot-1', name: '客服机器人', robotReference: 'missing',
      hasWorkToolRobotId: false, isEnabled: false, sendRateLimitPerMinute: 50,
      updatedAtUtc: '2026-07-25T07:00:00Z'
    });
    const wrapper = mount(RobotSettingsView, { props: { api } });
    await flushPromises();

    const probeButton = wrapper.get('[data-testid="probe-robot-1"]');
    expect(probeButton.attributes('disabled')).toBeDefined();
    expect(wrapper.text()).toContain('请先填写并保存 WorkTool 机器人 ID');

    await wrapper.get('[data-testid="credential-robot-1"]').setValue('replacement-id');

    expect(probeButton.attributes('disabled')).toBeDefined();
    expect(wrapper.text()).toContain('新机器人 ID 尚未保存');
    await probeButton.trigger('click');
    expect(api.probe).not.toHaveBeenCalled();
  });
});

function createApi(robot: Parameters<RobotApi['save']>[1] & {
  id: string;
  robotReference: string;
  hasWorkToolRobotId: boolean;
  updatedAtUtc: string;
}): RobotApi {
  return {
    list: vi.fn().mockResolvedValue([robot]),
    save: vi.fn(),
    probe: vi.fn(),
    configureMessageCallback: vi.fn(),
    configureCommandResultCallback: vi.fn(),
    getCallbacks: vi.fn()
  };
}
