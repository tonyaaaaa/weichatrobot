<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import {
  ElAlert,
  ElButton,
  ElEmpty,
  ElMessage,
  ElOption,
  ElPagination,
  ElSelect,
  ElSkeleton,
  ElTable,
  ElTableColumn,
  ElTag
} from 'element-plus';
import {
  sendCommandsApi,
  type SendCommandItem,
  type SendCommandPage,
  type SendCommandsApi
} from '../../api/sendCommands';
import type { WorkToolRobotOption } from '../../api/worktool';
import { formatBeijingTime } from '../../utils/beijingTime';
import { confirmAction } from '../../utils/dialogs';

const props = withDefaults(defineProps<{ api?: SendCommandsApi; initialGroup?: string }>(), {
  api: () => sendCommandsApi,
  initialGroup: ''
});

const filters = reactive({
  robotConfigId: '',
  group: props.initialGroup,
  status: '',
  fromLocal: '',
  toLocal: ''
});
const page = ref<SendCommandPage>({ items: [], total: 0, page: 1, pageSize: 20 });
const robots = ref<WorkToolRobotOption[]>([]);
const loading = ref(true);
const error = ref('');
const mutatingId = ref('');

const statuses = [
  'pending',
  'retrying',
  'leased',
  'dispatching',
  'deliveryUnknown',
  'deliveryUnknownResolved',
  'executedSucceeded',
  'executedPartially',
  'executedFailed',
  'resultTimeout',
  'blocked',
  'deadLetter',
  'cancelled'
];

const hasItems = computed(() => page.value.items.length > 0);

async function load(requestedPage = page.value.page): Promise<void> {
  loading.value = true;
  error.value = '';
  try {
    page.value = await props.api.list({
      robotConfigId: filters.robotConfigId || undefined,
      group: filters.group.trim() || undefined,
      status: filters.status || undefined,
      fromUtc: toUtc(filters.fromLocal),
      toUtc: toUtc(filters.toLocal),
      page: requestedPage,
      pageSize: page.value.pageSize
    });
  } catch {
    error.value = '发送队列加载失败，请检查服务状态后重试。';
  } finally {
    loading.value = false;
  }
}

async function loadRobots(): Promise<void> {
  try {
    robots.value = await props.api.listRobots();
  } catch {
    error.value = '机器人选项加载失败，仍可使用其他条件查询。';
  }
}

async function applyFilters(): Promise<void> {
  await load(1);
}

async function cancelCommand(item: SendCommandItem): Promise<void> {
  const confirmed = await confirmAction(
    `确认取消“${item.robotName} / ${item.groupName}”的这条待发送命令？取消后不会自动恢复。`,
    { title: '取消发送', confirmButtonText: '确认取消', danger: true }
  );
  if (!confirmed) return;
  await mutate(item, () => props.api.cancel(item.id, item.version), '发送命令已取消。');
}

async function cancelRow(row: unknown): Promise<void> {
  await cancelCommand(row as SendCommandItem);
}

async function acknowledgeUnknown(item: SendCommandItem): Promise<void> {
  const confirmed = await confirmAction(
    '确认已人工核对这条投递结果未知的命令？此操作仅关闭异常记录，不代表消息已发送成功。',
    { title: '确认已处理', confirmButtonText: '确认已处理' }
  );
  if (!confirmed) return;
  await mutate(
    item,
    () => props.api.acknowledgeUnknown(item.id, item.version),
    '未知投递记录已标记为人工处理。'
  );
}

async function acknowledgeRow(row: unknown): Promise<void> {
  await acknowledgeUnknown(row as SendCommandItem);
}

async function mutate(
  item: SendCommandItem,
  action: () => Promise<unknown>,
  successMessage: string
): Promise<void> {
  mutatingId.value = item.id;
  error.value = '';
  try {
    await action();
    ElMessage.success(successMessage);
    await load();
  } catch (reason: any) {
    if (reason?.response?.status === 409) {
      error.value = '记录状态已变化，列表已自动刷新，请重新确认。';
      await reloadAfterConflict();
    } else {
      error.value = '操作失败，请稍后重试。';
    }
  } finally {
    mutatingId.value = '';
  }
}

async function reloadAfterConflict(): Promise<void> {
  try {
    const refreshed = await props.api.list({
      robotConfigId: filters.robotConfigId || undefined,
      group: filters.group.trim() || undefined,
      status: filters.status || undefined,
      fromUtc: toUtc(filters.fromLocal),
      toUtc: toUtc(filters.toLocal),
      page: page.value.page,
      pageSize: page.value.pageSize
    });
    page.value = refreshed;
  } catch {
    error.value = '记录状态已变化，但列表刷新失败，请手动刷新。';
  }
}

function toUtc(value: string): string | undefined {
  if (!value) return undefined;
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? undefined : parsed.toISOString();
}

function statusLabel(status: string): string {
  const labels: Record<string, string> = {
    pending: '待发送',
    retrying: '等待重试',
    leased: '已租用',
    dispatching: '发送中',
    deliveryUnknown: '投递结果未知',
    deliveryUnknownResolved: '未知结果已处理',
    executedSucceeded: '执行成功',
    executedPartially: '部分成功',
    executedFailed: '执行失败',
    resultTimeout: '结果超时',
    blocked: '已阻塞',
    deadLetter: '死信',
    cancelled: '已取消'
  };
  return labels[status] ?? status;
}

function statusType(status: string): 'success' | 'warning' | 'danger' | 'info' | 'primary' {
  if (status === 'executedSucceeded') return 'success';
  if (status === 'deliveryUnknown' || status === 'retrying' || status === 'resultTimeout') return 'warning';
  if (status === 'executedFailed' || status === 'deadLetter' || status === 'blocked') return 'danger';
  if (status === 'pending' || status === 'dispatching' || status === 'leased') return 'primary';
  return 'info';
}

onMounted(() => Promise.all([load(), loadRobots()]));
</script>

<template>
  <section class="send-queue-page" aria-labelledby="send-queue-title">
    <header class="page-header">
      <div>
        <p class="eyebrow">运营与发送</p>
        <h1 id="send-queue-title">发送队列</h1>
        <p>查看 WorkTool 发送命令状态，取消尚未发送的命令，并人工关闭投递结果未知记录。</p>
      </div>
      <ElButton :loading="loading" @click="() => load()">刷新</ElButton>
    </header>

    <ElAlert
      title="投递结果未知不会阻塞后续消息，也不会自动重发"
      type="warning"
      :closable="false"
      show-icon
    >
      请先在群内核对是否已经收到，再点击“确认已处理”，避免重复发送。
    </ElAlert>

    <section class="panel filter-grid" aria-label="发送队列筛选">
      <label>机器人
        <ElSelect v-model="filters.robotConfigId" clearable filterable placeholder="全部机器人">
          <ElOption
            v-for="robot in robots"
            :key="robot.id"
            :label="robot.name"
            :value="robot.id"
          />
        </ElSelect>
      </label>
      <label>群名称
        <input v-model="filters.group" maxlength="256" placeholder="输入群名称">
      </label>
      <label>状态
        <ElSelect v-model="filters.status" clearable placeholder="全部状态">
          <ElOption
            v-for="status in statuses"
            :key="status"
            :label="statusLabel(status)"
            :value="status"
          />
        </ElSelect>
      </label>
      <label>开始时间
        <input v-model="filters.fromLocal" type="datetime-local">
      </label>
      <label>结束时间
        <input v-model="filters.toLocal" type="datetime-local">
      </label>
      <ElButton type="primary" data-testid="apply-send-command-filters" @click="applyFilters">
        查询
      </ElButton>
    </section>

    <ElAlert v-if="error" :title="error" type="error" :closable="false" show-icon />
    <ElSkeleton v-if="loading && !hasItems" :rows="7" animated />

    <section v-else class="panel queue-panel">
      <ElEmpty v-if="!hasItems" description="暂无符合条件的发送命令。" />
      <ElTable v-else :data="page.items" row-key="id">
        <ElTableColumn prop="robotName" label="机器人" min-width="150" />
        <ElTableColumn prop="groupName" label="群名称" min-width="180" />
        <ElTableColumn label="状态" min-width="140">
          <template #default="{ row }">
            <ElTag :type="statusType(row.status)">{{ statusLabel(row.status) }}</ElTag>
          </template>
        </ElTableColumn>
        <ElTableColumn prop="attemptCount" label="尝试次数" width="90" />
        <ElTableColumn label="消息" width="110">
          <template #default="{ row }">{{ row.messageLength }} 个字符</template>
        </ElTableColumn>
        <ElTableColumn label="创建时间" min-width="180">
          <template #default="{ row }">{{ formatBeijingTime(row.createdAtUtc) }}</template>
        </ElTableColumn>
        <ElTableColumn label="操作" min-width="190" fixed="right">
          <template #default="{ row }">
            <ElButton
              v-if="row.status === 'pending' || row.status === 'retrying'"
              :data-testid="`cancel-command-${row.id}`"
              :loading="mutatingId === row.id"
              type="danger"
              plain
              @click="cancelRow(row)"
            >
              取消发送
            </ElButton>
            <ElButton
              v-else-if="row.status === 'deliveryUnknown'"
              :data-testid="`acknowledge-command-${row.id}`"
              :loading="mutatingId === row.id"
              type="warning"
              plain
              @click="acknowledgeRow(row)"
            >
              确认已处理
            </ElButton>
            <span v-else class="read-only-state">只读</span>
          </template>
        </ElTableColumn>
      </ElTable>
      <ElPagination
        v-if="page.total > page.pageSize"
        :current-page="page.page"
        :page-size="page.pageSize"
        :total="page.total"
        layout="prev, pager, next, total"
        @current-change="load"
      />
    </section>
  </section>
</template>

<style scoped>
.send-queue-page {
  display: grid;
  width: 100%;
  max-width: 1440px;
  margin: 0 auto;
  gap: var(--space-xl);
}

.page-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--space-lg);
}

.page-header p,
.read-only-state {
  color: var(--color-muted-text);
}

.panel {
  min-width: 0;
  padding: var(--space-xl);
  border: 1px solid var(--color-border);
  border-radius: .75rem;
  background: var(--color-surface);
  box-shadow: var(--shadow-sm);
}

.filter-grid {
  display: grid;
  grid-template-columns: repeat(5, minmax(10rem, 1fr)) auto;
  align-items: end;
  gap: var(--space-md);
}

.filter-grid label {
  display: grid;
  min-width: 0;
  gap: var(--space-xs);
}

.filter-grid input {
  width: 100%;
  min-width: 0;
  min-height: 44px;
  box-sizing: border-box;
}

.queue-panel {
  display: grid;
  gap: var(--space-lg);
  overflow: hidden;
}

@media (max-width: 1100px) {
  .filter-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 600px) {
  .page-header {
    flex-direction: column;
  }

  .filter-grid {
    grid-template-columns: 1fr;
  }
}
</style>
