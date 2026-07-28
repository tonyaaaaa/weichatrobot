<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { ElAlert, ElButton, ElEmpty, ElSkeleton, ElTag } from 'element-plus';
import {
  modelApi,
  type ModelApi,
  type ModelConfiguration,
  type ModelConfigurationApiError,
  type ModelConfigurationDraft,
  type ModelConfigurationType
} from '../../api/models';
import ModelConfigurationDialog from './ModelConfigurationDialog.vue';
import { confirmAction as defaultConfirmAction } from '../../utils/dialogs';

const props = withDefaults(defineProps<{
  api?: ModelApi;
  confirmAction?: (message: string) => boolean | Promise<boolean>;
}>(), {
  api: () => modelApi,
  confirmAction: defaultConfirmAction
});

const loading = ref(true);
const busyId = ref('');
const error = ref('');
const notice = ref('');
const items = ref<ModelConfiguration[]>([]);
const dialogOpen = ref(false);
const editing = ref<ModelConfiguration>();

const groups = computed(() => ([
  {
    type: 'chat' as ModelConfigurationType,
    title: '对话模型',
    description: '用于群聊问答、上下文理解与回复生成。',
    items: items.value.filter(item => item.configurationType === 'chat')
  },
  {
    type: 'embedding' as ModelConfigurationType,
    title: '向量模型',
    description: '用于知识文档分段向量化与语义检索。',
    items: items.value.filter(item => item.configurationType === 'embedding')
  }
]));

async function load(): Promise<void> {
  loading.value = true;
  error.value = '';
  try {
    items.value = await props.api.list();
  } catch {
    error.value = '模型配置加载失败，请检查管理员权限和后端服务。';
  } finally {
    loading.value = false;
  }
}

function replaceById(configuration: ModelConfiguration): void {
  const index = items.value.findIndex(item => item.id === configuration.id);
  if (index >= 0) items.value.splice(index, 1, configuration);
  else items.value.push(configuration);
}

function openCreate(): void {
  editing.value = undefined;
  dialogOpen.value = true;
}

function openEdit(configuration: ModelConfiguration): void {
  editing.value = configuration;
  dialogOpen.value = true;
}

async function save(draft: ModelConfigurationDraft): Promise<void> {
  busyId.value = editing.value?.id ?? 'create';
  error.value = '';
  try {
    const saved = editing.value
      ? await props.api.update(editing.value.id, draft)
      : await props.api.create(draft);
    replaceById(saved);
    editing.value = saved;
    dialogOpen.value = false;
    notice.value = `${saved.name} 已保存。`;
  } catch (exception) {
    error.value = messageFor(exception, '模型配置保存失败，请检查字段后重试。');
  } finally {
    busyId.value = '';
  }
}

async function testConnection(configuration: ModelConfiguration): Promise<void> {
  await run(configuration, 'test', () => props.api.testConnection(configuration.id), updated => {
    notice.value = `${updated.name} 连接测试成功。`;
  });
}

async function testWebSearch(configuration: ModelConfiguration): Promise<void> {
  if (!props.api.testWebSearch) return;
  busyId.value = `${configuration.id}:web-search`;
  error.value = '';
  try {
    const result = await props.api.testWebSearch(configuration.id);
    notice.value = `${configuration.name} Web Search 测试成功，返回 ${result.sourceCount} 个合法来源。`;
  } catch {
    error.value = `${configuration.name} Web Search 测试失败；普通连接状态不受影响。`;
  } finally {
    busyId.value = '';
  }
}

async function toggleEnabled(configuration: ModelConfiguration): Promise<void> {
  await run(configuration, 'enabled', () =>
    props.api.setEnabled(configuration.id, !configuration.isEnabled, configuration.version), updated => {
    notice.value = `${updated.name} 已${updated.isEnabled ? '启用' : '停用'}。`;
  });
}

async function toggleDefault(configuration: ModelConfiguration): Promise<void> {
  await run(configuration, 'default', () =>
    props.api.setDefault(configuration.id, !configuration.isDefault, configuration.version), updated => {
    notice.value = updated.isDefault ? `${updated.name} 已设为默认。` : `${updated.name} 已取消默认。`;
  });
}

async function clearApiKey(configuration: ModelConfiguration): Promise<void> {
  if (!await props.confirmAction(`确认清除“${configuration.name}”保存的 API Key？清除后需要重新测试连接。`)) return;
  await run(configuration, 'clear-key', () =>
    props.api.clearApiKey(configuration.id, configuration.version), updated => {
    editing.value = updated;
    notice.value = `${updated.name} 的 API Key 已清除。`;
  });
}

async function remove(configuration: ModelConfiguration): Promise<void> {
  if (!await props.confirmAction(`确认删除“${configuration.name}”？此操作无法撤销。`)) return;
  busyId.value = `${configuration.id}:delete`;
  error.value = '';
  try {
    await props.api.delete(configuration.id, configuration.version);
    items.value = items.value.filter(item => item.id !== configuration.id);
    notice.value = `${configuration.name} 已删除。`;
  } catch (exception) {
    error.value = messageFor(exception, '模型配置删除失败，请稍后重试。');
  } finally {
    busyId.value = '';
  }
}

async function run(
  configuration: ModelConfiguration,
  action: string,
  operation: () => Promise<ModelConfiguration>,
  succeeded: (updated: ModelConfiguration) => void
): Promise<void> {
  busyId.value = `${configuration.id}:${action}`;
  error.value = '';
  try {
    const updated = await operation();
    replaceById(updated);
    succeeded(updated);
  } catch (exception) {
    error.value = messageFor(exception, `${configuration.name} 操作失败，请刷新后重试。`);
  } finally {
    busyId.value = '';
  }
}

function messageFor(exception: unknown, fallback: string): string {
  const data = (exception as { response?: { data?: ModelConfigurationApiError } })?.response?.data;
  switch (data?.code) {
    case 'model_name_conflict':
      return '配置名称已存在，请使用其他名称。';
    case 'model_concurrency_conflict':
      return '配置已被其他操作修改，请刷新后重试。';
    case 'model_test_required':
      return '请先对当前配置执行连接测试，测试成功后才能启用或设为默认。';
    case 'model_default_disable_forbidden':
      return '该配置是当前默认模型，请先选择其他默认模型。';
    case 'model_default_delete_blocked':
      return '请先取消默认模型后再删除。';
    case 'model_reference_delete_blocked':
      return `该配置已被 ${data.retrievalAuditCount ?? 0} 条检索审计引用，不能删除。`;
    default:
      return fallback;
  }
}

function keyText(configuration: ModelConfiguration): string {
  return configuration.hasApiKey ? `••••${configuration.lastFour ?? '已保存'}` : '未配置';
}

function connectionText(configuration: ModelConfiguration): string {
  if (configuration.connectionStatus === 'Succeeded') return '测试成功';
  if (configuration.connectionStatus === 'Failed') return '测试失败';
  return '待测试';
}

function connectionTagType(configuration: ModelConfiguration): 'info' | 'success' | 'danger' {
  if (configuration.connectionStatus === 'Succeeded') return 'success';
  if (configuration.connectionStatus === 'Failed') return 'danger';
  return 'info';
}

onMounted(load);
</script>

<template>
  <section class="ops-page" aria-labelledby="models-title">
    <header class="page-header">
      <div>
        <p class="eyebrow">系统配置</p>
        <h1 id="models-title">模型配置</h1>
        <p>统一管理 OpenAI 兼容对话与向量接口。名称可以修改，配置 ID 永久不变。</p>
      </div>
      <div class="header-actions">
        <ElButton :loading="loading" @click="load">刷新</ElButton>
        <ElButton data-testid="create-model" type="primary" @click="openCreate">新增模型配置</ElButton>
      </div>
    </header>

    <ElAlert v-if="error" :title="error" type="error" :closable="false" show-icon class="page-alert" />
    <ElSkeleton v-if="loading" :rows="7" animated aria-label="正在加载模型配置" />
    <template v-else>
      <ElEmpty v-if="!items.length" description="暂无模型配置，请先新增并完成连接测试。" />
      <template v-for="group in groups" :key="group.type">
      <section
        v-if="group.items.length"
        class="model-section"
        :aria-labelledby="`model-group-${group.type}`"
      >
        <header class="section-header">
          <div>
            <h2 :id="`model-group-${group.type}`">{{ group.title }}</h2>
            <p>{{ group.description }}</p>
          </div>
          <span class="count-badge">{{ group.items.length }}</span>
        </header>
        <div class="model-grid">
          <article
            v-for="item in group.items"
            :key="item.id"
            class="model-card"
            :data-testid="`model-card-${item.id}`"
          >
            <header class="card-header">
              <div class="card-title">
                <h3>{{ item.name }}</h3>
                <p>{{ item.provider }}</p>
              </div>
              <div class="status-tags">
                <ElTag :type="connectionTagType(item)" effect="light">{{ connectionText(item) }}</ElTag>
                <ElTag :type="item.isEnabled ? 'success' : 'info'" effect="plain">
                  {{ item.isEnabled ? '已启用' : '已停用' }}
                </ElTag>
                <ElTag v-if="item.isDefault" type="warning" effect="plain">默认</ElTag>
              </div>
            </header>

            <dl class="model-summary">
              <div><dt>接口地址</dt><dd class="mono" :title="item.baseUrl">{{ item.baseUrl }}</dd></div>
              <div><dt>模型名称</dt><dd class="mono">{{ item.model }}</dd></div>
              <div v-if="item.configurationType === 'embedding'"><dt>向量维度</dt><dd>{{ item.embeddingDimension ?? '未配置' }}</dd></div>
              <div v-if="item.configurationType === 'chat'"><dt>Web Search</dt><dd>{{ item.webSearchMode === 'ZaiChatCompletions' ? 'Z.AI Chat Completions' : '未启用' }}</dd></div>
              <div><dt>API Key</dt><dd class="mono">{{ keyText(item) }}</dd></div>
              <div><dt>调用策略</dt><dd>{{ item.timeoutSeconds }} 秒超时 · {{ item.maxRetries }} 次重试</dd></div>
            </dl>

            <p v-if="item.connectionStatus === 'Failed'" class="failure-note">
              最近测试失败：{{ item.lastTestFailureSummary ?? 'invalid_response' }}
            </p>

            <footer class="card-actions">
              <ElButton :data-testid="`edit-${item.id}`" @click="openEdit(item)">编辑</ElButton>
              <ElButton
                :data-testid="`test-${item.id}`"
                :loading="busyId === `${item.id}:test`"
                @click="testConnection(item)"
              >测试连接</ElButton>
              <ElButton
                v-if="item.configurationType === 'chat' && item.webSearchMode === 'ZaiChatCompletions'"
                :data-testid="`test-web-search-${item.id}`"
                :loading="busyId === `${item.id}:web-search`"
                @click="testWebSearch(item)"
              >测试 Web Search</ElButton>
              <ElButton
                :data-testid="`enable-${item.id}`"
                :disabled="item.connectionStatus !== 'Succeeded' || item.isDefault"
                :loading="busyId === `${item.id}:enabled`"
                @click="toggleEnabled(item)"
              >{{ item.isEnabled ? '停用' : '启用' }}</ElButton>
              <ElButton
                :data-testid="`default-${item.id}`"
                :disabled="!item.isDefault && item.connectionStatus !== 'Succeeded'"
                :loading="busyId === `${item.id}:default`"
                @click="toggleDefault(item)"
              >{{ item.isDefault ? '取消默认' : '设为默认' }}</ElButton>
              <ElButton
                :data-testid="`delete-${item.id}`"
                type="danger"
                plain
                :loading="busyId === `${item.id}:delete`"
                @click="remove(item)"
              >删除</ElButton>
            </footer>
          </article>
        </div>
      </section>
      </template>
    </template>

    <ModelConfigurationDialog
      v-model="dialogOpen"
      :configuration="editing"
      :saving="busyId === 'create' || busyId === editing?.id"
      @save="save"
      @clear-api-key="clearApiKey"
    />
    <p class="sr-only" aria-live="polite">{{ notice }}</p>
    <ElAlert v-if="notice" :title="notice" type="success" :closable="false" show-icon class="page-alert" />
  </section>
</template>

<style scoped>
.header-actions,
.status-tags,
.card-actions {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
}

.page-alert {
  margin-bottom: 16px;
}

.model-section + .model-section {
  margin-top: 28px;
}

.section-header,
.card-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
}

.section-header {
  margin-bottom: 12px;
}

.section-header h2,
.card-title h3 {
  margin: 0;
}

.section-header p,
.card-title p {
  margin: 4px 0 0;
  color: var(--el-text-color-secondary);
}

.count-badge {
  min-width: 28px;
  padding: 3px 9px;
  border-radius: 999px;
  background: var(--el-fill-color-light);
  color: var(--el-text-color-secondary);
  text-align: center;
  font-size: 13px;
}

.model-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(100%, 420px), 1fr));
  gap: 16px;
}

.model-card {
  padding: 18px;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 12px;
  background: var(--el-bg-color);
  box-shadow: var(--el-box-shadow-lighter);
}

.model-summary {
  display: grid;
  gap: 10px;
  margin: 18px 0;
}

.model-summary > div {
  display: grid;
  grid-template-columns: 88px minmax(0, 1fr);
  gap: 12px;
}

.model-summary dt {
  color: var(--el-text-color-secondary);
}

.model-summary dd {
  min-width: 0;
  margin: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mono {
  font-family: var(--mono, ui-monospace, SFMono-Regular, Consolas, monospace);
}

.failure-note {
  margin: -4px 0 14px;
  color: var(--el-color-danger);
  font-size: 13px;
}

.card-actions :deep(.el-button) {
  min-height: 40px;
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
}

@media (max-width: 640px) {
  .page-header,
  .section-header,
  .card-header {
    flex-direction: column;
  }

  .header-actions,
  .header-actions :deep(.el-button) {
    width: 100%;
  }

  .model-summary > div {
    grid-template-columns: 1fr;
    gap: 2px;
  }
}
</style>
