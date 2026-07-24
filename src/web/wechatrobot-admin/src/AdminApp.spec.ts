import { mount } from '@vue/test-utils';
import { defineComponent, h } from 'vue';
import { ElPagination } from 'element-plus';
import { describe, expect, it } from 'vitest';
import AdminApp from './AdminApp.vue';

const PaginationRoute = defineComponent({
  setup: () => () => h(ElPagination, {
    currentPage: 1,
    pageSize: 10,
    total: 30,
    layout: 'prev, pager, next',
    'onUpdate:currentPage': () => undefined
  })
});

describe('AdminApp Element Plus locale', () => {
  it('provides the zh-cn locale to operational pagination DOM', () => {
    const wrapper = mount(AdminApp, {
      global: {
        stubs: {
          RouterView: PaginationRoute
        }
      }
    });

    expect(wrapper.get('button.btn-prev').attributes('aria-label')).toBe('上一页');
    expect(wrapper.get('button.btn-next').attributes('aria-label')).toBe('下一页');
  });
});
