<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue';
import { ElAlert, ElButton, ElEmpty, ElOption, ElPagination, ElSelect, ElSkeleton, ElTag } from 'element-plus';
import {
  administrationAuditApi,
  type AdministrationAuditApi,
  type AdministrationAuditFilterOptions,
  type AdministrationAuditPage
} from '../../api/administrationAudit';
import { formatBeijingTime } from '../../utils/beijingTime';
import { safeEvidence, safeEvidenceText } from '../../utils/evidenceRedaction';

const props = withDefaults(defineProps<{ api?: AdministrationAuditApi }>(), {
  api: () => administrationAuditApi
});
const filters = reactive({
  actor: '',
  action: '',
  targetType: '',
  targetId: '',
  fromLocal: '',
  toLocal: ''
});
const page = ref<AdministrationAuditPage>({
  items: [],
  total: 0,
  page: 1,
  pageSize: 20
});
const loading = ref(true);
const error = ref('');
const options = ref<AdministrationAuditFilterOptions>({
  actors: [],
  actions: [],
  targetTypes: [],
  targets: []
});

async function load(requestedPage = page.value.page): Promise<void> {
  loading.value = true;
  error.value = '';
  try {
    page.value = await props.api.list({
      actor: filters.actor || undefined,
      action: filters.action || undefined,
      targetType: filters.targetType || undefined,
      targetId: filters.targetId || undefined,
      fromUtc: utc(filters.fromLocal),
      toUtc: utc(filters.toLocal),
      page: requestedPage,
      pageSize: page.value.pageSize
    });
  } catch {
    error.value = '管理审计查询失败，请检查筛选条件和管理员权限。';
  } finally {
    loading.value = false;
  }
}

async function applyFilters(): Promise<void> {
  await load(1);
}

async function loadFilterOptions(targetType = filters.targetType, q = ''): Promise<void> {
  try {
    options.value = await props.api.filterOptions(targetType || undefined, q || undefined);
  } catch {
    error.value = '管理审计筛选选项加载失败，请刷新页面重试。';
  }
}

async function changeTargetType(): Promise<void> {
  filters.targetId = '';
  await loadFilterOptions();
}

async function searchTargets(query: string): Promise<void> {
  if (filters.targetType) await loadFilterOptions(filters.targetType, query);
}

function utc(value: string): string | undefined {
  if (!value) return undefined;
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? undefined : parsed.toISOString();
}

onMounted(() => Promise.all([load(), loadFilterOptions()]));
</script>

<template>
  <section class="ops-page" aria-labelledby="administration-audit-title">
    <header class="page-header">
      <div>
        <p class="eyebrow">运营与安全</p>
        <h1 id="administration-audit-title">管理审计</h1>
        <p>统一查看模型、机器人、知识库、用户等管理操作；详情经过服务端和前端双重脱敏。</p>
      </div>
      <ElButton :loading="loading" @click="() => load()">刷新</ElButton>
    </header>

    <section class="panel filter-grid">
      <label>操作人
        <ElSelect v-model="filters.actor" data-testid="administration-audit-actor" filterable clearable placeholder="全部操作人">
          <ElOption v-for="actor in options.actors" :key="actor" :value="actor" :label="actor" />
        </ElSelect>
      </label>
      <label>动作
        <ElSelect v-model="filters.action" data-testid="administration-audit-action" filterable clearable placeholder="全部动作">
          <ElOption v-for="action in options.actions" :key="action" :value="action" :label="action" />
        </ElSelect>
      </label>
      <label>目标类型
        <ElSelect v-model="filters.targetType" data-testid="administration-audit-target-type" filterable clearable placeholder="全部类型" @change="changeTargetType">
          <ElOption v-for="targetType in options.targetTypes" :key="targetType" :value="targetType" :label="targetType" />
        </ElSelect>
      </label>
      <label>目标
        <ElSelect
          v-model="filters.targetId"
          data-testid="administration-audit-target"
          filterable
          remote
          clearable
          :disabled="!filters.targetType"
          :remote-method="searchTargets"
          placeholder="先选择目标类型"
        >
          <ElOption v-for="target in options.targets" :key="`${target.targetType}:${target.targetId}`" :value="target.targetId" :label="target.label" />
        </ElSelect>
      </label>
      <label>开始时间<input v-model="filters.fromLocal" type="datetime-local"></label>
      <label>结束时间<input v-model="filters.toLocal" type="datetime-local"></label>
      <ElButton data-testid="apply-administration-audit-filters" type="primary" @click="applyFilters">查询</ElButton>
      <p>UTC 边界为开始时间包含、结束时间不包含。</p>
    </section>

    <ElSkeleton v-if="loading" :rows="6" animated />
    <ElAlert v-else-if="error" :title="error" type="error" :closable="false" show-icon />
    <section v-else class="panel">
      <ElEmpty v-if="page.items.length === 0" description="暂无符合条件的管理审计。" />
      <div v-else class="audit-list">
        <article v-for="item in page.items" :key="item.id" class="audit-card">
          <header>
            <div><ElTag effect="plain">{{ safeEvidenceText(item.action) }}</ElTag> <strong>{{ safeEvidenceText(item.actor) }}</strong></div>
            <time>{{ formatBeijingTime(item.createdAtUtc) }}</time>
          </header>
          <p>{{ safeEvidenceText(item.targetType) }} · <span class="mono">{{ safeEvidenceText(item.targetId) }}</span></p>
          <pre>{{ safeEvidence(item.detail) }}</pre>
        </article>
      </div>
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
.ops-page { display: grid; width: 100%; max-width: 1440px; margin: 0 auto; gap: var(--space-xl); }
.page-header, .audit-card > header { display: flex; align-items: flex-start; justify-content: space-between; gap: var(--space-lg); }
.page-header p { color: var(--color-muted-text); }
.panel { padding: var(--space-xl); border: 1px solid var(--color-border); border-radius: .75rem; background: var(--color-surface); box-shadow: var(--shadow-sm); }
.filter-grid { display: grid; grid-template-columns: repeat(4, minmax(10rem, 1fr)); align-items: end; gap: var(--space-md); }
.filter-grid label { display: grid; gap: var(--space-xs); }
.filter-grid input { min-height: 44px; }
.filter-grid p { grid-column: 1 / -1; margin: 0; color: var(--color-muted-text); }
.audit-list { display: grid; gap: var(--space-md); }
.audit-card { padding: var(--space-lg); border: 1px solid var(--color-border); border-radius: .6rem; background: var(--color-background); }
.audit-card pre { overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere; }
@media (max-width: 900px) { .filter-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@media (max-width: 600px) { .filter-grid { grid-template-columns: 1fr; } .page-header, .audit-card > header { flex-direction: column; } }
</style>
