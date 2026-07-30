<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue';
import {
  ElAlert,
  ElButton,
  ElEmpty,
  ElOption,
  ElPagination,
  ElSelect,
  ElSkeleton,
  ElTable,
  ElTableColumn,
  ElTag
} from 'element-plus';
import {
  agentDiagnosticsApi,
  type AgentDiagnosticsApi,
  type AgentDiagnosticsItem,
  type AgentDiagnosticsPage,
  type AgentRuntimeStatus,
  type IntentCategory,
  type IntentDecision,
  type IntentRuntimeMode
} from '../../api/agentDiagnostics';
import {
  groupOptionApi,
  type GroupOptionApi
} from '../../api/groupOptions';
import GroupProfileSelect from '../../components/groups/GroupProfileSelect.vue';
import { formatBeijingTime } from '../../utils/beijingTime';

const props = withDefaults(defineProps<{
  api?: AgentDiagnosticsApi;
  groupApi?: GroupOptionApi;
}>(), {
  api: () => agentDiagnosticsApi,
  groupApi: () => groupOptionApi
});

const runtime = ref<AgentRuntimeStatus | null>(null);
const result = ref<AgentDiagnosticsPage>({
  items: [],
  total: 0,
  page: 1,
  pageSize: 20
});
const loading = ref(true);
const error = ref('');
const filters = reactive<{
  groupId: string;
  runtimeMode: '' | IntentRuntimeMode;
  decision: '' | IntentDecision;
}>({
  groupId: '',
  runtimeMode: '',
  decision: ''
});

async function load(requestedPage = result.value.page): Promise<void> {
  loading.value = true;
  error.value = '';
  try {
    const [runtimeResult, pageResult] = await Promise.all([
      props.api.runtime(),
      props.api.list({
        groupId: filters.groupId || undefined,
        runtimeMode: filters.runtimeMode || undefined,
        decision: filters.decision || undefined,
        page: requestedPage,
        pageSize: result.value.pageSize
      })
    ]);
    runtime.value = runtimeResult;
    result.value = pageResult;
  } catch {
    error.value = 'Agent 诊断加载失败，请检查 API 和数据库迁移后重试。';
  } finally {
    loading.value = false;
  }
}

function runtimeLabel(value: string): string {
  return ({
    Legacy: '现有回答',
    Shadow: '影子判断',
    AgentFramework: 'Agent 正式执行',
    Paused: '已暂停',
    Disabled: '未启用'
  } as Record<string, string>)[value] ?? value;
}

function decisionLabel(value: IntentDecision): string {
  return ({
    Reply: '建议回复',
    NoReply: '建议不回复',
    Uncertain: '无法确定'
  } as Record<IntentDecision, string>)[value];
}

function categoryLabel(value: IntentCategory): string {
  return ({
    DirectedToBot: '向机器人提问',
    FollowUpToBot: '追问机器人',
    HumanConversation: '成员对话',
    SocialChatter: '社交消息',
    Uncertain: '无法确定'
  } as Record<IntentCategory, string>)[value];
}

function decisionType(value: IntentDecision): 'success' | 'danger' | 'warning' {
  return value === 'Reply' ? 'success' : value === 'NoReply' ? 'danger' : 'warning';
}

function itemRow(row: unknown): AgentDiagnosticsItem {
  return row as AgentDiagnosticsItem;
}

onMounted(() => load(1));
</script>

<template>
  <section class="page-shell">
    <header class="page-heading">
      <div>
        <p class="eyebrow">Microsoft Agent Framework / 可观测性</p>
        <h1>智能回复诊断</h1>
        <p>查看意图 Agent 的稳定判断元数据。这里不展示提示词、模型原始响应或凭据。</p>
      </div>
      <ElButton @click="load()">刷新</ElButton>
    </header>

    <ElAlert
      v-if="error"
      type="error"
      :closable="false"
      show-icon
      :title="error"
    >
      <ElButton
        data-testid="reload-agent-diagnostics"
        @click="load(1)"
      >重试</ElButton>
    </ElAlert>

    <ElSkeleton v-if="loading && !runtime" :rows="6" animated />

    <template v-else>
      <section v-if="runtime" class="runtime-grid" aria-label="Agent 运行状态">
        <article>
          <span>群消息意图</span>
          <strong>{{ runtimeLabel(runtime.intentRuntimeMode) }}</strong>
        </article>
        <article>
          <span>知识回答</span>
          <strong>{{ runtimeLabel(runtime.answerRuntimeMode) }}</strong>
        </article>
        <article>
          <span>固定回复模板</span>
          <strong>{{ runtimeLabel(runtime.templateRoutingRuntimeMode) }}</strong>
        </article>
        <article>
          <span>私聊机器人</span>
          <strong>{{ runtimeLabel(runtime.privateChatRuntimeMode) }}</strong>
        </article>
      </section>

      <section class="filter-card">
        <label>
          <span>群名称</span>
          <GroupProfileSelect
            v-model="filters.groupId"
            :api="groupApi"
            @change="load(1)"
            @load-error="error = '群名称选项加载失败，仍可查看全部诊断。'"
          />
        </label>
        <label>
          <span>运行模式</span>
          <ElSelect v-model="filters.runtimeMode" clearable @change="load(1)">
            <ElOption value="Legacy" label="现有逻辑" />
            <ElOption value="Shadow" label="影子判断" />
            <ElOption value="AgentFramework" label="Agent 正式执行" />
            <ElOption value="Paused" label="已暂停" />
          </ElSelect>
        </label>
        <label>
          <span>判断结果</span>
          <ElSelect v-model="filters.decision" clearable @change="load(1)">
            <ElOption value="Reply" label="建议回复" />
            <ElOption value="NoReply" label="建议不回复" />
            <ElOption value="Uncertain" label="无法确定" />
          </ElSelect>
        </label>
      </section>

      <ElEmpty
        v-if="!error && result.items.length === 0"
        description="暂无意图诊断记录。Intent 处于现有逻辑模式时不会产生影子记录。"
      />
      <section v-else-if="result.items.length" class="table-card">
        <ElTable :data="result.items" row-key="id">
          <ElTableColumn label="群 / 成员" min-width="190">
            <template #default="{ row }">
              <strong>{{ itemRow(row).groupName }}</strong>
              <small>{{ itemRow(row).senderDisplayName }}</small>
            </template>
          </ElTableColumn>
          <ElTableColumn label="判断" min-width="150">
            <template #default="{ row }">
              <ElTag :type="decisionType(itemRow(row).decision)">
                {{ decisionLabel(itemRow(row).decision) }}
              </ElTag>
              <small>{{ categoryLabel(itemRow(row).category) }}</small>
            </template>
          </ElTableColumn>
          <ElTableColumn label="依据" min-width="210">
            <template #default="{ row }">
              <code>{{ itemRow(row).reasonCode }}</code>
              <small v-if="itemRow(row).failureCode">
                {{ itemRow(row).failureCode }}
              </small>
            </template>
          </ElTableColumn>
          <ElTableColumn label="运行" min-width="160">
            <template #default="{ row }">
              <span>{{ runtimeLabel(itemRow(row).runtimeMode) }}</span>
              <small>
                可信度 {{ Math.round(itemRow(row).confidence * 100) }}%
                · {{ itemRow(row).latencyMilliseconds }} ms
              </small>
            </template>
          </ElTableColumn>
          <ElTableColumn label="正式会话" min-width="110">
            <template #default="{ row }">
              {{ itemRow(row).formalConversationIncluded ? '已进入' : '未进入' }}
            </template>
          </ElTableColumn>
          <ElTableColumn label="判断时间" min-width="170">
            <template #default="{ row }">
              {{ formatBeijingTime(itemRow(row).decidedAtUtc) }}
            </template>
          </ElTableColumn>
        </ElTable>
        <ElPagination
          v-model:current-page="result.page"
          :page-size="result.pageSize"
          :total="result.total"
          layout="total, prev, pager, next"
          @current-change="load"
        />
      </section>
    </template>
  </section>
</template>

<style scoped>
.page-shell { display: grid; gap: 1rem; min-width: 0; }
.page-heading { display: flex; justify-content: space-between; gap: 1rem; align-items: start; }
.page-heading h1 { margin: .15rem 0; }
.page-heading p { margin: 0; color: var(--color-secondary); }
.eyebrow { color: var(--color-accent) !important; font-weight: 700; }
.runtime-grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: .75rem; }
.runtime-grid article, .filter-card, .table-card { border: 1px solid var(--color-border); border-radius: .75rem; background: var(--color-surface); }
.runtime-grid article { display: grid; gap: .4rem; padding: 1rem; }
.runtime-grid span, small { color: var(--color-secondary); }
.filter-card { display: grid; grid-template-columns: minmax(16rem, 2fr) repeat(2, minmax(10rem, 1fr)); gap: .75rem; padding: 1rem; }
label { display: grid; gap: .4rem; font-weight: 600; }
.table-card { overflow: hidden; padding: .75rem; }
.table-card strong, .table-card small { display: block; }
.el-pagination { justify-content: flex-end; margin-top: .75rem; }
code { overflow-wrap: anywhere; }
@media (max-width: 900px) {
  .runtime-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .filter-card { grid-template-columns: 1fr; }
}
@media (max-width: 560px) {
  .runtime-grid { grid-template-columns: 1fr; }
  .page-heading { display: grid; }
}
</style>
