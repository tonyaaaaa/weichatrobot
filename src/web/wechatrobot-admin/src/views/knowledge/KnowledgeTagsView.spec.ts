import { flushPromises, mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import { describe, expect, it, vi } from 'vitest';
import type { KnowledgeTag, KnowledgeTagApi } from '../../api/knowledgeTags';
import { useAuthStore } from '../../stores/auth';
import KnowledgeTagsView from './KnowledgeTagsView.vue';

function tag(overrides: Partial<KnowledgeTag> = {}): KnowledgeTag {
  return {
    id: 'tag-1',
    name: '产品',
    isEnabled: true,
    isGlobalPublic: true,
    version: 1,
    createdAtUtc: '2026-07-24T00:00:00Z',
    ...overrides
  };
}

function createTagApi(initial = [tag()]) {
  let items = initial.map(item => ({ ...item }));
  const api: KnowledgeTagApi = {
    list: vi.fn(async params => {
      const filtered = items.filter(item =>
        (!params.q || item.name.includes(params.q))
        && (params.state === 'all' || params.state === undefined
          || item.isEnabled === (params.state === 'enabled'))
        && (params.global === 'all' || params.global === undefined
          || item.isGlobalPublic === (params.global === 'global')));
      const start = (params.page - 1) * params.pageSize;
      return {
        items: filtered.slice(start, start + params.pageSize).map(item => ({ ...item })),
        total: filtered.length,
        page: params.page,
        pageSize: params.pageSize
      };
    }),
    options: vi.fn(async () => []),
    create: vi.fn(async request => {
      const created = tag({
        id: `tag-${items.length + 1}`,
        name: request.name,
        isGlobalPublic: request.isGlobalPublic,
        version: 0
      });
      items.push(created);
      return { ...created };
    }),
    update: vi.fn(async (id, request) => {
      const current = items.find(item => item.id === id)!;
      Object.assign(current, request, { version: current.version + 1 });
      return { ...current };
    }),
    setEnabled: vi.fn(async (id, request) => {
      const current = items.find(item => item.id === id)!;
      current.isEnabled = request.isEnabled;
      current.version++;
      return { ...current };
    }),
    delete: vi.fn(async id => {
      items = items.filter(item => item.id !== id);
    })
  };
  return api;
}

function mountView(
  api: KnowledgeTagApi,
  roles = ['Admin'],
  confirmAction: (message: string) => boolean | Promise<boolean> = () => true
) {
  const pinia = createPinia();
  const auth = useAuthStore(pinia);
  auth.user = {
    id: 'user-1',
    email: 'operator@example.test',
    displayName: 'Operator',
    roles
  };
  return mount(KnowledgeTagsView, {
    props: { api, confirmAction },
    global: { plugins: [pinia] }
  });
}

describe('KnowledgeTagsView', () => {
  it('creates, disables and edits using the current row version', async () => {
    const api = createTagApi();
    const wrapper = mountView(api);
    await flushPromises();

    expect(wrapper.text()).toContain('全局公开');
    expect(wrapper.text()).not.toContain('后端暂未提供标签维护 API');

    await wrapper.get('[data-testid="create-tag"]').trigger('click');
    await wrapper.get('[data-testid="tag-name"]').setValue('新标签');
    await wrapper.get('[data-testid="save-tag"]').trigger('click');
    await flushPromises();
    expect(api.create).toHaveBeenCalledWith({
      name: '新标签',
      isGlobalPublic: false
    });

    await wrapper.get('[data-testid="toggle-tag-tag-1"]').trigger('click');
    await flushPromises();
    expect(api.setEnabled).toHaveBeenCalledWith('tag-1', {
      isEnabled: false,
      expectedVersion: 1
    });

    await wrapper.get('[data-testid="edit-tag-tag-1"]').trigger('click');
    await wrapper.get('[data-testid="tag-name"]').setValue('产品知识');
    await wrapper.get('[data-testid="tag-global"]').setValue(false);
    await wrapper.get('[data-testid="save-tag"]').trigger('click');
    await flushPromises();
    expect(api.update).toHaveBeenCalledWith('tag-1', {
      name: '产品知识',
      isGlobalPublic: false,
      expectedVersion: 2
    });
  });

  it('resets pagination when filters change', async () => {
    const api = createTagApi(Array.from({ length: 21 }, (_, index) =>
      tag({
        id: `tag-${index + 1}`,
        name: `标签 ${index + 1}`,
        isGlobalPublic: false
      })));
    const wrapper = mountView(api);
    await flushPromises();

    await wrapper.get('[data-testid="next-page"]').trigger('click');
    await flushPromises();
    expect(api.list).toHaveBeenLastCalledWith(expect.objectContaining({ page: 2 }));

    await wrapper.get('[data-testid="tag-state-filter"]').setValue('disabled');
    await flushPromises();
    expect(api.list).toHaveBeenLastCalledWith(expect.objectContaining({
      state: 'disabled',
      page: 1
    }));
  });

  it('hides physical delete from a knowledge operator and confirms it for admin', async () => {
    const operatorApi = createTagApi();
    const operator = mountView(operatorApi, ['KnowledgeOperator']);
    await flushPromises();
    expect(operator.find('[data-testid="delete-tag-tag-1"]').exists()).toBe(false);

    const confirmAction = vi.fn().mockResolvedValue(false);
    const adminApi = createTagApi();
    const admin = mountView(adminApi, ['Admin'], confirmAction);
    await flushPromises();
    await admin.get('[data-testid="delete-tag-tag-1"]').trigger('click');
    await flushPromises();
    expect(confirmAction).toHaveBeenCalledWith(
      '仅未被群、分段、审核或索引任务引用的标签可物理删除。确认删除“产品”？'
    );
    expect(adminApi.delete).not.toHaveBeenCalled();
  });

  it('refreshes a stale row from the concurrency response', async () => {
    const api = createTagApi();
    api.setEnabled = vi.fn().mockRejectedValue({
      response: {
        data: {
          error: 'knowledge-tag-concurrency-conflict',
          current: tag({ name: '产品（最新）', version: 8 })
        }
      }
    });
    const wrapper = mountView(api);
    await flushPromises();

    await wrapper.get('[data-testid="toggle-tag-tag-1"]').trigger('click');
    await flushPromises();

    expect(wrapper.text()).toContain('标签已被其他操作员修改，页面已刷新为最新版本。');
    expect(wrapper.text()).toContain('产品（最新）');
    expect(wrapper.text()).toContain('8');
  });

  it('explains reference counts and suggests disabling instead of deleting', async () => {
    const api = createTagApi();
    api.delete = vi.fn().mockRejectedValue({
      response: {
        data: {
          error: 'knowledge-tag-referenced',
          references: { groups: 2, chunks: 3, reviews: 1, indexJobs: 4 }
        }
      }
    });
    const wrapper = mountView(api);
    await flushPromises();

    await wrapper.get('[data-testid="delete-tag-tag-1"]').trigger('click');
    await flushPromises();

    expect(wrapper.text()).toContain('标签仍被引用，不能删除；可先停用。');
    expect(wrapper.text()).toContain('群 2');
    expect(wrapper.text()).toContain('分段 3');
    expect(wrapper.text()).toContain('审核 1');
    expect(wrapper.text()).toContain('索引任务 4');
  });
});
