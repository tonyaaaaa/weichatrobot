<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { ElAlert, ElButton, ElSkeleton, ElTag } from 'element-plus';
import {
  dashboardApi,
  type DashboardApi,
  type DashboardSummary,
  type ReadinessComponent
} from '../api/dashboard';
import { formatBeijingTime } from '../utils/beijingTime';

const props = withDefaults(defineProps<{ api?: DashboardApi }>(), {
  api: () => dashboardApi
});

const loading = ref(true);
const error = ref('');
const data = ref<DashboardSummary>();

const readinessLabel = computed(() => {
  switch (data.value?.readiness.status) {
    case 'healthy': return '正常';
    case 'degraded': return '降级';
    case 'failed': return '异常';
    default: return '未检查';
  }
});

const readinessType = computed(() => {
  switch (data.value?.readiness.status) {
    case 'healthy': return 'success';
    case 'degraded': return 'warning';
    case 'failed': return 'danger';
    default: return 'info';
  }
});

const durableJobStates = computed(() =>
  Object.entries(data.value?.operations.durableJobs ?? {})
    .sort(([left], [right]) => left.localeCompare(right)));
const sendCommandStates = computed(() =>
  Object.entries(data.value?.operations.sendCommands ?? {})
    .sort(([left], [right]) => left.localeCompare(right)));

async function load(): Promise<void> {
  loading.value = true;
  error.value = '';
  try {
    data.value = await props.api.getSummary();
  } catch {
    error.value = '工作台数据加载失败，请检查后端服务后重试。';
  } finally {
    loading.value = false;
  }
}

function componentType(component: ReadinessComponent): 'success' | 'danger' {
  return component.status === 'healthy' ? 'success' : 'danger';
}

onMounted(load);
</script>

<template>
  <section class="dashboard-page" aria-labelledby="dashboard-title">
    <header class="page-header">
      <div>
        <p class="eyebrow">运营总览</p>
        <h1 id="dashboard-title">工作台</h1>
        <p>集中查看机器人、知识库、任务队列和基础组件运行状态。</p>
      </div>
      <div class="header-actions">
        <span v-if="data" class="checked-at">
          检查时间：{{ formatBeijingTime(data.checkedAtUtc) }}
        </span>
        <ElButton :loading="loading" @click="load">刷新</ElButton>
      </div>
    </header>

    <ElSkeleton
      v-if="loading && !data"
      :rows="10"
      animated
      aria-label="正在加载工作台"
    />

    <ElAlert
      v-if="error"
      :title="error"
      type="error"
      :closable="false"
      show-icon
    >
      <template #default>
        <ElButton data-testid="retry-dashboard" @click="load">重新加载</ElButton>
      </template>
    </ElAlert>

    <template v-if="data">
      <ElAlert
        v-if="data.robots.failedChecks > 0 || data.readiness.status !== 'healthy'"
        title="部分检查失败"
        type="warning"
        :closable="false"
        show-icon
      >
        数据库统计仍然有效；外部机器人或基础组件状态可能暂时不可用。
      </ElAlert>

      <section class="panel" aria-labelledby="robot-summary-title">
        <div class="section-heading">
          <div>
            <h2 id="robot-summary-title">机器人</h2>
            <p>可达、在线和回调配置是独立状态。</p>
          </div>
          <ElTag v-if="data.robots.failedChecks" type="warning">
            {{ data.robots.failedChecks }} 个检查失败
          </ElTag>
        </div>
        <div class="metric-grid">
          <article class="metric-card">
            <span>机器人总数</span>
            <strong data-testid="robot-total">{{ data.robots.total }}</strong>
          </article>
          <article class="metric-card">
            <span>已启用</span>
            <strong>{{ data.robots.enabled }}</strong>
          </article>
          <article class="metric-card">
            <span>WorkTool 可达</span>
            <strong>{{ data.robots.reachable }}</strong>
          </article>
          <article class="metric-card">
            <span>当前在线</span>
            <strong>{{ data.robots.online }}</strong>
          </article>
          <article class="metric-card">
            <span>消息回调已配置</span>
            <strong>{{ data.robots.messageCallbackConfigured }}</strong>
          </article>
          <article class="metric-card">
            <span>结果回调已配置</span>
            <strong>{{ data.robots.commandResultCallbackConfigured }}</strong>
          </article>
        </div>
      </section>

      <section class="panel" aria-labelledby="knowledge-summary-title">
        <div class="section-heading">
          <div>
            <h2 id="knowledge-summary-title">知识库</h2>
            <p>文档、版本、待审核内容和失败任务的实时数据库统计。</p>
          </div>
        </div>
        <div class="metric-grid metric-grid-four">
          <article class="metric-card">
            <span>有效文档</span>
            <strong data-testid="knowledge-documents">{{ data.knowledge.documents }}</strong>
          </article>
          <article class="metric-card">
            <span>文档版本</span>
            <strong data-testid="knowledge-versions">{{ data.knowledge.versions }}</strong>
          </article>
          <article class="metric-card">
            <span>待审核候选</span>
            <strong data-testid="pending-candidates">{{ data.knowledge.pendingCandidates }}</strong>
          </article>
          <article class="metric-card" :class="{ danger: data.knowledge.failedTasks > 0 }">
            <span>失败任务</span>
            <strong>{{ data.knowledge.failedTasks }}</strong>
          </article>
        </div>
      </section>

      <div class="split-grid">
        <section class="panel" aria-labelledby="operations-title">
          <div class="section-heading">
            <div>
              <h2 id="operations-title">队列与死信</h2>
              <p>按后端实际状态分组，不合并未知状态。</p>
            </div>
            <div class="dead-letter-count">
              <span>死信</span>
              <strong data-testid="dead-letters">{{ data.operations.deadLetters }}</strong>
            </div>
          </div>
          <div class="queue-grid">
            <div>
              <h3>Durable Job</h3>
              <p v-if="durableJobStates.length === 0" class="empty-copy">暂无任务</p>
              <dl v-else>
                <div v-for="[state, count] in durableJobStates" :key="state">
                  <dt>{{ state }}</dt>
                  <dd>{{ count }}</dd>
                </div>
              </dl>
            </div>
            <div>
              <h3>发送命令</h3>
              <p v-if="sendCommandStates.length === 0" class="empty-copy">暂无命令</p>
              <dl v-else>
                <div v-for="[state, count] in sendCommandStates" :key="state">
                  <dt>{{ state }}</dt>
                  <dd>{{ count }}</dd>
                </div>
              </dl>
            </div>
          </div>
        </section>

        <section class="panel" aria-labelledby="readiness-title">
          <div class="section-heading">
            <div>
              <h2 id="readiness-title">基础组件</h2>
              <p>必需组件异常会导致 readiness 失败。</p>
            </div>
            <ElTag
              data-testid="readiness-status"
              :type="readinessType"
              effect="dark"
            >{{ readinessLabel }}</ElTag>
          </div>
          <ul class="component-list">
            <li v-for="component in data.readiness.components" :key="component.name">
              <div>
                <strong>{{ component.name }}</strong>
                <span>{{ component.required ? '必需' : '可选' }}</span>
              </div>
              <div class="component-status">
                <span v-if="component.detail">{{ component.detail }}</span>
                <ElTag :type="componentType(component)">
                  {{ component.status === 'healthy' ? '正常' : '失败' }}
                </ElTag>
              </div>
            </li>
          </ul>
        </section>
      </div>
    </template>
  </section>
</template>

<style scoped>
.dashboard-page {
  display: grid;
  width: 100%;
  max-width: 1440px;
  margin: 0 auto;
  gap: var(--space-xl);
}

.page-header,
.section-heading,
.header-actions,
.component-list li,
.component-status {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-lg);
}

.page-header {
  align-items: flex-start;
}

.page-header p,
.section-heading p {
  margin-bottom: 0;
  color: var(--color-muted-text);
}

.header-actions {
  flex-wrap: wrap;
  justify-content: flex-end;
}

.checked-at,
.empty-copy,
.component-list span {
  color: var(--color-muted-text);
  font-size: .875rem;
}

.panel {
  display: grid;
  min-width: 0;
  gap: var(--space-lg);
  padding: var(--space-xl);
  border: 1px solid var(--color-border);
  border-radius: .75rem;
  background: var(--color-surface);
  box-shadow: var(--shadow-sm);
}

.section-heading h2,
.section-heading p,
.queue-grid h3 {
  margin-top: 0;
}

.metric-grid {
  display: grid;
  grid-template-columns: repeat(6, minmax(0, 1fr));
  gap: var(--space-md);
}

.metric-grid-four {
  grid-template-columns: repeat(4, minmax(0, 1fr));
}

.metric-card {
  display: grid;
  min-height: 7rem;
  align-content: space-between;
  gap: var(--space-md);
  padding: var(--space-lg);
  border: 1px solid var(--color-border);
  border-radius: .65rem;
  background: var(--color-background);
}

.metric-card span,
.dead-letter-count span {
  color: var(--color-muted-text);
  font-size: .875rem;
}

.metric-card strong,
.dead-letter-count strong {
  color: var(--color-heading);
  font-size: 2rem;
}

.metric-card.danger strong,
.dead-letter-count strong {
  color: var(--color-danger, #c2410c);
}

.split-grid,
.queue-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--space-xl);
}

.dead-letter-count {
  display: grid;
  justify-items: end;
}

.queue-grid > div {
  min-width: 0;
  padding: var(--space-lg);
  border-radius: .65rem;
  background: var(--color-background);
}

dl,
.component-list {
  display: grid;
  gap: var(--space-sm);
  margin: 0;
  padding: 0;
}

dl > div {
  display: flex;
  justify-content: space-between;
  gap: var(--space-lg);
  padding-block: var(--space-xs);
  border-bottom: 1px solid var(--color-border);
}

dd {
  margin: 0;
  font-weight: 700;
}

.component-list {
  list-style: none;
}

.component-list li {
  padding-block: var(--space-sm);
  border-bottom: 1px solid var(--color-border);
}

.component-list li > div:first-child {
  display: grid;
  gap: .2rem;
}

@media (max-width: 1180px) {
  .metric-grid {
    grid-template-columns: repeat(3, minmax(0, 1fr));
  }
}

@media (max-width: 800px) {
  .page-header,
  .section-heading,
  .split-grid,
  .queue-grid {
    align-items: stretch;
    grid-template-columns: 1fr;
  }

  .page-header,
  .section-heading {
    flex-direction: column;
  }

  .header-actions {
    justify-content: flex-start;
  }

  .metric-grid,
  .metric-grid-four {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 520px) {
  .metric-grid,
  .metric-grid-four {
    grid-template-columns: 1fr;
  }
}
</style>
