import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import KnowledgeTagSelector from './KnowledgeTagSelector.vue';

describe('KnowledgeTagSelector', () => {
  it('loads enabled options and emits distinct selected ids in option order', async () => {
    const api = {
      options: vi.fn().mockResolvedValue([
        { id: 'tag-1', name: '产品', isGlobalPublic: false },
        { id: 'tag-2', name: '公开', isGlobalPublic: true }
      ])
    };
    const wrapper = mount(KnowledgeTagSelector, {
      props: { api, modelValue: ['tag-2', 'tag-2'] }
    });
    await flushPromises();

    await wrapper.get('[data-testid="knowledge-tag-tag-1"]').setValue(true);

    expect(api.options).toHaveBeenCalledOnce();
    expect(wrapper.emitted('update:modelValue')?.at(-1)?.[0]).toEqual(['tag-1', 'tag-2']);
    expect(wrapper.text()).toContain('公开（全局公开）');
  });

  it('shows loading and then the empty state', async () => {
    let resolveOptions!: (value: []) => void;
    const api = {
      options: vi.fn().mockReturnValue(new Promise<[]>(resolve => {
        resolveOptions = resolve;
      }))
    };
    const wrapper = mount(KnowledgeTagSelector, {
      props: { api, modelValue: [] }
    });

    expect(wrapper.text()).toContain('正在加载标签');
    resolveOptions([]);
    await flushPromises();
    expect(wrapper.text()).toContain('当前没有可用标签');
  });

  it('shows a stable failure message without rendering a manual id input', async () => {
    const api = {
      options: vi.fn().mockRejectedValue(new Error('network unavailable'))
    };
    const wrapper = mount(KnowledgeTagSelector, {
      props: { api, modelValue: [] }
    });
    await flushPromises();

    expect(wrapper.text()).toContain('标签加载失败，请刷新后重试。');
    expect(wrapper.find('input[type="text"]').exists()).toBe(false);
  });

  it('shows required validation copy when no tag is selected', async () => {
    const api = { options: vi.fn().mockResolvedValue([]) };
    const wrapper = mount(KnowledgeTagSelector, {
      props: { api, modelValue: [], required: true }
    });
    await flushPromises();

    expect(wrapper.get('[data-testid="knowledge-tag-required"]').text())
      .toContain('请至少选择一个知识标签。');
  });
});
