<script setup lang="ts">
import { reactive, watch } from 'vue';
import {
  ElAlert,
  ElButton,
  ElDialog,
  ElForm,
  ElFormItem,
  ElInput,
  ElInputNumber,
  ElOption,
  ElSelect
} from 'element-plus';
import type {
  ModelConfiguration,
  ModelConfigurationDraft,
  ModelConfigurationType
} from '../../api/models';

const props = withDefaults(defineProps<{
  modelValue: boolean;
  configuration?: ModelConfiguration;
  saving?: boolean;
}>(), {
  configuration: undefined,
  saving: false
});

const emit = defineEmits<{
  'update:modelValue': [value: boolean];
  save: [draft: ModelConfigurationDraft];
  'clear-api-key': [configuration: ModelConfiguration];
}>();

const emptyDraft = (): ModelConfigurationDraft => ({
  name: '',
  provider: 'OpenAI 兼容',
  configurationType: 'chat',
  baseUrl: '',
  model: '',
  apiKey: '',
  timeoutSeconds: 30,
  maxRetries: 0
});

const draft = reactive<ModelConfigurationDraft>(emptyDraft());
const errors = reactive({ name: '', baseUrl: '', model: '' });

function resetDraft(): void {
  const source = props.configuration
    ? {
        name: props.configuration.name,
        provider: props.configuration.provider,
        configurationType: props.configuration.configurationType,
        baseUrl: props.configuration.baseUrl,
        model: props.configuration.model,
        apiKey: '',
        timeoutSeconds: props.configuration.timeoutSeconds,
        maxRetries: props.configuration.maxRetries,
        version: props.configuration.version
      }
    : emptyDraft();
  Object.assign(draft, source);
  errors.name = '';
  errors.baseUrl = '';
  errors.model = '';
}

watch(
  () => [props.modelValue, props.configuration] as const,
  ([visible]) => {
    if (visible) resetDraft();
  },
  { immediate: true }
);

function validate(): boolean {
  errors.name = draft.name.trim() ? '' : '请输入配置名称';
  errors.model = draft.model.trim() ? '' : '请输入模型名称';
  errors.baseUrl = '';
  try {
    const url = new URL(draft.baseUrl);
    if (url.protocol !== 'http:' && url.protocol !== 'https:') {
      errors.baseUrl = '请输入有效的 HTTP 或 HTTPS 地址';
    }
  } catch {
    errors.baseUrl = '请输入有效的 HTTP 或 HTTPS 地址';
  }
  return !errors.name && !errors.baseUrl && !errors.model;
}

function submit(): void {
  if (!validate()) return;
  emit('save', {
    ...draft,
    name: draft.name.trim(),
    provider: draft.provider.trim(),
    baseUrl: draft.baseUrl.trim().replace(/\/+$/, ''),
    model: draft.model.trim()
  });
}

function close(): void {
  emit('update:modelValue', false);
}
</script>

<template>
  <ElDialog
    :model-value="modelValue"
    :title="configuration ? '编辑模型配置' : '新增模型配置'"
    width="min(640px, calc(100vw - 32px))"
    :close-on-click-modal="!saving"
    :close-on-press-escape="!saving"
    :teleported="false"
    @close="close"
  >
    <ElAlert
      v-if="configuration"
      title="名称可以修改，配置 ID 永久不变。"
      type="info"
      :closable="false"
      show-icon
      class="dialog-note"
    />
    <ElForm label-position="top" @submit.prevent="submit">
      <div class="field-grid">
        <ElFormItem label="配置名称" :error="errors.name">
          <ElInput
            v-model="draft.name"
            data-testid="model-name"
            aria-label="配置名称"
            maxlength="128"
            show-word-limit
            @blur="validate"
          />
          <p v-if="errors.name" class="field-error" role="alert">{{ errors.name }}</p>
        </ElFormItem>
        <ElFormItem label="配置类型">
          <ElSelect v-model="draft.configurationType" aria-label="配置类型">
            <ElOption label="对话模型" value="chat" />
            <ElOption label="向量模型" value="embedding" />
          </ElSelect>
        </ElFormItem>
      </div>
      <ElFormItem label="服务商">
        <ElInput v-model="draft.provider" aria-label="服务商" />
      </ElFormItem>
      <ElFormItem label="接口地址" :error="errors.baseUrl">
        <ElInput
          v-model="draft.baseUrl"
          data-testid="model-base-url"
          aria-label="接口地址"
          placeholder="https://api.example.com"
          @blur="validate"
        />
        <p v-if="errors.baseUrl" class="field-error" role="alert">{{ errors.baseUrl }}</p>
      </ElFormItem>
      <ElFormItem label="模型名称" :error="errors.model">
        <ElInput
          v-model="draft.model"
          data-testid="model-model"
          aria-label="模型名称"
          @blur="validate"
        />
        <p v-if="errors.model" class="field-error" role="alert">{{ errors.model }}</p>
      </ElFormItem>
      <ElFormItem label="API Key">
        <ElInput
          v-model="draft.apiKey"
          type="password"
          show-password
          autocomplete="new-password"
          data-testid="model-api-key"
          aria-label="API Key"
        />
        <p class="field-help">
          <template v-if="configuration?.hasApiKey">
            当前密钥末四位：<span class="mono">{{ configuration.lastFour ?? '已保存' }}</span>；留空将保留现有密钥。
          </template>
          <template v-else>可留空，适用于本机或无需鉴权的 OpenAI 兼容接口。</template>
        </p>
      </ElFormItem>
      <div class="field-grid compact-grid">
        <ElFormItem label="超时（秒）">
          <ElInputNumber v-model="draft.timeoutSeconds" :min="1" :max="300" controls-position="right" />
        </ElFormItem>
        <ElFormItem label="最大重试次数">
          <ElInputNumber v-model="draft.maxRetries" :min="0" :max="5" controls-position="right" />
        </ElFormItem>
      </div>
    </ElForm>

    <template #footer>
      <div class="dialog-actions">
        <ElButton
          v-if="configuration?.hasApiKey"
          data-testid="clear-model-api-key"
          type="danger"
          plain
          :disabled="saving"
          @click="emit('clear-api-key', configuration)"
        >
          清除密钥
        </ElButton>
        <span class="action-spacer" />
        <ElButton :disabled="saving" @click="close">取消</ElButton>
        <ElButton data-testid="model-save" type="primary" :loading="saving" @click="submit">
          {{ saving ? '保存中…' : '保存' }}
        </ElButton>
      </div>
    </template>
  </ElDialog>
</template>

<style scoped>
.dialog-note {
  margin-bottom: 18px;
}

.field-grid {
  display: grid;
  grid-template-columns: minmax(0, 1.4fr) minmax(180px, 0.6fr);
  gap: 16px;
}

.compact-grid {
  grid-template-columns: repeat(2, minmax(0, 1fr));
}

.field-help {
  margin: 6px 0 0;
  color: var(--el-text-color-secondary);
  font-size: 13px;
  line-height: 1.5;
}

.field-error {
  width: 100%;
  margin: 4px 0 0;
  color: var(--el-color-danger);
  font-size: 12px;
  line-height: 1.4;
}

.mono {
  font-family: var(--mono, ui-monospace, SFMono-Regular, Consolas, monospace);
}

.dialog-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.action-spacer {
  flex: 1;
}

@media (max-width: 640px) {
  .field-grid {
    grid-template-columns: 1fr;
    gap: 0;
  }

  .dialog-actions {
    flex-wrap: wrap;
  }
}
</style>
