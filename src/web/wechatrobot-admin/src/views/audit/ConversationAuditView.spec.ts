import { flushPromises, mount } from '@vue/test-utils';
import { ElOption, ElSelect } from 'element-plus';
import { describe, expect, it, vi } from 'vitest';
import ConversationAuditView from './ConversationAuditView.vue';

describe('ConversationAuditView filters', () => {
  it('converts local datetimes to UTC and resets pagination when applying filters', async () => {
    const api = {
      groupOptions: vi.fn().mockResolvedValue([
        {
          id: '00000000-0000-0000-0000-000000000801',
          name: '技术部',
          workToolGroupRemark: 'tech',
          robotName: '客服机器人',
          isEnabled: true
        },
        {
          id: '00000000-0000-0000-0000-000000000802',
          name: '历史群',
          robotName: '客服机器人',
          isEnabled: false
        }
      ]),
      createKnowledgeCandidate: vi.fn(),
      capability: vi.fn().mockResolvedValue({
        available: true, items: [], total: 0, page: 1, pageSize: 20
      })
    };
    const wrapper = mount(ConversationAuditView, { props: { api } });
    await flushPromises();

    const groupSelector = wrapper.findComponent(ElSelect);
    expect(groupSelector.exists()).toBe(true);
    const labels = wrapper.findAllComponents(ElOption).map(option => option.props('label'));
    expect(labels).toContain('技术部（tech） · 客服机器人');
    expect(labels).toContain('历史群 · 客服机器人 · 已停用');
    expect(wrapper.find('[data-testid="audit-group-id"]').exists()).toBe(false);
    groupSelector.vm.$emit(
      'update:modelValue',
      '00000000-0000-0000-0000-000000000801');
    await wrapper.vm.$nextTick();
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

  it('labels the actual answer source and renders only structured web citations', async () => {
    const api = {
      groupOptions: vi.fn().mockResolvedValue([]),
      createKnowledgeCandidate: vi.fn(),
      capability: vi.fn().mockResolvedValue({
        available: true,
        total: 1,
        page: 1,
        pageSize: 20,
        items: [{
          id: 'audit-1',
          groupProfileId: 'group-1',
          question: '今天有什么更新？',
          answer: '联网回答',
          answerSource: 'web_search',
          webSearchFailureCode: null,
          webSearchSources: [{
            title: '官方来源',
            url: 'https://example.com/news',
            site: 'Example',
            publishedAt: '2026-07-28',
            index: 1
          }],
          sources: [],
          evidence: [],
          inputSummary: {},
          createdAtUtc: '2026-07-28T01:00:00Z'
        }]
      })
    };
    const wrapper = mount(ConversationAuditView, { props: { api } });
    await flushPromises();

    expect(wrapper.text()).toContain('联网搜索');
    expect(wrapper.text()).toContain('官方来源');
    const link = wrapper.get('a[href="https://example.com/news"]');
    expect(link.attributes('target')).toBe('_blank');
    expect(link.attributes('rel')).toContain('noopener');
  });
});
