import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it, vi } from 'vitest';
import ModelSettingsView from './ModelSettingsView.vue';
import type { ModelApi, ModelConfiguration } from '../../api/models';

const dialogStub = {
  name: 'ModelConfigurationDialog',
  props: ['modelValue', 'configuration'],
  emits: ['update:modelValue', 'save', 'clear-api-key'],
  template: '<div v-if="modelValue" role="dialog"><button data-testid="dialog-save" @click="$emit(\'save\', configuration || {})">保存</button><button v-if="configuration" data-testid="dialog-clear" @click="$emit(\'clear-api-key\', configuration)">清除</button></div>'
};

function model(overrides: Partial<ModelConfiguration> = {}): ModelConfiguration {
  return {
    id: '11111111-1111-1111-1111-111111111111',
    name: '本地对话',
    provider: 'OpenAI 兼容',
    configurationType: 'chat',
    baseUrl: 'http://127.0.0.1:11434',
    model: 'qwen',
    timeoutSeconds: 30,
    maxRetries: 0,
    isEnabled: false,
    isDefault: false,
    connectionStatus: 'Untested',
    hasApiKey: true,
    lastFour: '1234',
    version: 0,
    ...overrides
  };
}

function api(items: ModelConfiguration[] = []): ModelApi {
  return {
    list: vi.fn().mockResolvedValue(items),
    create: vi.fn(),
    update: vi.fn(),
    testConnection: vi.fn(),
    testAgentCapabilities: vi.fn(),
    setEnabled: vi.fn(),
    setDefault: vi.fn(),
    clearApiKey: vi.fn(),
    delete: vi.fn()
  };
}

function mountView(modelApi: ModelApi) {
  return mount(ModelSettingsView, {
    props: { api: modelApi, confirmAction: vi.fn().mockResolvedValue(true) },
    global: { stubs: { ModelConfigurationDialog: dialogStub } }
  });
}

describe('ModelSettingsView', () => {
  it('keeps the create action in the empty state and opens the dialog', async () => {
    const wrapper = mountView(api());
    await flushPromises();

    expect(wrapper.get('[data-testid="create-model"]').text()).toContain('新增模型配置');
    await wrapper.get('[data-testid="create-model"]').trigger('click');
    expect(wrapper.get('[role="dialog"]').attributes('role')).toBe('dialog');
  });

  it('groups cards and exposes safe status and key metadata', async () => {
    const chat = model();
    const embedding = model({
      id: '22222222-2222-2222-2222-222222222222',
      name: '向量模型',
      configurationType: 'embedding',
      connectionStatus: 'Succeeded',
      hasApiKey: false,
      lastFour: undefined
    });
    const wrapper = mountView(api([chat, embedding]));
    await flushPromises();

    expect(wrapper.text()).toContain('对话模型');
    expect(wrapper.text()).toContain('向量模型');
    expect(wrapper.text()).toContain('••••1234');
    expect(wrapper.text()).toContain('待测试');
    expect(wrapper.get(`[data-testid="enable-${chat.id}"]`).attributes('disabled')).toBeDefined();
    expect(wrapper.get(`[data-testid="default-${chat.id}"]`).attributes('disabled')).toBeDefined();
  });

  it('refreshes by immutable ID after testing and renaming', async () => {
    const original = model();
    const modelApi = api([original]);
    vi.mocked(modelApi.testConnection).mockResolvedValue(model({
      connectionStatus: 'Succeeded',
      version: 1
    }));
    vi.mocked(modelApi.update).mockResolvedValue(model({
      name: '改名后',
      connectionStatus: 'Succeeded',
      version: 2
    }));
    const wrapper = mountView(modelApi);
    await flushPromises();

    await wrapper.get(`[data-testid="test-${original.id}"]`).trigger('click');
    await flushPromises();
    expect(wrapper.text()).toContain('测试成功');

    await wrapper.get(`[data-testid="edit-${original.id}"]`).trigger('click');
    wrapper.findComponent(dialogStub).vm.$emit('save', {
      name: '改名后',
      provider: original.provider,
      configurationType: original.configurationType,
      baseUrl: original.baseUrl,
      model: original.model,
      timeoutSeconds: 30,
      maxRetries: 0,
      version: 1
    });
    await flushPromises();
    expect(modelApi.update).toHaveBeenCalledWith(original.id, expect.objectContaining({ name: '改名后' }));
    expect(wrapper.findAll(`[data-testid="model-card-${original.id}"]`)).toHaveLength(1);
    expect(wrapper.text()).toContain('改名后');
  });

  it('probes chat Agent capabilities independently and hides the action for embedding models', async () => {
    const chat = model();
    const embedding = model({
      id: '22222222-2222-2222-2222-222222222222',
      configurationType: 'embedding'
    });
    const modelApi = api([chat, embedding]);
    vi.mocked(modelApi.testAgentCapabilities).mockResolvedValue({
      modelConfigurationId: chat.id,
      modelConfigurationVersion: 3,
      chat: true,
      functionTools: true,
      toolResultLoop: false,
      jsonObject: true,
      jsonSchema: false,
      testedAtUtc: '2026-07-29T05:00:00Z'
    });
    const wrapper = mountView(modelApi);
    await flushPromises();

    expect(wrapper.find(`[data-testid="test-agent-${chat.id}"]`).exists()).toBe(true);
    expect(wrapper.find(`[data-testid="test-agent-${embedding.id}"]`).exists()).toBe(false);

    await wrapper.get(`[data-testid="test-agent-${chat.id}"]`).trigger('click');
    await flushPromises();

    expect(modelApi.testAgentCapabilities).toHaveBeenCalledWith(chat.id);
    expect(wrapper.get(`[data-testid="agent-capabilities-${chat.id}"]`).text()).toContain('基础对话：支持');
    expect(wrapper.get(`[data-testid="agent-capabilities-${chat.id}"]`).text()).toContain('工具结果回传：不支持');
    expect(wrapper.get(`[data-testid="agent-capabilities-${chat.id}"]`).text()).toContain('JSON Schema：不支持');
  });

  it('clears keys explicitly and keeps server delete conflicts visible', async () => {
    const original = model();
    const modelApi = api([original]);
    vi.mocked(modelApi.clearApiKey).mockResolvedValue(model({ hasApiKey: false, lastFour: undefined, version: 1 }));
    vi.mocked(modelApi.delete).mockRejectedValue({
      response: { data: { code: 'model_reference_delete_blocked', retrievalAuditCount: 3 } }
    });
    const wrapper = mountView(modelApi);
    await flushPromises();

    await wrapper.get(`[data-testid="edit-${original.id}"]`).trigger('click');
    wrapper.findComponent(dialogStub).vm.$emit('clear-api-key', original);
    await flushPromises();
    expect(modelApi.clearApiKey).toHaveBeenCalledWith(original.id, original.version);

    await wrapper.get(`[data-testid="delete-${original.id}"]`).trigger('click');
    await flushPromises();
    expect(wrapper.text()).toContain('该配置已被 3 条检索审计引用，不能删除');
  });
});
