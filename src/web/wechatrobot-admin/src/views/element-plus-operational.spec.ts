import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { describe, expect, it, vi } from 'vitest';
import AdminLayout from '../layouts/AdminLayout.vue';
import HandoffQueueView from './handoffs/HandoffQueueView.vue';
import KnowledgeDocumentsView from './knowledge/KnowledgeDocumentsView.vue';
import KnowledgeReviewView from './knowledge/KnowledgeReviewView.vue';
import KnowledgeTagsView from './knowledge/KnowledgeTagsView.vue';
import ModelSettingsView from './models/ModelSettingsView.vue';
import SystemSettingsView from './settings/SystemSettingsView.vue';
import UserRolesView from './users/UserRolesView.vue';

describe('Task 16 Element Plus operational surfaces', () => {
  it('uses Element Plus for queue tables, filters and pagination', async () => {
    const reviewApi = {
      listCandidates: vi.fn().mockResolvedValue({
        items: [{ id: 'c1', question: '怎么退款？', status: 'pending', version: 1, updatedAtUtc: '2026-07-22T00:00:00Z' }],
        total: 21,
        page: 1,
        pageSize: 20
      }),
      getCandidate: vi.fn(),
      reviewCandidate: vi.fn()
    };
    const review = mount(KnowledgeReviewView, { props: { api: reviewApi } });
    await flushPromises();
    expect(review.findComponent({ name: 'ElTable' }).exists()).toBe(true);
    expect(review.findComponent({ name: 'ElSelect' }).exists()).toBe(true);
    expect(review.findComponent({ name: 'ElPagination' }).exists()).toBe(true);

    const handoffApi = {
      assignees: vi.fn().mockResolvedValue([]),
      list: vi.fn().mockResolvedValue({
        items: [{ id: 'h1', state: 'WaitingHuman', reasonCode: 'manual', version: 1, updatedAtUtc: '2026-07-22T00:00:00Z' }],
        total: 1,
        page: 1,
        pageSize: 20
      }),
      detail: vi.fn(),
      messages: vi.fn(),
      transitions: vi.fn(),
      assign: vi.fn(),
      resolve: vi.fn(),
      restore: vi.fn()
    };
    const handoffs = mount(HandoffQueueView, { props: { api: handoffApi } });
    await flushPromises();
    expect(handoffs.findComponent({ name: 'ElTable' }).exists()).toBe(true);
    expect(handoffs.findComponent({ name: 'ElSelect' }).exists()).toBe(true);
    expect(handoffs.findComponent({ name: 'ElPagination' }).exists()).toBe(true);
  });

  it('uses Element Plus forms and status components without replacing multipart upload control', async () => {
    const modelApi = {
      list: vi.fn().mockResolvedValue([{
        id: 'm1',
        name: 'chat-default',
        provider: 'openai-compatible',
        configurationType: 'chat',
        baseUrl: 'https://example.test/v1',
        model: 'gpt-test',
        timeoutSeconds: 60,
        maxRetries: 2,
        isEnabled: true,
        isDefault: true,
        connectionStatus: 'Succeeded' as const,
        hasApiKey: true,
        lastFour: '1234',
        version: 1
      }]),
      create: vi.fn(),
      update: vi.fn(),
      testConnection: vi.fn(),
      setEnabled: vi.fn(),
      setDefault: vi.fn(),
      clearApiKey: vi.fn(),
      delete: vi.fn()
    };
    const models = mount(ModelSettingsView, { props: { api: modelApi } });
    await flushPromises();
    expect(models.findComponent({ name: 'ElButton' }).exists()).toBe(true);
    expect(models.findComponent({ name: 'ElTag' }).exists()).toBe(true);

    const documents = mount(KnowledgeDocumentsView, {
      props: { api: { upload: vi.fn() } }
    });
    expect(documents.find('input[type="file"]').exists()).toBe(true);
    expect(documents.findComponent({ name: 'ElButton' }).exists()).toBe(true);
    expect(documents.get('[data-testid="upload-document"]').classes()).toContain('el-button--primary');
  });

  it('uses operational controls for users and keeps alerts on the remaining read-only settings surface', async () => {
    const users = mount(UserRolesView, { props: { api: {
      list: vi.fn().mockResolvedValue({
        items: [{ id: 'u1', email: 'admin@example.test', displayName: 'Admin', isEnabled: true, roles: ['Admin'] }],
        total: 1, page: 1, pageSize: 20
      }),
      roles: vi.fn().mockResolvedValue(['Admin', 'KnowledgeOperator', 'HumanAgent']),
      create: vi.fn(), setEnabled: vi.fn(), setRoles: vi.fn()
    } } });
    await flushPromises();
    expect(users.findComponent({ name: 'ElButton' }).exists()).toBe(true);
    expect(users.findComponent({ name: 'ElTag' }).exists()).toBe(true);
    expect(mount(SystemSettingsView).findComponent({ name: 'ElAlert' }).exists()).toBe(true);

    setActivePinia(createPinia());
    const layout = mount(AdminLayout, {
      global: { stubs: { RouterLink: { template: '<a><slot /></a>' }, RouterView: true } }
    });
    expect(layout.findComponent({ name: 'ElButton' }).exists()).toBe(true);
  });

  it('uses Element Plus status and action components on knowledge tag management', async () => {
    const pinia = createPinia();
    const tags = mount(KnowledgeTagsView, {
      props: {
        api: {
          list: vi.fn().mockResolvedValue({
            items: [{
              id: 'tag-1',
              name: '产品',
              isEnabled: true,
              isGlobalPublic: false,
              version: 1,
              createdAtUtc: '2026-07-24T00:00:00Z'
            }],
            total: 1,
            page: 1,
            pageSize: 20
          }),
          options: vi.fn(),
          create: vi.fn(),
          update: vi.fn(),
          setEnabled: vi.fn(),
          delete: vi.fn()
        }
      },
      global: { plugins: [pinia] }
    });
    await flushPromises();
    expect(tags.findComponent({ name: 'ElButton' }).exists()).toBe(true);
    expect(tags.findComponent({ name: 'ElTag' }).exists()).toBe(true);
  });
});
