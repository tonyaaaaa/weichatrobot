<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import {
  ElAlert, ElButton, ElEmpty, ElMessage, ElPagination, ElSelect, ElOption,
  ElSkeleton, ElTable, ElTableColumn, ElTag
} from 'element-plus';
import {
  memoryApi, type MemoryApi, type MemoryCandidate, type MemoryEntry, type MemoryJob
} from '../../api/memory';
import { confirmAction, promptAction } from '../../utils/dialogs';
import { formatBeijingTime } from '../../utils/beijingTime';
import GroupProfileSelect from '../../components/groups/GroupProfileSelect.vue';
import { groupOptionApi, type GroupOptionApi } from '../../api/groupOptions';

const props = withDefaults(defineProps<{
  initialGroupId?: string;
  api?: MemoryApi;
  groupOptionApi?: GroupOptionApi;
}>(), {
  initialGroupId: '',
  api: () => memoryApi,
  groupOptionApi: () => groupOptionApi
});
type Tab = 'candidates' | 'entries' | 'jobs';
const tab = ref<Tab>('candidates');
const loading = ref(true);
const error = ref('');
const groupOptionError = ref('');
const groupProfileId = ref(props.initialGroupId);
const status = ref('');
const page = ref(1);
const pageSize = 20;
const total = ref(0);
const candidates = ref<MemoryCandidate[]>([]);
const entries = ref<MemoryEntry[]>([]);
const jobs = ref<MemoryJob[]>([]);
const currentItems = computed(() => tab.value === 'candidates' ? candidates.value : tab.value === 'entries' ? entries.value : jobs.value);

async function load() {
  loading.value = true; error.value = '';
  const query = { groupProfileId: groupProfileId.value, status: status.value, page: page.value, pageSize };
  try {
    if (tab.value === 'candidates') {
      const result = await props.api.listCandidates(query); candidates.value = result.items; total.value = result.total;
    } else if (tab.value === 'entries') {
      const result = await props.api.listEntries(query); entries.value = result.items; total.value = result.total;
    } else {
      const result = await props.api.listJobs(query); jobs.value = result.items; total.value = result.total;
    }
  } catch {
    error.value = '记忆数据加载失败，请重试。';
  } finally { loading.value = false; }
}
function selectTab(value: Tab) { tab.value = value; status.value = ''; page.value = 1; void load(); }
function filter() { page.value = 1; void load(); }
function onGroupLoadError() {
  groupOptionError.value = '群选择项加载失败，请刷新页面重试。';
}
function changePage(value: number) { page.value = value; void load(); }
async function promote(value: unknown) {
  const row = value as MemoryCandidate;
  if (!await confirmAction('确认将该候选晋升为长期记忆？')) return;
  await mutate(() => props.api.promoteCandidate(row.id, row.version), '候选已晋升。');
}
async function reject(value: unknown) {
  const row = value as MemoryCandidate;
  if (!await confirmAction('确认拒绝该记忆候选？', { danger: true })) return;
  await mutate(() => props.api.rejectCandidate(row.id, row.version), '候选已拒绝。');
}
async function reorganize(value: unknown) {
  const row = value as MemoryCandidate;
  if (!await confirmAction('确认使用原始证据重新整理该候选？')) return;
  await mutate(() => props.api.reorganizeCandidate(row.id, row.version), '候选已重新排队整理。');
}
async function edit(raw: unknown) {
  const row = raw as MemoryCandidate;
  const content = await promptAction('编辑候选内容（不会修改原始会话）', { inputValue: row.content });
  if (content === null || content.trim() === row.content) return;
  await mutate(() => props.api.editCandidate(row.id, content, row.confidence, row.version), '候选已更新。');
}
async function forget(value: unknown) {
  const row = value as MemoryEntry;
  if (!await confirmAction('确认忘记该长期记忆？后续回答将不再召回。', { danger: true })) return;
  await mutate(() => props.api.forgetEntry(row.id, row.version), '记忆已忘记。');
}
async function restore(value: unknown) {
  const row = value as MemoryEntry;
  if (!await confirmAction('确认恢复该长期记忆？')) return;
  await mutate(() => props.api.restoreEntry(row.id, row.version), '记忆已恢复。');
}
async function retry(value: unknown) {
  const row = value as MemoryJob;
  if (!await confirmAction('确认重试该记忆整理任务？')) return;
  await mutate(() => props.api.retryJob(row.id, row.version), '任务已重新排队。');
}
async function mutate(action: () => Promise<unknown>, success: string) {
  try { await action(); ElMessage.success(success); await load(); }
  catch (exception) {
    const statusCode = (exception as { response?: { status?: number } })?.response?.status;
    error.value = statusCode === 409 ? '数据已被其他操作修改，列表已刷新。' : '操作失败，请重试。';
    await load();
  }
}
onMounted(load);
</script>

<template>
  <section class="ops-page memory-center" aria-labelledby="memory-title">
    <header class="page-header">
      <div>
        <p class="eyebrow">自动整理与长期召回</p>
        <h1 id="memory-title">记忆中心</h1>
        <p>用户偏好、群规则和机器人经验可晋升为长期记忆；业务事实必须进入知识学习审核。</p>
      </div>
      <ElButton @click="load">刷新</ElButton>
    </header>
    <div class="tab-strip" role="tablist" aria-label="记忆中心分类">
      <ElButton :type="tab === 'candidates' ? 'primary' : 'default'" role="tab" :aria-selected="tab === 'candidates'" @click="selectTab('candidates')">待整理</ElButton>
      <ElButton :type="tab === 'entries' ? 'primary' : 'default'" role="tab" :aria-selected="tab === 'entries'" @click="selectTab('entries')">长期记忆</ElButton>
      <ElButton :type="tab === 'jobs' ? 'primary' : 'default'" role="tab" :aria-selected="tab === 'jobs'" @click="selectTab('jobs')">整理任务</ElButton>
    </div>
    <section class="panel">
      <div class="toolbar memory-filters">
        <label>群
          <GroupProfileSelect
            v-model="groupProfileId"
            :api="props.groupOptionApi"
            @change="filter"
            @load-error="onGroupLoadError"
          />
        </label>
        <label>状态<ElSelect v-model="status" clearable placeholder="全部状态" @change="filter">
          <ElOption value="pending" label="待处理" /><ElOption value="accumulating" label="积累中" />
          <ElOption value="promoted" label="已晋升" /><ElOption value="routed_to_knowledge" label="待知识审核" />
          <ElOption value="active" label="有效" /><ElOption value="forgotten" label="已忘记" />
          <ElOption value="retrying" label="重试中" /><ElOption value="deadLetter" label="失败" />
        </ElSelect></label>
      </div>
      <ElAlert v-if="groupOptionError" :title="groupOptionError" type="warning" :closable="false" />
      <ElSkeleton v-if="loading" :rows="5" animated />
      <ElAlert v-else-if="error && !currentItems.length" :title="error" type="error" :closable="false"><ElButton @click="load">重试</ElButton></ElAlert>
      <ElEmpty v-else-if="!currentItems.length" :description="tab === 'candidates' ? '暂无待整理记忆。' : tab === 'entries' ? '暂无长期记忆。' : '暂无整理任务。'" />
      <ElTable v-else-if="tab === 'candidates'" :data="candidates" row-key="id">
        <ElTableColumn prop="content" label="候选内容" min-width="260" show-overflow-tooltip />
        <ElTableColumn label="作用域" width="150"><template #default="{ row }">{{ row.scopeType }}<span v-if="row.subjectDisplayName"> · {{ row.subjectDisplayName }}</span></template></ElTableColumn>
        <ElTableColumn prop="memoryType" label="类型" width="150" />
        <ElTableColumn label="证据" width="180"><template #default="{ row }">{{ row.observationCount }} 次 / {{ row.distinctSessionCount }} 会话 / {{ row.distinctDayCount }} 天</template></ElTableColumn>
        <ElTableColumn label="可信度" width="100"><template #default="{ row }">{{ Math.round(row.confidence * 100) }}%</template></ElTableColumn>
        <ElTableColumn label="状态" width="120"><template #default="{ row }"><ElTag effect="plain">{{ row.status }}</ElTag></template></ElTableColumn>
        <ElTableColumn label="操作" min-width="220"><template #default="{ row }">
          <ElButton size="small" @click="edit(row)">编辑</ElButton>
          <ElButton size="small" type="primary" :disabled="row.memoryType === 'BusinessFact' || !['pending','accumulating'].includes(row.status)" @click="promote(row)">晋升</ElButton>
          <ElButton size="small" type="danger" plain :disabled="!['pending','accumulating'].includes(row.status)" @click="reject(row)">拒绝</ElButton>
          <ElButton size="small" @click="reorganize(row)">重新整理</ElButton>
        </template></ElTableColumn>
      </ElTable>
      <ElTable v-else-if="tab === 'entries'" :data="entries" row-key="id">
        <ElTableColumn prop="content" label="记忆内容" min-width="280" show-overflow-tooltip />
        <ElTableColumn prop="scopeType" label="作用域" width="110" />
        <ElTableColumn prop="memoryType" label="类型" width="150" />
        <ElTableColumn label="状态" width="110"><template #default="{ row }"><ElTag effect="plain">{{ row.status }}</ElTag></template></ElTableColumn>
        <ElTableColumn label="召回" width="150"><template #default="{ row }">{{ row.recallCount }} 次<span v-if="row.lastRecalledAtUtc"> · {{ formatBeijingTime(row.lastRecalledAtUtc) }}</span></template></ElTableColumn>
        <ElTableColumn label="有效期" width="170"><template #default="{ row }">{{ row.expiresAtUtc ? formatBeijingTime(row.expiresAtUtc) : '长期有效' }}</template></ElTableColumn>
        <ElTableColumn label="操作" width="130"><template #default="{ row }">
          <ElButton v-if="row.status === 'active'" size="small" type="danger" plain @click="forget(row)">忘记</ElButton>
          <ElButton v-else-if="['forgotten','expired'].includes(row.status)" size="small" @click="restore(row)">恢复</ElButton>
        </template></ElTableColumn>
      </ElTable>
      <ElTable v-else :data="jobs" row-key="id">
        <ElTableColumn prop="jobType" label="任务" min-width="220" />
        <ElTableColumn label="状态" width="120"><template #default="{ row }"><ElTag effect="plain">{{ row.status }}</ElTag></template></ElTableColumn>
        <ElTableColumn prop="attemptCount" label="次数" width="90" />
        <ElTableColumn label="更新时间" width="180"><template #default="{ row }">{{ formatBeijingTime(row.updatedAtUtc) }}</template></ElTableColumn>
        <ElTableColumn label="操作" width="110"><template #default="{ row }"><ElButton size="small" :disabled="!['retrying','deadLetter'].includes(row.status)" @click="retry(row)">重试</ElButton></template></ElTableColumn>
      </ElTable>
      <div class="pagination"><span>共 {{ total }} 条</span><ElPagination :current-page="page" :page-size="pageSize" :total="total" layout="prev, pager, next" @current-change="changePage" /></div>
      <ElAlert v-if="error && currentItems.length" :title="error" type="warning" :closable="false" />
    </section>
  </section>
</template>

<style scoped>
.memory-center { max-width: 1440px; margin: 0 auto; }
.tab-strip { display: flex; gap: .5rem; margin-bottom: 1rem; }
.memory-filters { display: grid; grid-template-columns: minmax(240px, 1fr) minmax(180px, 280px); gap: 1rem; }
.memory-filters label { display: grid; gap: .35rem; }
@media (max-width: 720px) { .memory-filters { grid-template-columns: 1fr; } }
</style>
