import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import MemoryCenterView from './MemoryCenterView.vue';

vi.mock('../../utils/dialogs', () => ({
  confirmAction: vi.fn().mockResolvedValue(true),
  promptAction: vi.fn().mockResolvedValue(null)
}));

describe('MemoryCenterView', () => {
  it('loads group-scoped candidates and exposes all three workflows', async () => {
    const groupOptionApi = {
      list: vi.fn().mockResolvedValue([{
        id: 'group-1',
        name: '机器人测试群1',
        workToolGroupRemark: null,
        robotName: '默认机器人',
        state: 'enabled',
        isEnabled: true
      }])
    };
    const api = {
      listCandidates: vi.fn().mockResolvedValue({
        items: [{
          id: 'candidate-1', scopeType: 'User', groupProfileId: 'group-1',
          subjectDisplayName: '小王', memoryType: 'UserPreference', content: '偏好结论优先',
          confidence: .9, isExplicit: true, observationCount: 2, distinctSessionCount: 2,
          distinctDayCount: 1, hasUnresolvedConflict: false, status: 'accumulating',
          version: 1, createdAtUtc: '2026-07-28T00:00:00Z', updatedAtUtc: '2026-07-28T00:00:00Z'
        }],
        total: 1, page: 1, pageSize: 20
      }),
      listEntries: vi.fn().mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 20 }),
      listJobs: vi.fn().mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 20 }),
      editCandidate: vi.fn(), promoteCandidate: vi.fn(), rejectCandidate: vi.fn(),
      reorganizeCandidate: vi.fn(), forgetEntry: vi.fn(), restoreEntry: vi.fn(), retryJob: vi.fn()
    };

    const wrapper = mount(MemoryCenterView, {
      props: { initialGroupId: 'group-1', api, groupOptionApi }
    });
    await flushPromises();

    expect(wrapper.text()).not.toContain('群 ID');
    expect(wrapper.find('[data-testid="group-profile-select"]').exists()).toBe(true);
    expect(wrapper.text()).toContain('机器人测试群1 · 默认机器人');
    expect(api.listCandidates).toHaveBeenCalledWith(expect.objectContaining({ groupProfileId: 'group-1' }));
    expect(wrapper.text()).toContain('偏好结论优先');
    expect(wrapper.text()).toContain('2 次 / 2 会话 / 1 天');
    expect(wrapper.text()).toContain('待整理');
    expect(wrapper.text()).toContain('长期记忆');
    expect(wrapper.text()).toContain('整理任务');
  });
});
