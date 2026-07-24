<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import { useRoute } from 'vue-router';
import { groupApi, type ContextOverrides, type EffectiveContext, type GroupApi, type GroupRule, type PatternKind } from '../../api/groups';
import ContextPolicyForm from '../../components/groups/ContextPolicyForm.vue';
import RuleEditor from '../../components/groups/RuleEditor.vue';
import RulePreview from '../../components/groups/RulePreview.vue';

const props = withDefaults(defineProps<{ groupId?: string; api?: GroupApi }>(), { groupId: '', api: () => groupApi });
const route = useRoute();
const activeGroupId = ref(props.groupId || String(route.params.id ?? ''));
const includeRules = ref<GroupRule[]>([]);
const excludeRules = ref<GroupRule[]>([]);
const boundTagIds = ref<string[]>([]);
const availableTags = ref<{ id: string; name: string; isGlobalPublic: boolean; isEnabled: boolean; isBound: boolean }[]>([]);
const previewGroupNames = ref('');
const previewResults = ref<{ groupName: string; isMatch: boolean; isExcluded: boolean }[]>([]);
const notice = ref('');
const configured = reactive<ContextOverrides>({});
const effective = ref<EffectiveContext>({ senderIsolated: false, historyTurns: 6, idleTimeoutMinutes: 30, tokenCap: 3000, summaryEnabled: true, includeBotHistory: true });
const canSave = computed(() => activeGroupId.value.length > 0);

function addRule(direction: 'include' | 'exclude', patternKind: PatternKind) {
  (direction === 'include' ? includeRules : excludeRules).value.push({ pattern: '', patternKind, ignoreCase: true });
}
function removeRule(direction: 'include' | 'exclude', index: number) { (direction === 'include' ? includeRules : excludeRules).value.splice(index, 1); }
function request(clearContext = false) { return { includeRules: includeRules.value, excludeRules: excludeRules.value, boundTagIds: boundTagIds.value, context: { ...configured }, clearContext }; }
async function load() {
  if (!activeGroupId.value) return;
  const configuration = await props.api.getConfiguration(activeGroupId.value);
  includeRules.value = configuration.rules.include; excludeRules.value = configuration.rules.exclude; boundTagIds.value = configuration.boundTagIds;
  availableTags.value = configuration.availableTags; Object.assign(configured, configuration.context.configured); effective.value = configuration.context.effective;
}
async function preview() {
  const groupNames = previewGroupNames.value.split('\n').map(name => name.trim()).filter(Boolean);
  const result = await props.api.previewRules({ includeRules: includeRules.value, excludeRules: excludeRules.value, groupNames });
  previewResults.value = result.results;
}
async function save(clearContext = false) {
  if (!canSave.value) { notice.value = '请先输入群配置 ID。'; return; }
  const saved = await props.api.updateConfiguration(activeGroupId.value, request(clearContext));
  Object.assign(configured, saved.context.configured); effective.value = saved.context.effective; notice.value = clearContext ? `已清空 ${saved.clearedContextSessions} 个本群会话上下文，历史和审计记录已保留。` : '群配置已保存。';
}
onMounted(load);
</script>

<template>
  <section class="group-rules-view" aria-labelledby="group-rules-title">
    <header class="group-page-header">
      <div>
        <p class="eyebrow">群配置</p>
        <h1 id="group-rules-title">群管理</h1>
        <p>维护群匹配、知识库标签和上下文策略。全局公开标签始终可用。</p>
      </div>
      <RouterLink class="group-operations-link" :to="{ name: 'group-operations' }">新建群、成员和群信息操作</RouterLink>
    </header>

    <div class="group-panel group-identity-bar">
      <label for="group-config-id">群配置 ID</label>
      <input id="group-config-id" v-model.trim="activeGroupId" aria-label="群配置 ID" placeholder="输入群配置 ID">
      <button type="button" @click="load">读取配置</button>
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
      </aside>
    </div>

    <footer class="group-panel group-save-bar">
      <p class="group-save-hint">保存后，新规则与策略将用于该群后续消息。</p>
      <p class="group-save-notice" aria-live="polite">{{ notice }}</p>
      <button class="primary-action" type="button" data-testid="save-configuration" :disabled="!canSave" @click="() => save()">保存群配置</button>
    </footer>
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
  display: grid;
  grid-template-columns: auto minmax(16rem, 1fr) auto;
  align-items: center;
  gap: var(--space-md);
}
.group-identity-bar label { margin: 0; white-space: nowrap; }
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
  .group-identity-bar { grid-template-columns: 1fr auto; }
  .group-identity-bar label { grid-column: 1 / -1; }
}
@media (max-width: 600px) {
  .group-panel { padding: var(--space-lg); }
  .group-identity-bar,
  .preview-editor,
  .group-save-bar { grid-template-columns: 1fr; }
  .group-identity-bar label,
  .preview-editor label,
  .group-save-notice,
  .group-save-bar button {
    grid-column: 1;
  }
  .group-save-bar button { grid-row: auto; width: 100%; }
}
</style>
