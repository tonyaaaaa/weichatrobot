import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import ModelConfigurationDialog from './ModelConfigurationDialog.vue';
import type { ModelConfiguration } from '../../api/models';

const dialogStub = {
  props: ['modelValue'],
  template: '<div v-if="modelValue" role="dialog"><slot /><slot name="footer" /></div>'
};

const existing: ModelConfiguration = {
  id: '11111111-1111-1111-1111-111111111111',
  name: '现有模型',
  provider: 'OpenAI 兼容',
  configurationType: 'chat',
  baseUrl: 'https://provider.example.test',
  model: 'qwen',
  timeoutSeconds: 30,
  maxRetries: 0,
  isEnabled: false,
  isDefault: false,
  connectionStatus: 'Untested',
  hasApiKey: true,
  lastFour: '1234',
  version: 2
};

describe('ModelConfigurationDialog', () => {
  it('renders the form inside an Element Plus dialog with number controls', async () => {
    const wrapper = mount(ModelConfigurationDialog, {
      props: { modelValue: true }
    });
    await flushPromises();

    expect(wrapper.find('.el-dialog').exists()).toBe(true);
    expect(wrapper.find('.el-dialog__body').exists()).toBe(true);
    expect(wrapper.findAll('.el-input-number')).toHaveLength(2);
  });

  it('blocks blank names and invalid URLs with inline messages', async () => {
    const wrapper = mount(ModelConfigurationDialog, {
      props: { modelValue: true },
      global: { stubs: { ElDialog: dialogStub } }
    });

    await wrapper.get('[data-testid="model-save"]').trigger('click');
    await flushPromises();
    expect(wrapper.text()).toContain('请输入配置名称');

    await wrapper.get('[data-testid="model-name"]').setValue('本地模型');
    await wrapper.get('[data-testid="model-base-url"]').setValue('not-a-url');
    await wrapper.get('[data-testid="model-model"]').setValue('qwen');
    await wrapper.get('[data-testid="model-save"]').trigger('click');
    await flushPromises();
    expect(wrapper.text()).toContain('请输入有效的 HTTP 或 HTTPS 地址');
    expect(wrapper.emitted('save')).toBeUndefined();
  });

  it('offers only chat and embedding and emits a complete isolated draft', async () => {
    const wrapper = mount(ModelConfigurationDialog, {
      props: { modelValue: true },
      global: { stubs: { ElDialog: dialogStub } }
    });

    const optionValues = wrapper.findAllComponents({ name: 'ElOption' }).map(option => option.props('value'));
    expect(optionValues).toEqual(['chat', 'embedding', 'None', 'ZaiChatCompletions']);
    await wrapper.get('[data-testid="model-name"]').setValue('本地模型');
    await wrapper.get('[data-testid="model-base-url"]').setValue('http://127.0.0.1:11434');
    await wrapper.get('[data-testid="model-model"]').setValue('qwen');
    await wrapper.get('[data-testid="model-save"]').trigger('click');
    await flushPromises();

    expect(wrapper.emitted('save')).toHaveLength(1);
    expect(wrapper.emitted('save')?.[0]?.[0]).toEqual(expect.objectContaining({
      name: '本地模型',
      provider: 'OpenAI 兼容',
      configurationType: 'chat',
      baseUrl: 'http://127.0.0.1:11434',
      model: 'qwen',
      apiKey: '',
      timeoutSeconds: 30,
      maxRetries: 0,
      webSearchMode: 'None'
    }));
  });

  it('requires and emits a vector dimension only for embedding configurations', async () => {
    const wrapper = mount(ModelConfigurationDialog, {
      props: { modelValue: true },
      global: { stubs: { ElDialog: dialogStub } }
    });

    const type = wrapper.getComponent({ name: 'ElSelect' });
    await type.setValue('embedding');
    expect(wrapper.find('[data-testid="embedding-dimension"]').exists()).toBe(true);
    await wrapper.get('[data-testid="model-name"]').setValue('向量模型');
    await wrapper.get('[data-testid="model-base-url"]').setValue('https://provider.example.test/v1');
    await wrapper.get('[data-testid="model-model"]').setValue('embedding-model');
    await wrapper.get('[data-testid="model-save"]').trigger('click');
    expect(wrapper.text()).toContain('请输入向量维度');

    await wrapper.get('[data-testid="embedding-dimension"] input').setValue('1024');
    await wrapper.get('[data-testid="model-save"]').trigger('click');
    expect(wrapper.emitted('save')?.at(-1)?.[0]).toEqual(expect.objectContaining({
      configurationType: 'embedding',
      embeddingDimension: 1024
    }));
  });

  it('never pre-fills an existing key and emits explicit clear confirmation', async () => {
    const wrapper = mount(ModelConfigurationDialog, {
      props: { modelValue: true, configuration: existing },
      global: { stubs: { ElDialog: dialogStub } }
    });

    expect((wrapper.get('[data-testid="model-api-key"]').element as HTMLInputElement).value).toBe('');
    expect(wrapper.text()).toContain('留空将保留现有密钥');
    expect(wrapper.text()).toContain('当前密钥末四位：1234');
    await wrapper.get('[data-testid="clear-model-api-key"]').trigger('click');
    expect(wrapper.emitted('clear-api-key')?.[0]).toEqual([existing]);
    expect('apiKey' in existing).toBe(false);
  });
});
