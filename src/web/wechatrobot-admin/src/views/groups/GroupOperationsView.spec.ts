import { describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { ElOption, ElSelect } from 'element-plus';
import GroupOperationsView from './GroupOperationsView.vue';

describe('GroupOperationsView', () => {
  it('requires an explicit manual invitation acknowledgement before registering an existing group', async () => {
    const api = {
      listGroups: vi.fn().mockResolvedValue([]),
      listRobots: vi.fn().mockResolvedValue([
        { id: 'robot-config-1', name: '客服机器人', isEnabled: true },
        { id: 'robot-config-2', name: '停用机器人', isEnabled: false }
      ]),
      registerExistingGroup: vi.fn(),
      preview: vi.fn(),
      execute: vi.fn(),
      listOperations: vi.fn().mockResolvedValue([]),
      getAuditScope: vi.fn().mockResolvedValue({ scope: '服务端审计范围' })
    };
    const wrapper = mount(GroupOperationsView, { props: { api } });
    await flushPromises();
    const robotSelectors = wrapper.findAllComponents(ElSelect);
    expect(robotSelectors).toHaveLength(2);
    const robotOptions = wrapper.findAllComponents(ElOption);
    expect(robotOptions.map(option => option.props('label'))).toContain('客服机器人');
    expect(robotOptions.filter(option => option.props('label') === '停用机器人').every(option => option.props('disabled'))).toBe(true);
    expect(wrapper.find('[data-testid="operation-robot-config-id"]').exists()).toBe(false);
    await wrapper.get('[data-testid="register-existing-group"]').trigger('click');
    expect(wrapper.text()).toContain('先由人工在企业微信中邀请机器人入群');
    robotSelectors[0].vm.$emit('update:modelValue', 'robot-config-1');
    await wrapper.vm.$nextTick();
    await wrapper.get('[data-testid="manual-invitation-completed"]').setValue(true);
    await wrapper.get('[data-testid="register-existing-group"]').trigger('click');
    expect(api.registerExistingGroup).toHaveBeenCalledWith({
      robotConfigId: 'robot-config-1',
      name: '',
      workToolGroupRemark: '',
      manualInvitationCompleted: true
    });
    expect(wrapper.text()).not.toContain('外部群 ID');
  });

  it('loads registered groups and selecting one populates the operation form', async () => {
    const api = {
      listGroups: vi.fn().mockResolvedValue([{ id: 'known-1', robotConfigId: 'robot-config-1', name: '技术支持群', workToolGroupRemark: 'support-east' }]),
      listRobots: vi.fn().mockResolvedValue([{ id: 'robot-config-1', name: '客服机器人', isEnabled: true }]),
      registerExistingGroup: vi.fn(), preview: vi.fn(), execute: vi.fn(), listOperations: vi.fn().mockResolvedValue([]), getAuditScope: vi.fn().mockResolvedValue({ scope: '服务端审计范围' })
    };
    const wrapper = mount(GroupOperationsView, { props: { api } });
    await Promise.resolve(); await wrapper.vm.$nextTick();

    expect(wrapper.text()).toContain('技术支持群');
    await wrapper.get('[data-testid="select-known-group-known-1"]').trigger('click');
    expect(wrapper.findAllComponents(ElSelect)[1].props('modelValue')).toBe('robot-config-1');
    expect((wrapper.get('[data-testid="operation-group-name"]').element as HTMLInputElement).value).toBe('support-east');
    expect(wrapper.text()).toContain('成员显示名');
    expect(wrapper.text()).toContain('不是稳定 ID');
  });

  it('shows distinct WorkTool acceptance and final execution states', async () => {
    const statuses = [
      ['accepted', 'WorkTool 已接受，等待机器人执行结果'],
      ['executedSucceeded', '机器人执行成功'],
      ['executedPartially', '机器人部分执行成功'],
      ['executedFailed', '机器人执行失败'],
      ['deliveryUnknown', '投递结果未知'],
      ['resultTimeout', '等待执行结果超时']
    ];
    const api = {
      listGroups: vi.fn().mockResolvedValue([]),
      listRobots: vi.fn().mockResolvedValue([]),
      registerExistingGroup: vi.fn(),
      preview: vi.fn(),
      execute: vi.fn(),
      getAuditScope: vi.fn().mockResolvedValue({ scope: '服务端返回的群命令审计范围' }),
      listOperations: vi.fn().mockResolvedValue(statuses.map(([status], index) => ({
        id: `audit-${index}`,
        operation: 'AddMembers',
        workToolCommandNumber: 207,
        status,
        createdAtUtc: '2026-07-24T00:00:00Z'
      })))
    };

    const wrapper = mount(GroupOperationsView, { props: { api } });
    await Promise.resolve();
    await wrapper.vm.$nextTick();

    for (const [, copy] of statuses) {
      expect(wrapper.text()).toContain(copy);
    }
    expect(wrapper.text()).toContain('服务端返回的群命令审计范围');
  });
});
