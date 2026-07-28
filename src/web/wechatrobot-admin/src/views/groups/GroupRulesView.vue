<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue';
import { groupApi, type AnswerFallbackSettings, type ContextOverrides, type EffectiveContext, type GroupApi, type GroupRule, type PatternKind } from '../../api/groups';
import ContextPolicyForm from '../../components/groups/ContextPolicyForm.vue';
import RuleEditor from '../../components/groups/RuleEditor.vue';
import RulePreview from '../../components/groups/RulePreview.vue';

const props = withDefaults(
  defineProps<{
    id: string;
    api?: Pick<GroupApi, 'getConfiguration' | 'updateConfiguration' | 'previewRules'>;
  }>(),
  { api: () => groupApi }
);
const includeRules = ref<GroupRule[]>([]);
const excludeRules = ref<GroupRule[]>([]);
const boundTagIds = ref<string[]>([]);
const availableTags = ref<{ id: string; name: string; isGlobalPublic: boolean; isEnabled: boolean; isBound: boolean }[]>([]);
const previewGroupNames = ref('');
const previewResults = ref<{ groupName: string; isMatch: boolean; isExcluded: boolean }[]>([]);
const notice = ref('');
const saveError = ref('');
const groupName = ref('');
const configurationVersion = ref(0);
const loading = ref(true);
const loadError = ref('');
const configurationLoaded = ref(false);
const configured = reactive<ContextOverrides>({});
const effective = ref<EffectiveContext>({ senderIsolated: false, historyTurns: 6, idleTimeoutMinutes: 30, tokenCap: 3000, summaryEnabled: true, includeBotHistory: true });
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
const canSave = computed(() => configurationLoaded.value && !loading.value);

function addRule(direction: 'include' | 'exclude', patternKind: PatternKind) {
  (direction === 'include' ? includeRules : excludeRules).value.push({ pattern: '', patternKind, ignoreCase: true });
}
function removeRule(direction: 'include' | 'exclude', index: number) { (direction === 'include' ? includeRules : excludeRules).value.splice(index, 1); }
function request(clearContext = false) { return {
  includeRules: includeRules.value,
  excludeRules: excludeRules.value,
  boundTagIds: boundTagIds.value,
  context: { ...configured },
  clearContext,
  answerFallback: { ...answerFallback },
  expectedConfigurationVersion: configurationVersion.value
}; }
function toggleWebSearch(enabled: boolean) {
  answerFallback.webSearchEnabled = enabled;
  if (enabled && !answerFallback.modelKnowledgeFallbackEnabled)
    answerFallback.modelKnowledgeFallbackEnabled = true;
}
async function load() {
  loading.value = true;
  loadError.value = '';
  configurationLoaded.value = false;
  try {
    const configuration = await props.api.getConfiguration(props.id);
    groupName.value = configuration.name;
    includeRules.value = configuration.rules.include;
    excludeRules.value = configuration.rules.exclude;
    boundTagIds.value = configuration.boundTagIds;
    availableTags.value = configuration.availableTags;
    configurationVersion.value = Number.isInteger(configuration.configurationVersion)
      ? configuration.configurationVersion
      : 0;
    Object.assign(configured, configuration.context.configured);
    effective.value = configuration.context.effective;
    if (configuration.answerFallback)
      Object.assign(answerFallback, configuration.answerFallback);
    configurationLoaded.value = true;
  } catch (error) {
    const status = (error as { response?: { status?: number } }).response?.status;
    groupName.value = '';
    loadError.value = status === 404 ? '群不存在或已删除。' : '群配置加载失败，请稍后重试。';
  } finally {
    loading.value = false;
  }
}
async function preview() {
  const groupNames = previewGroupNames.value.split('\n').map(name => name.trim()).filter(Boolean);
  const result = await props.api.previewRules({ includeRules: includeRules.value, excludeRules: excludeRules.value, groupNames });
  previewResults.value = result.results;
}
async function save(clearContext = false) {
  if (!canSave.value) return;
  saveError.value = '';
  try {
    const saved = await props.api.updateConfiguration(props.id, request(clearContext));
    Object.assign(configured, saved.context.configured);
    effective.value = saved.context.effective;
    if (Number.isInteger(saved.configurationVersion))
      configurationVersion.value = saved.configurationVersion;
    notice.value = clearContext ? `已清空 ${saved.clearedContextSessions} 个本群会话上下文，历史和审计记录已保留。` : '群配置已保存。';
  } catch (exception) {
    const data = (exception as { response?: { status?: number; data?: { error?: string } } }).response;
    if (data?.status === 409 && data.data?.error === 'group-configuration-conflict') {
      await load();
      saveError.value = '群配置已被其他操作员修改，已加载最新版本，请复核后重新保存。';
      return;
    }
    saveError.value = '群配置保存失败，请稍后重试。';
  }
}
watch(() => props.id, load, { immediate: true });
</script>

<template>
  <section class="group-rules-view" aria-labelledby="group-rules-title">
    <header class="group-page-header">
      <div>
        <p class="eyebrow">群配置</p>
        <h1 id="group-rules-title">{{ groupName || '群配置' }}</h1>
        <p>维护群匹配、知识库标签和上下文策略。全局公开标签始终可用。</p>
      </div>
      <RouterLink class="group-operations-link" :to="{ name: 'group-list' }">返回群列表</RouterLink>
    </header>

    <p v-if="loading" class="group-panel" aria-live="polite">正在加载群配置…</p>
    <section v-else-if="loadError" class="group-panel group-load-error" aria-live="assertive">
      <p>{{ loadError }}</p>
      <RouterLink :to="{ name: 'group-list' }">返回群列表</RouterLink>
    </section>

    <template v-if="configurationLoaded">
      <div class="group-panel group-identity-bar">
        <RouterLink :to="{ name: 'group-list' }">返回群列表</RouterLink>
        <strong>{{ groupName }}</strong>
      </div>

      <div class="group-layout">
      <div class="group-primary-column">
        <RuleEditor :include-rules="includeRules" :exclude-rules="excludeRules" @add="addRule" @remove="removeRule" />
        <section class="group-panel preview-panel" aria-labelledby="preview-title">
          <div class="panel-heading">
            <div>
              <h2 id="preview-title">保存前预览</h2>
              <p>每行输入一个已知群名称，检查当前规则是否会匹配或排除。</p>
            </div>
          </div>
          <div class="preview-editor">
            <label for="preview-group-names">已知群名称</label>
            <textarea id="preview-group-names" v-model="previewGroupNames" placeholder="例如：技术支持群&#10;售后服务群" />
            <button type="button" data-testid="preview-rules" @click="preview">预览匹配结果</button>
          </div>
          <RulePreview :results="previewResults" />
        </section>
      </div>

      <aside class="group-secondary-column">
        <section class="group-panel tag-panel" aria-labelledby="tag-panel-title">
          <h2 id="tag-panel-title">知识库标签</h2>
          <p>多选标签按 OR 关系检索；“全局公开”标签无需绑定。</p>
          <div v-if="availableTags.length" class="tag-choice-list">
            <label v-for="tag in availableTags" :key="tag.id" :class="{ 'stale-tag': !tag.isEnabled }">
              <input v-model="boundTagIds" type="checkbox" :data-testid="`tag-${tag.id}`" :value="tag.id" :disabled="!tag.isEnabled && !tag.isBound">
              <span>{{ tag.name }}{{ !tag.isEnabled ? '（已禁用，移除后不可重新添加）' : tag.isGlobalPublic ? '（全局公开）' : '' }}</span>
            </label>
          </div>
          <p v-else class="empty-tags">当前没有可绑定的知识库标签。</p>
        </section>
        <ContextPolicyForm :configured="configured" :effective="effective" @clear="save(true)" />
        <section class="group-panel fallback-panel" aria-labelledby="fallback-title">
          <h2 id="fallback-title">知识库未命中时</h2>
          <p>按顺序尝试联网搜索、模型自身知识，最后执行无证据策略。知识库命中时不会调用这些降级能力。</p>
          <label class="switch-row">
            <input
              :checked="answerFallback.webSearchEnabled"
              data-testid="web-search-enabled"
              type="checkbox"
              @change="toggleWebSearch(($event.target as HTMLInputElement).checked)"
            >
            <span><strong>允许模型 Web Search</strong><small>仅默认对话模型明确支持 Z.AI Web Search 时有效。</small></span>
          </label>
          <label class="switch-row">
            <input v-model="answerFallback.modelKnowledgeFallbackEnabled" data-testid="model-knowledge-enabled" type="checkbox">
            <span><strong>允许模型自身知识回答</strong><small>搜索不可用或失败时继续回答，并在审计中标记来源。</small></span>
          </label>
          <template v-if="answerFallback.webSearchEnabled">
            <label class="switch-row">
              <input v-model="answerFallback.webSearchShowSources" type="checkbox">
              <span><strong>在群消息中显示网页来源</strong><small>最多追加 3 条经过净化的链接。</small></span>
            </label>
            <div class="fallback-grid">
              <label>结果数量
                <input v-model.number="answerFallback.webSearchResultCount" type="number" min="1" max="20">
              </label>
              <label>时间范围
                <select v-model="answerFallback.webSearchRecency">
                  <option value="NoLimit">不限</option>
                  <option value="OneDay">一天内</option>
                  <option value="OneWeek">一周内</option>
                  <option value="OneMonth">一月内</option>
                  <option value="OneYear">一年内</option>
                </select>
              </label>
              <label>摘要长度
                <select v-model="answerFallback.webSearchContentSize">
                  <option value="Medium">标准</option>
                  <option value="High">详细</option>
                </select>
              </label>
              <label>域名白名单（可选）
                <input v-model="answerFallback.webSearchDomainFilter" placeholder="example.com,news.example.com">
              </label>
            </div>
          </template>
          <label>最终无证据策略
            <select v-model="answerFallback.finalNoEvidencePolicy">
              <option value="InsufficientEvidence">明确提示没有可靠答案</option>
              <option value="Clarification">请用户补充问题</option>
            </select>
          </label>
        </section>
      </aside>
      </div>

      <footer class="group-panel group-save-bar">
        <p class="group-save-hint">保存后，新规则与策略将用于该群后续消息。</p>
        <p v-if="saveError" class="group-save-error" role="alert">{{ saveError }}</p>
        <p class="group-save-notice" aria-live="polite">{{ notice }}</p>
        <button class="primary-action" type="button" data-testid="save-configuration" :disabled="!canSave" @click="() => save()">保存群配置</button>
      </footer>
    </template>
  </section>
</template>

<style scoped>
.group-rules-view {
  display: grid;
  width: 100%;
  max-width: 1440px;
  margin: 0 auto;
  gap: var(--space-xl);
}
.group-page-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--space-xl);
}
.group-page-header p { margin-bottom: 0; color: var(--color-muted-text); }
.group-operations-link {
  display: inline-flex;
  min-height: 44px;
  align-items: center;
  flex: 0 0 auto;
  padding: .55rem .85rem;
  border: 1px solid var(--color-border);
  border-radius: .5rem;
  color: var(--color-accent-strong);
  background: var(--color-surface);
  font-weight: 600;
  text-decoration: none;
}
.group-panel {
  min-width: 0;
  padding: var(--space-xl);
  border: 1px solid var(--color-border);
  border-radius: .75rem;
  background: var(--color-surface);
  box-shadow: var(--shadow-sm);
}
.group-identity-bar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: var(--space-md);
}
.group-load-error { color: var(--color-danger); }
.group-load-error p { margin-top: 0; }
.group-layout {
  display: grid;
  grid-template-columns: minmax(0, 1.55fr) minmax(18rem, 1fr);
  align-items: start;
  gap: var(--space-xl);
}
.group-primary-column,
.group-secondary-column {
  display: grid;
  min-width: 0;
  gap: var(--space-xl);
}
.panel-heading p,
.tag-panel > p {
  margin-bottom: var(--space-lg);
  color: var(--color-muted-text);
}
.preview-editor {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  align-items: end;
  gap: var(--space-sm) var(--space-md);
}
.preview-editor label {
  grid-column: 1 / -1;
  margin: 0;
}
.preview-editor textarea { min-height: 6rem; }
.preview-editor button { margin-bottom: 0; }
.tag-choice-list {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(100%, 13rem), 1fr));
  gap: var(--space-sm);
}
.tag-choice-list label {
  display: flex;
  align-items: flex-start;
  min-height: 44px;
  gap: var(--space-sm);
  margin: 0;
  padding: var(--space-md);
  border: 1px solid var(--color-border);
  border-radius: .5rem;
  background: var(--color-background);
  cursor: pointer;
}
.tag-choice-list input {
  width: 1.25rem;
  min-height: 1.25rem;
  margin: .125rem 0 0;
  flex: 0 0 auto;
}
.tag-choice-list span { overflow-wrap: anywhere; }
.tag-choice-list .stale-tag { color: var(--color-muted-text); }
.empty-tags {
  margin-bottom: 0 !important;
  padding: var(--space-lg);
  border-radius: .5rem;
  background: var(--color-background);
}
.fallback-panel { display: grid; gap: var(--space-md); }
.fallback-panel > p { margin: 0; color: var(--color-muted-text); }
.switch-row {
  display: flex;
  align-items: flex-start;
  gap: var(--space-sm);
  margin: 0;
}
.switch-row input { width: 1.25rem; min-height: 1.25rem; margin-top: .15rem; }
.switch-row span { display: grid; gap: var(--space-xs); }
.switch-row small { color: var(--color-muted-text); }
.fallback-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--space-md); }
.fallback-grid label,
.fallback-panel > label:not(.switch-row) { display: grid; gap: var(--space-xs); }
.group-save-bar {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  align-items: center;
  gap: var(--space-sm) var(--space-xl);
}
.group-save-hint,
.group-save-notice { margin: 0; }
.group-save-hint { color: var(--color-muted-text); }
.group-save-notice {
  grid-column: 1;
  color: var(--color-success);
  font-weight: 600;
}
.group-save-notice:empty { display: none; }
.group-save-bar button {
  grid-column: 2;
  grid-row: 1 / span 2;
  min-width: 9rem;
}
@media (max-width: 900px) {
  .group-layout { grid-template-columns: 1fr; }
}
@media (max-width: 700px) {
  .group-page-header { flex-direction: column; }
  .group-identity-bar { align-items: flex-start; flex-direction: column; }
}
@media (max-width: 600px) {
  .group-panel { padding: var(--space-lg); }
  .preview-editor,
  .group-save-bar { grid-template-columns: 1fr; }
  .preview-editor label,
  .group-save-notice,
  .group-save-bar button {
    grid-column: 1;
  }
  .group-save-bar button { grid-row: auto; width: 100%; }
  .fallback-grid { grid-template-columns: 1fr; }
}
</style>
