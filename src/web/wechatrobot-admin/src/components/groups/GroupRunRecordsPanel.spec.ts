import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import GroupRunRecordsPanel from './GroupRunRecordsPanel.vue';

describe('GroupRunRecordsPanel', () => {
  it('provides four real filtered destinations without invented metrics', () => {
    const wrapper = mount(GroupRunRecordsPanel, {
      props: { groupId: 'group-1', groupName: '售后群' },
      global: {
        stubs: {
          RouterLink: {
            props: ['to'],
            template: '<a><slot /></a>'
          }
        }
      }
    });

    expect(wrapper.findAll('[data-testid="record-entry"]')).toHaveLength(4);
    expect(wrapper.text()).toContain('按当前群自动筛选');
    expect(wrapper.text()).toContain('发送队列当前按群名称筛选');
    expect(wrapper.text()).not.toMatch(/成功率|今日成功|最近成功/);
  });
});
