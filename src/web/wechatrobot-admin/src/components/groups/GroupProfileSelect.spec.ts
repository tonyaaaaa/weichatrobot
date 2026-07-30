import { flushPromises, mount } from '@vue/test-utils';
import { ElOption, ElSelect } from 'element-plus';
import { describe, expect, it, vi } from 'vitest';
import GroupProfileSelect from './GroupProfileSelect.vue';

describe('GroupProfileSelect', () => {
  it('shows names and emits the stable local group id', async () => {
    const api = {
      list: vi.fn().mockResolvedValue([
        {
          id: 'group-1',
          name: '技术支持群',
          workToolGroupRemark: 'support-east',
          robotName: '默认机器人',
          state: 'enabled',
          isEnabled: true
        },
        {
          id: 'group-2',
          name: '历史群',
          workToolGroupRemark: null,
          robotName: '默认机器人',
          state: 'archived',
          isEnabled: false
        }
      ])
    };
    const wrapper = mount(GroupProfileSelect, {
      props: { modelValue: '', api }
    });
    await flushPromises();

    const labels = wrapper.findAllComponents(ElOption).map(option => option.props('label'));
    expect(labels).toContain('技术支持群（support-east） · 默认机器人');
    expect(labels).toContain('历史群 · 默认机器人 · 已归档');

    wrapper.findComponent(ElSelect).vm.$emit('update:modelValue', 'group-1');
    await wrapper.vm.$nextTick();
    expect(wrapper.emitted('update:modelValue')?.[0]).toEqual(['group-1']);
    expect(wrapper.emitted('change')?.[0]).toEqual(['group-1']);
  });

  it('keeps an unknown route id visible and reports load failures', async () => {
    const api = { list: vi.fn().mockRejectedValue(new Error('unavailable')) };
    const wrapper = mount(GroupProfileSelect, {
      props: { modelValue: 'missing-group', api }
    });
    await flushPromises();

    const fallback = wrapper.findAllComponents(ElOption)
      .find(option => option.props('value') === 'missing-group');
    expect(fallback?.props('label')).toBe('群记录不存在或已删除');
    expect(fallback?.props('disabled')).toBe(true);
    expect(wrapper.emitted('load-error')).toHaveLength(1);
  });
});
