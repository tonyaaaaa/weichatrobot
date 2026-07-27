<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue';
import { ElAlert, ElButton, ElEmpty, ElPagination, ElSkeleton, ElTag } from 'element-plus';
import {
  administrationAuditApi,
  type AdministrationAuditApi,
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

function utc(value: string): string | undefined {
  if (!value) return undefined;
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? undefined : parsed.toISOString();
}

onMounted(load);
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
      <label>操作人<input v-model="filters.actor" data-testid="administration-audit-actor"></label>
      <label>动作<input v-model="filters.action" placeholder="例如 user_created"></label>
      <label>目标类型<input v-model="filters.targetType" placeholder="例如 ApplicationUser"></label>
      <label>目标 ID<input v-model="filters.targetId"></label>
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
