<script setup lang="ts">
import { computed } from 'vue';
import {
  ElAlert,
  ElCheckbox,
  ElInput,
  ElInputNumber,
  ElOption,
  ElSelect,
  ElSwitch,
  ElTag
} from 'element-plus';
import type { AnswerFallbackSettings, GroupConfiguration } from '../../api/groups';
import GroupFixedRepliesPanel from './GroupFixedRepliesPanel.vue';

const props = defineProps<{
  groupId?: string;
  groupName?: string;
  availableTags: GroupConfiguration['availableTags'];
  boundTagIds: string[];
  answerFallback: AnswerFallbackSettings;
  defaultChatModel: GroupConfiguration['defaultChatModel'];
}>();
const emit = defineEmits<{
  'update:boundTagIds': [value: string[]];
  'update:answerFallback': [value: AnswerFallbackSettings];
}>();

const selectedTags = computed({
  get: () => props.boundTagIds,
  set: value => emit('update:boundTagIds', [...value])
});
const enabledTagOptions = computed(() =>
  props.availableTags.filter(tag => tag.isEnabled && !tag.isGlobalPublic));
const disabledBoundTags = computed(() =>
  props.availableTags.filter(tag => !tag.isEnabled && tag.isBound));
const globalTags = computed(() =>
  props.availableTags.filter(tag => tag.isEnabled && tag.isGlobalPublic));

function updateFallback(patch: Partial<AnswerFallbackSettings>): void {
  emit('update:answerFallback', { ...props.answerFallback, ...patch });
}

function toggleWebSearch(enabled: boolean): void {
  updateFallback({
    webSearchEnabled: enabled,
    modelKnowledgeFallbackEnabled: enabled
      ? true
      : props.answerFallback.modelKnowledgeFallbackEnabled
  });
}

function modelCapabilityCopy(): string {
  const reasons = {
    not_configured: '尚未配置默认聊天模型，运行时无法执行联网搜索。',
    disabled: '默认聊天模型已停用，运行时无法执行联网搜索。',
    connection_not_succeeded: '默认聊天模型尚未通过连接测试，运行时将跳过联网搜索。',
    not_enabled: '当前默认聊天模型尚未启用 Web Search。请在模型配置中选择已验证的联网模式并完成专项测试。',
    unsupported: '当前默认聊天模型不支持 Web Search，运行时将跳过联网搜索并继续执行后续降级。',
    none: '当前默认聊天模型已具备 Web Search 能力。'
  };
  return reasons[props.defaultChatModel.unavailableReason];
}
</script>

<template>
  <div class="answer-flow">
    <GroupFixedRepliesPanel
      v-if="groupId"
      :group-id="groupId"
      :group-name="groupName"
    />
    <section class="flow-step" data-testid="answer-step">
      <div class="step-number">1</div>
      <div class="step-content">
        <header>
          <div>
            <h2>先查知识库</h2>
            <p>选择这个群可检索的业务知识；全局公开知识自动参与检索。</p>
          </div>
          <ElTag type="success" effect="plain">优先使用</ElTag>
        </header>

        <label class="field-label" for="group-knowledge-tags">业务知识标签</label>
        <ElSelect
          id="group-knowledge-tags"
          v-model="selectedTags"
          multiple
          filterable
          clearable
          collapse-tags
          collapse-tags-tooltip
          placeholder="选择业务知识标签"
          class="full-control"
        >
          <ElOption
            v-for="tag in enabledTagOptions"
            :key="tag.id"
            :label="tag.name"
            :value="tag.id"
          />
          <ElOption
            v-for="tag in disabledBoundTags"
            :key="tag.id"
            :label="`${tag.name}（已禁用，移除后不可重新添加）`"
            :value="tag.id"
          />
        </ElSelect>

        <div class="public-tags">
          <span>全局公开：</span>
          <template v-if="globalTags.length">
            <ElTag v-for="tag in globalTags" :key="tag.id" effect="plain">{{ tag.name }}</ElTag>
          </template>
          <span v-else class="muted">暂无全局公开标签</span>
        </div>
      </div>
    </section>

    <section class="flow-step" data-testid="answer-step">
      <div class="step-number">2</div>
      <div class="step-content">
        <header>
          <div>
            <h2>未命中时继续尝试</h2>
            <p>按顺序尝试联网搜索和模型自身知识，每项都可独立关闭。</p>
          </div>
        </header>

        <div class="setting-row">
          <div><strong>模型 Web Search</strong><span>从网页检索最新信息</span></div>
          <ElSwitch
            :model-value="answerFallback.webSearchEnabled"
            data-testid="web-search-enabled"
            @update:model-value="toggleWebSearch(Boolean($event))"
          />
        </div>
        <ElAlert
          :title="defaultChatModel.configurationName ? `默认模型：${defaultChatModel.configurationName}` : '默认模型未配置'"
          :description="modelCapabilityCopy()"
          :type="defaultChatModel.canUseWebSearch ? 'success' : 'warning'"
          :closable="false"
          show-icon
        />

        <div v-if="answerFallback.webSearchEnabled" class="search-options">
          <label>搜索结果数量
            <ElInputNumber
              :model-value="answerFallback.webSearchResultCount"
              :min="1"
              :max="20"
              controls-position="right"
              @update:model-value="updateFallback({ webSearchResultCount: Number($event) })"
            />
          </label>
          <label>时间范围
            <ElSelect
              :model-value="answerFallback.webSearchRecency"
              @update:model-value="updateFallback({ webSearchRecency: $event })"
            >
              <ElOption label="不限" value="NoLimit" />
              <ElOption label="一天内" value="OneDay" />
              <ElOption label="一周内" value="OneWeek" />
              <ElOption label="一月内" value="OneMonth" />
              <ElOption label="一年内" value="OneYear" />
            </ElSelect>
          </label>
          <label>搜索摘要长度
            <ElSelect
              :model-value="answerFallback.webSearchContentSize"
              @update:model-value="updateFallback({ webSearchContentSize: $event })"
            >
              <ElOption label="标准" value="Medium" />
              <ElOption label="详细" value="High" />
            </ElSelect>
          </label>
          <label>限定搜索域名
            <ElInput
              :model-value="answerFallback.webSearchDomainFilter ?? ''"
              placeholder="example.com, news.example.com"
              @update:model-value="updateFallback({ webSearchDomainFilter: String($event) || null })"
            />
          </label>
          <ElCheckbox
            :model-value="answerFallback.webSearchShowSources"
            @update:model-value="updateFallback({ webSearchShowSources: Boolean($event) })"
          >
            在群消息中展示网页来源
          </ElCheckbox>
        </div>

        <div class="setting-row">
          <div><strong>模型自身知识</strong><span>搜索不可用或失败时继续回答</span></div>
          <ElSwitch
            :model-value="answerFallback.modelKnowledgeFallbackEnabled"
            data-testid="model-knowledge-enabled"
            @update:model-value="updateFallback({ modelKnowledgeFallbackEnabled: Boolean($event) })"
          />
        </div>
      </div>
    </section>

    <section class="flow-step" data-testid="answer-step">
      <div class="step-number">3</div>
      <div class="step-content">
        <header><div><h2>仍无可靠答案时</h2><p>确定机器人最后如何回复，避免没有依据地编造答案。</p></div></header>
        <label class="field-label">最终处理策略
          <ElSelect
            :model-value="answerFallback.finalNoEvidencePolicy"
            class="full-control"
            @update:model-value="updateFallback({ finalNoEvidencePolicy: $event })"
          >
            <ElOption label="明确提示没有可靠答案" value="InsufficientEvidence" />
            <ElOption label="请群成员补充问题" value="Clarification" />
          </ElSelect>
        </label>
      </div>
    </section>
  </div>
</template>

<style scoped>
.answer-flow { display: grid; gap: var(--space-lg); }
.flow-step {
  display: grid;
  grid-template-columns: 2.5rem minmax(0, 1fr);
  gap: var(--space-md);
  padding: var(--space-xl);
  border: 1px solid var(--color-border);
  border-radius: .9rem;
  background: var(--color-surface);
}
.step-number {
  display: grid;
  width: 2.25rem;
  height: 2.25rem;
  place-items: center;
  border-radius: 999px;
  color: white;
  background: var(--color-primary);
  font-weight: 700;
}
.step-content { min-width: 0; display: grid; gap: var(--space-lg); }
header { display: flex; align-items: flex-start; justify-content: space-between; gap: var(--space-lg); }
h2, p { margin: 0; }
header p, .setting-row span, .muted { color: var(--color-muted-text); }
.field-label, .search-options label { display: grid; gap: var(--space-sm); font-weight: 600; }
.full-control { width: 100%; }
.public-tags { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-sm); }
.setting-row {
  display: flex;
  min-height: 3.25rem;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-lg);
  padding: var(--space-md);
  border-radius: .75rem;
  background: var(--color-background);
}
.setting-row div { display: grid; gap: .2rem; }
.search-options {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--space-lg);
  padding-left: var(--space-lg);
  border-left: 3px solid var(--color-primary);
}
.search-options .el-checkbox { grid-column: 1 / -1; }
@media (max-width: 720px) {
  .flow-step { grid-template-columns: 1fr; padding: var(--space-lg); }
  .search-options { grid-template-columns: 1fr; padding-left: 0; border-left: 0; }
  header { align-items: flex-start; }
}
</style>
