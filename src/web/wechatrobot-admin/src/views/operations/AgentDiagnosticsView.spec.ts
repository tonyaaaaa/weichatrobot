import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import AgentDiagnosticsView from './AgentDiagnosticsView.vue';
import type { AgentDiagnosticsApi } from '../../api/agentDiagnostics';
import type { GroupOptionApi } from '../../api/groupOptions';

describe('AgentDiagnosticsView', () => {
  it('shows runtime modes and safe intent decision metadata', async () => {
    const api: AgentDiagnosticsApi = {
      runtime: vi.fn().mockResolvedValue({
        intentRuntimeMode: 'Shadow',
        answerRuntimeMode: 'Legacy',
        privateChatRuntimeMode: 'AgentFramework',
        templateRoutingRuntimeMode: 'AgentFramework',
        intentModelConfigurationId: null,
        intentMinimumConfidence: 0.8,
        intentHistoryMessageCount: 12,
        intentHistoryMinutes: 10
      }),
      list: vi.fn().mockResolvedValue({
        items: [{
          id: 'audit-1',
          conversationMessageId: 'message-1',
          groupProfileId: 'group-1',
          groupName: '机器人测试群1',
          senderDisplayName: '张三',
          decision: 'NoReply',
          category: 'HumanConversation',
          reasonCode: 'human_to_human_exchange',
          confidence: 0.93,
          failureCode: null,
          runtimeMode: 'Shadow',
          agentVersion: 'message-intent-v1',
          modelConfigurationId: 'model-1',
          modelConfigurationVersion: 3,
          latencyMilliseconds: 125,
          formalConversationIncluded: true,
          decidedAtUtc: '2026-07-29T07:00:00Z'
        }],
        total: 1,
        page: 1,
        pageSize: 20
      })
    };
    const groupApi: GroupOptionApi = {
      list: vi.fn().mockResolvedValue([{
        id: 'group-1',
        name: '机器人测试群1',
        robotName: '默认机器人',
        state: 'enabled',
        isEnabled: true
      }])
    };

    const wrapper = mount(AgentDiagnosticsView, {
      props: { api, groupApi }
    });
    await flushPromises();

    expect(wrapper.text()).toContain('影子判断');
    expect(wrapper.text()).toContain('现有回答');
    expect(wrapper.text()).toContain('机器人测试群1');
    expect(wrapper.text()).toContain('成员对话');
    expect(wrapper.text()).toContain('human_to_human_exchange');
    expect(wrapper.text()).not.toContain('prompt');
  });

  it('provides a visible retry state when diagnostics cannot load', async () => {
    const api: AgentDiagnosticsApi = {
      runtime: vi.fn().mockRejectedValue(new Error('offline')),
      list: vi.fn().mockRejectedValue(new Error('offline'))
    };
    const groupApi: GroupOptionApi = {
      list: vi.fn().mockResolvedValue([])
    };
    const wrapper = mount(AgentDiagnosticsView, { props: { api, groupApi } });
    await flushPromises();

    expect(wrapper.text()).toContain('Agent 诊断加载失败');
    expect(wrapper.find('[data-testid="reload-agent-diagnostics"]').exists()).toBe(true);
  });
});
