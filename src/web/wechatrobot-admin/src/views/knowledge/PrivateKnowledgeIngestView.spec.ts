import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import PrivateKnowledgeIngestView from './PrivateKnowledgeIngestView.vue';
import type {
  PrivateKnowledgeIngestApi,
  PrivateKnowledgeIngestBatch
} from '../../api/privateKnowledgeIngest';

function batch(
  overrides: Partial<PrivateKnowledgeIngestBatch> = {}
): PrivateKnowledgeIngestBatch {
  return {
    id: '11111111-1111-1111-1111-111111111111',
    robotConfigId: '22222222-2222-2222-2222-222222222222',
    sourceConversationMessageId: '33333333-3333-3333-3333-333333333333',
    roomType: 4,
    sourceActorDisplayName: '内部同事',
    status: 'Failed',
    totalCount: 4,
    newCount: 1,
    duplicateCount: 1,
    supplementCount: 1,
    correctionCount: 1,
    failureCode: 'agent_invalid_output',
    version: 2,
    createdAtUtc: '2026-07-29T06:00:00Z',
    updatedAtUtc: '2026-07-29T06:01:00Z',
    ...overrides
  };
}

describe('PrivateKnowledgeIngestView', () => {
  it('shows safe batch metadata and retries failed batches', async () => {
    const item = batch();
    const api: PrivateKnowledgeIngestApi = {
      list: vi.fn().mockResolvedValue([item]),
      get: vi.fn(),
      retry: vi.fn().mockResolvedValue({ ...item, status: 'Received', version: 3 })
    };
    const wrapper = mount(PrivateKnowledgeIngestView, { props: { api } });
    await flushPromises();

    expect(wrapper.text()).toContain('内部同事');
    expect(wrapper.text()).toContain('新增 1');
    expect(wrapper.text()).toContain('重复 1');
    expect(wrapper.text()).toContain('agent_invalid_output');
    expect(wrapper.text()).toContain('兼容显示名');
    await wrapper.get(`[data-testid="retry-${item.id}"]`).trigger('click');
    await flushPromises();
    expect(api.retry).toHaveBeenCalledWith(item.id, 2);
    expect(wrapper.text()).toContain('等待处理');
  });

  it('shows a retryable loading failure', async () => {
    const api: PrivateKnowledgeIngestApi = {
      list: vi.fn().mockRejectedValue(new Error('offline')),
      get: vi.fn(),
      retry: vi.fn()
    };
    const wrapper = mount(PrivateKnowledgeIngestView, { props: { api } });
    await flushPromises();

    expect(wrapper.text()).toContain('私聊入库批次加载失败');
    expect(wrapper.find('[data-testid="reload-private-ingests"]').exists()).toBe(true);
  });
});
