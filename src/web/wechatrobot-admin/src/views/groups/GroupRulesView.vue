<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue';
import { onBeforeRouteLeave } from 'vue-router';
import {
  ElAlert,
  ElButton,
  ElMessage,
  ElSkeleton,
  ElTabPane,
  ElTabs,
  ElTag
} from 'element-plus';
import {
  groupApi,
  type AnswerFallbackSettings,
  type ContextOverrides,
  type EffectiveContext,
  type GroupApi,
  type GroupConfiguration,
  type GroupLifecycleStatus,
  type GroupRule,
  type PatternKind
} from '../../api/groups';
import GroupAdvancedSettingsPanel from '../../components/groups/GroupAdvancedSettingsPanel.vue';
import GroupContextMemoryPanel from '../../components/groups/GroupContextMemoryPanel.vue';
import GroupKnowledgeAnswerPanel from '../../components/groups/GroupKnowledgeAnswerPanel.vue';
import GroupRunRecordsPanel from '../../components/groups/GroupRunRecordsPanel.vue';
import { confirmAction } from '../../utils/dialogs';
import {
  createGroupConfigurationDraft,
  groupConfigurationDraftSignature,
  type GroupConfigurationDraft
} from './groupConfigurationDraft';

const props = withDefaults(
  defineProps<{
    id: string;
    api?: Pick<GroupApi, 'getConfiguration' | 'updateConfiguration' | 'previewRules' | 'clearContext'>;
  }>(),
  { api: () => groupApi }
);

const includeRules = ref<GroupRule[]>([]);
const excludeRules = ref<GroupRule[]>([]);
const boundTagIds = ref<string[]>([]);
const availableTags = ref<GroupConfiguration['availableTags']>([]);
const previewGroupNames = ref('');
const previewResults = ref<{ groupName: string; isMatch: boolean; isExcluded: boolean }[]>([]);
const groupName = ref('');
const identity = reactive({
  robotName: '',
  workToolGroupRemark: null as string | null,
  registrationSource: '',
  state: 'enabled' as GroupLifecycleStatus,
  isEnabled: true,
  stateVersion: 0
});
const configurationVersion = ref(0);
const loading = ref(true);
const saving = ref(false);
const loadError = ref('');
const canRetryLoad = ref(false);
const configurationLoaded = ref(false);
const activeTab = ref('knowledge');
const savedSignature = ref('');
const configured = reactive<ContextOverrides>({});
const effective = ref<EffectiveContext>({
  senderIsolated: false,
  historyTurns: 6,
  idleTimeoutMinutes: 30,
  tokenCap: 3000,
  summaryEnabled: true,
  includeBotHistory: true
});
const answerFallback = reactive<AnswerFallbackSettings>({
  webSearchEnabled: false,
  modelKnowledgeFallbackEnabled: false,
  webSearchShowSources: false,
  webSearchResultCount: 5,
  webSearchRecency: 'NoLimit',
  webSearchDomainFilter: null,
  webSearchContentSize: 'Medium',
  finalNoEvidencePolicy: 'InsufficientEvidence'
});
const defaultChatModel = ref<GroupConfiguration['defaultChatModel']>({
  isConfigured: false,
  configurationName: null,
  connectionStatus: null,
  webSearchMode: 'None',
  canUseWebSearch: false,
  unavailableReason: 'not_configured'
});
const memorySummary = ref<GroupConfiguration['memorySummary']>({
  activeGroupMemoryCount: 0,
  activeMemberMemoryCount: 0,
  pendingCandidateCount: 0,
  pendingOrRunningJobCount: 0
});
const agentRuntime = ref<NonNullable<GroupConfiguration['agentRuntime']>>({
  intentRuntimeMode: 'Legacy',
  answerRuntimeMode: 'Legacy',
  templateRoutingRuntimeMode: 'AgentFramework',
  editable: false
});

function currentDraft(): GroupConfigurationDraft {
  return {
    includeRules: includeRules.value.map(rule => ({ ...rule })),
    excludeRules: excludeRules.value.map(rule => ({ ...rule })),
    boundTagIds: [...boundTagIds.value],
    context: { ...configured },
    answerFallback: { ...answerFallback }
  };
}

const isDirty = computed(() =>
  configurationLoaded.value
  && groupConfigurationDraftSignature(currentDraft()) !== savedSignature.value);
const canSave = computed(() => isDirty.value && !loading.value && !saving.value);

function replaceReactive<T extends object>(target: T, source: Partial<T>): void {
  for (const key of Object.keys(target) as (keyof T)[]) delete target[key];
  Object.assign(target, source);
}

function applyConfiguration(configuration: GroupConfiguration): void {
  groupName.value = configuration.name;
  Object.assign(identity, {
    robotName: configuration.identity?.robotName ?? '未找到机器人配置',
    workToolGroupRemark: configuration.identity?.workToolGroupRemark ?? null,
    registrationSource: configuration.identity?.registrationSource ?? 'Manual',
    state: configuration.identity?.state ?? 'enabled',
    isEnabled: configuration.identity?.isEnabled ?? true,
    stateVersion: configuration.identity?.stateVersion ?? 0
  });
  const draft = createGroupConfigurationDraft({
    ...configuration,
    answerFallback: configuration.answerFallback ?? { ...answerFallback }
  });
  includeRules.value = draft.includeRules;
  excludeRules.value = draft.excludeRules;
  boundTagIds.value = draft.boundTagIds;
  availableTags.value = configuration.availableTags ?? [];
  replaceReactive(configured, draft.context);
  Object.assign(answerFallback, draft.answerFallback);
  defaultChatModel.value = configuration.defaultChatModel ?? {
    isConfigured: false,
    configurationName: null,
    connectionStatus: null,
    webSearchMode: 'None',
    canUseWebSearch: false,
    unavailableReason: 'not_configured'
  };
  memorySummary.value = configuration.memorySummary ?? {
    activeGroupMemoryCount: 0,
    activeMemberMemoryCount: 0,
    pendingCandidateCount: 0,
    pendingOrRunningJobCount: 0
  };
  agentRuntime.value = configuration.agentRuntime ?? {
    intentRuntimeMode: 'Legacy',
    answerRuntimeMode: 'Legacy',
    templateRoutingRuntimeMode: 'AgentFramework',
    editable: false
  };
  effective.value = configuration.context.effective;
  configurationVersion.value = Number.isInteger(configuration.configurationVersion)
    ? configuration.configurationVersion
    : 0;
  savedSignature.value = groupConfigurationDraftSignature(currentDraft());
}

function addRule(direction: 'include' | 'exclude', patternKind: PatternKind): void {
  (direction === 'include' ? includeRules : excludeRules).value.push({
    pattern: '',
    patternKind,
    ignoreCase: true
  });
}

function removeRule(direction: 'include' | 'exclude', index: number): void {
  (direction === 'include' ? includeRules : excludeRules).value.splice(index, 1);
}

async function load(): Promise<void> {
  loading.value = true;
  loadError.value = '';
  canRetryLoad.value = false;
  configurationLoaded.value = false;
  try {
    applyConfiguration(await props.api.getConfiguration(props.id));
    configurationLoaded.value = true;
  } catch (error) {
    const status = (error as { response?: { status?: number } }).response?.status;
    groupName.value = '';
    loadError.value = status === 404 ? '群不存在或已删除。' : '群配置加载失败，请稍后重试。';
    canRetryLoad.value = status !== 404;
  } finally {
    loading.value = false;
  }
}

async function preview(): Promise<void> {
  const groupNames = previewGroupNames.value
    .split('\n')
    .map(name => name.trim())
    .filter(Boolean);
  const result = await props.api.previewRules({
    includeRules: includeRules.value,
    excludeRules: excludeRules.value,
    groupNames
  });
  previewResults.value = result.results;
}

async function save(): Promise<void> {
  if (!canSave.value) return;
  saving.value = true;
  try {
    const draft = currentDraft();
    const saved = await props.api.updateConfiguration(props.id, {
      ...draft,
      clearContext: false,
      expectedConfigurationVersion: configurationVersion.value
    });
    applyConfiguration(saved);
    configurationLoaded.value = true;
    ElMessage.success('群配置已保存');
  } catch (exception) {
    const data = (exception as { response?: { status?: number; data?: { error?: string } } }).response;
    if (data?.status === 409 && data.data?.error === 'group-configuration-conflict') {
      await load();
      ElMessage.warning('群配置已被其他操作员修改，已加载最新版本，请复核后重新保存。');
    } else {
      ElMessage.error('群配置保存失败，请稍后重试。');
    }
  } finally {
    saving.value = false;
  }
}

async function clearContext(): Promise<void> {
  const confirmed = await confirmAction(
    '确认清空本群短期上下文吗？历史消息和审计记录会保留。',
    { title: '清空短期上下文', danger: true }
  );
  if (!confirmed) return;
  try {
    const result = await props.api.clearContext(props.id, configurationVersion.value);
    configurationVersion.value = result.configurationVersion;
    ElMessage.success(`已清空 ${result.clearedSessions} 个本群会话上下文`);
  } catch (exception) {
    const status = (exception as { response?: { status?: number } }).response?.status;
    if (status === 409) {
      await load();
      ElMessage.warning('群配置版本已变化，已加载最新设置。');
    } else {
      ElMessage.error('上下文清空失败，请稍后重试。');
    }
  }
}

function beforeUnload(event: BeforeUnloadEvent): void {
  if (!isDirty.value) return;
  event.preventDefault();
  event.returnValue = '';
}

watch(isDirty, dirty => {
  if (dirty) window.addEventListener('beforeunload', beforeUnload);
  else window.removeEventListener('beforeunload', beforeUnload);
});

onBeforeRouteLeave(async () => {
  if (!isDirty.value) return true;
  return confirmAction(
    '当前群配置尚未保存，离开后修改将丢失。确认离开吗？',
    { title: '放弃未保存修改' }
  );
});

onBeforeUnmount(() => window.removeEventListener('beforeunload', beforeUnload));
watch(() => props.id, load, { immediate: true });

function stateLabel(state: GroupLifecycleStatus): string {
  return { enabled: '已启用', disabled: '已停用', archived: '已归档' }[state];
}

function sourceLabel(source: string): string {
  return source === 'WorkToolImport' ? 'WorkTool 导入' : '手工登记';
}
</script>

<template>
  <section class="group-detail-view" aria-labelledby="group-detail-title">
    <header class="group-detail-header">
      <div>
        <p class="eyebrow">群管理 / 业务配置</p>
        <h1 id="group-detail-title">{{ groupName || '群配置' }}</h1>
        <p>配置这个群使用哪些知识、如何回答，以及短期上下文策略。</p>
      </div>
      <RouterLink class="secondary-action" :to="{ name: 'group-list' }">返回群列表</RouterLink>
    </header>

    <ElSkeleton v-if="loading" :rows="8" animated />
    <section v-else-if="loadError" class="load-error-state" role="alert">
      <ElAlert :title="loadError" type="error" :closable="false" show-icon />
      <ElButton
        v-if="canRetryLoad"
        data-testid="retry-group-configuration"
        :loading="loading"
        @click="load"
      >
        重新加载
      </ElButton>
    </section>

    <template v-if="configurationLoaded">
      <section class="group-identity-card" aria-label="群基本信息">
        <div>
          <span>机器人</span>
          <strong>{{ identity.robotName }}</strong>
        </div>
        <div>
          <span>群备注</span>
          <strong>{{ identity.workToolGroupRemark || '未设置' }}</strong>
        </div>
        <div>
          <span>登记来源</span>
          <ElTag effect="plain">{{ sourceLabel(identity.registrationSource) }}</ElTag>
        </div>
        <div>
          <span>状态</span>
          <ElTag :type="identity.state === 'enabled' ? 'success' : identity.state === 'archived' ? 'info' : 'warning'">
            {{ stateLabel(identity.state) }}
          </ElTag>
        </div>
      </section>

      <ElTabs v-model="activeTab" class="group-detail-tabs">
        <ElTabPane label="知识与回答" name="knowledge">
          <GroupKnowledgeAnswerPanel
            :group-id="id"
            :group-name="groupName"
            :available-tags="availableTags"
            :bound-tag-ids="boundTagIds"
            :answer-fallback="{ ...answerFallback }"
            :default-chat-model="defaultChatModel"
            @update:bound-tag-ids="boundTagIds = $event"
            @update:answer-fallback="Object.assign(answerFallback, $event)"
          />
        </ElTabPane>

        <ElTabPane label="上下文与记忆" name="context">
          <GroupContextMemoryPanel
            :configured="{ ...configured }"
            :effective="effective"
            :memory-summary="memorySummary"
            :group-id="id"
            @update:configured="replaceReactive(configured, $event)"
            @clear-context="clearContext"
          />
        </ElTabPane>

        <ElTabPane label="运行记录" name="records">
          <GroupRunRecordsPanel :group-id="id" :group-name="groupName" />
        </ElTabPane>

        <ElTabPane label="高级设置" name="advanced">
          <GroupAdvancedSettingsPanel
            :registration-source="identity.registrationSource"
            :include-rules="includeRules"
            :exclude-rules="excludeRules"
            :preview-results="previewResults"
            :preview-group-names="previewGroupNames"
            :agent-runtime="agentRuntime"
            @add="addRule"
            @remove="removeRule"
            @update:preview-group-names="previewGroupNames = $event"
            @preview="preview"
          />
        </ElTabPane>
      </ElTabs>

      <footer v-if="isDirty" class="group-save-bar">
        <p>有未保存的群配置修改。</p>
        <ElButton
          type="primary"
          data-testid="save-configuration"
          :loading="saving"
          :disabled="!canSave"
          @click="save"
        >
          保存群配置
        </ElButton>
      </footer>
    </template>
  </section>
</template>

<style scoped>
.group-detail-view {
  display: grid;
  width: 100%;
  max-width: 1280px;
  margin: 0 auto;
  gap: var(--space-xl);
}
.load-error-state {
  display: grid;
  justify-items: start;
  gap: var(--space-md);
}

.group-detail-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--space-lg);
}

.group-detail-header h1,
.detail-panel h2 {
  margin: 0;
}

.group-detail-header p:last-child,
.detail-panel > p {
  color: var(--color-muted-text);
}

.group-identity-card,
.detail-panel {
  min-width: 0;
  padding: var(--space-xl);
  border: 1px solid var(--color-border);
  border-radius: .8rem;
  background: var(--color-surface);
  box-shadow: var(--shadow-sm);
}

.group-identity-card {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: var(--space-lg);
}

.group-identity-card > div {
  display: grid;
  align-content: start;
  gap: .35rem;
}

.group-identity-card span {
  color: var(--color-muted-text);
  font-size: .875rem;
}

.group-detail-tabs {
  min-width: 0;
  padding: 0 var(--space-xl) var(--space-xl);
  border: 1px solid var(--color-border);
  border-radius: .8rem;
  background: var(--color-surface);
}

.tab-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--space-xl);
}

.tag-choice-list,
.link-list,
.advanced-stack {
  display: grid;
  gap: var(--space-md);
}

.tag-choice-list label {
  display: flex;
  align-items: flex-start;
  gap: var(--space-sm);
  white-space: normal;
}

.form-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--space-lg);
  margin: var(--space-lg) 0;
}

.form-grid label,
.full-field {
  display: grid;
  min-width: 0;
  gap: var(--space-sm);
}

.form-grid :deep(.el-select),
.full-field :deep(.el-select) {
  width: 100%;
}

.record-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--space-lg);
}

.record-grid a {
  display: grid;
  gap: .35rem;
  padding: var(--space-lg);
  border: 1px solid var(--color-border);
  border-radius: .65rem;
  text-decoration: none;
}

.record-grid a span {
  color: var(--color-muted-text);
}

.group-save-bar {
  position: sticky;
  z-index: 10;
  bottom: var(--space-lg);
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-lg);
  padding: var(--space-lg);
  border: 1px solid var(--color-primary);
  border-radius: .8rem;
  background: var(--color-surface);
  box-shadow: var(--shadow-lg);
}

.group-save-bar p {
  margin: 0;
}

.empty-state {
  padding: var(--space-lg);
  border-radius: .6rem;
  background: var(--color-muted-surface);
  color: var(--color-muted-text);
}

@media (max-width: 820px) {
  .group-identity-card,
  .tab-grid,
  .record-grid,
  .form-grid {
    grid-template-columns: 1fr;
  }

  .group-detail-header {
    flex-direction: column;
  }

  .group-save-bar {
    bottom: 0;
    align-items: stretch;
    flex-direction: column;
  }
}
</style>
