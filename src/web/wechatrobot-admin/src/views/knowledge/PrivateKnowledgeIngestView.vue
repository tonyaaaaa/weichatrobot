<script setup lang="ts">
import { onMounted, ref } from 'vue';
import {
  ElAlert,
  ElButton,
  ElEmpty,
  ElMessage,
  ElOption,
  ElSelect,
  ElTable,
  ElTableColumn,
  ElTag
} from 'element-plus';
import {
  privateKnowledgeIngestApi,
  type PrivateKnowledgeIngestApi,
  type PrivateKnowledgeIngestBatch,
  type PrivateKnowledgeIngestStatus
} from '../../api/privateKnowledgeIngest';

const props = withDefaults(defineProps<{ api?: PrivateKnowledgeIngestApi }>(), {
  api: () => privateKnowledgeIngestApi
});
const items = ref<PrivateKnowledgeIngestBatch[]>([]);
const loading = ref(true);
const error = ref('');
const status = ref('');
const retryingId = ref('');

const labels: Record<PrivateKnowledgeIngestStatus, string> = {
  Received: '等待处理',
  Extracting: '正在整理',
  Comparing: '正在比对',
  Staged: '等待索引',
  Indexing: '正在索引',
  Activated: '已入库',
  Retryable: '可重试',
  Failed: '失败'
};
function label(value: PrivateKnowledgeIngestStatus): string {
  return labels[value] ?? value;
}
function type(value: PrivateKnowledgeIngestStatus): 'success' | 'warning' | 'danger' | 'info' {
  if (value === 'Activated') return 'success';
  if (value === 'Failed') return 'danger';
  if (value === 'Retryable') return 'warning';
  return 'info';
}
function canRetry(item: PrivateKnowledgeIngestBatch): boolean {
  return item.status === 'Failed' || item.status === 'Retryable';
}
function asBatch(value: unknown): PrivateKnowledgeIngestBatch {
  return value as PrivateKnowledgeIngestBatch;
}
async function load(): Promise<void> {
  loading.value = true;
  error.value = '';
  try {
    items.value = await props.api.list({ status: status.value || undefined });
  } catch {
    error.value = '私聊入库批次加载失败，请重试。';
  } finally {
    loading.value = false;
  }
}
async function retry(item: PrivateKnowledgeIngestBatch): Promise<void> {
  retryingId.value = item.id;
  try {
    const updated = await props.api.retry(item.id, item.version);
    const index = items.value.findIndex(value => value.id === item.id);
    if (index >= 0) items.value.splice(index, 1, updated);
    ElMessage.success('已重新加入处理队列');
  } catch {
    ElMessage.error('重试失败，批次状态可能已经变化，请刷新后再试。');
  } finally {
    retryingId.value = '';
  }
}
onMounted(load);
</script>

<template>
  <section class="ops-page">
    <header class="page-header">
      <div>
        <p class="eyebrow">私聊自动整理</p>
        <h1>私聊知识入库</h1>
        <p>查看内部同事私聊触发的自动整理、比对、索引和直接入库结果。</p>
      </div>
      <ElButton @click="load">刷新</ElButton>
    </header>
    <ElAlert
      title="WorkTool 私聊只提供兼容显示名，页面不会把昵称描述为稳定企业微信成员 ID。"
      type="info"
      :closable="false"
      show-icon
    />
    <div class="toolbar">
      <ElSelect v-model="status" placeholder="全部状态" clearable @change="load">
        <ElOption label="等待处理" value="Received" />
        <ElOption label="处理中" value="Extracting" />
        <ElOption label="正在索引" value="Indexing" />
        <ElOption label="已入库" value="Activated" />
        <ElOption label="可重试" value="Retryable" />
        <ElOption label="失败" value="Failed" />
      </ElSelect>
    </div>
    <ElAlert v-if="error" :title="error" type="error" :closable="false">
      <ElButton data-testid="reload-private-ingests" @click="load">重试</ElButton>
    </ElAlert>
    <ElEmpty v-else-if="!loading && !items.length" description="暂无私聊入库批次" />
    <ElTable v-else v-loading="loading" :data="items">
      <ElTableColumn prop="sourceActorDisplayName" label="来源显示名" min-width="130" />
      <ElTableColumn label="状态" width="110">
        <template #default="{ row }">
          <ElTag :type="type(row.status)">{{ label(row.status) }}</ElTag>
        </template>
      </ElTableColumn>
      <ElTableColumn label="整理结果" min-width="260">
        <template #default="{ row }">
          <div class="statistics">
            <span>新增 {{ row.newCount }}</span>
            <span>重复 {{ row.duplicateCount }}</span>
            <span>补充 {{ row.supplementCount }}</span>
            <span>纠正 {{ row.correctionCount }}</span>
          </div>
        </template>
      </ElTableColumn>
      <ElTableColumn prop="failureCode" label="失败代码" min-width="170">
        <template #default="{ row }">{{ row.failureCode || '—' }}</template>
      </ElTableColumn>
      <ElTableColumn label="更新时间" min-width="170">
        <template #default="{ row }">{{ new Date(row.updatedAtUtc).toLocaleString('zh-CN') }}</template>
      </ElTableColumn>
      <ElTableColumn label="操作" width="100" fixed="right">
        <template #default="{ row }">
          <ElButton
            v-if="canRetry(asBatch(row))"
            link
            type="primary"
            :loading="retryingId === row.id"
            :data-testid="`retry-${row.id}`"
            @click="retry(asBatch(row))"
          >
            重试
          </ElButton>
        </template>
      </ElTableColumn>
    </ElTable>
  </section>
</template>

<style scoped>
.toolbar { display: flex; max-width: 18rem; margin: 1rem 0; }
.toolbar :deep(.el-select) { width: 100%; }
.statistics { display: flex; flex-wrap: wrap; gap: .35rem .9rem; }
</style>
