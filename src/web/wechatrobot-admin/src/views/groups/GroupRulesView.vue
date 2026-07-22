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
const availableTags = ref<{ id: string; name: string; isGlobalPublic: boolean }[]>([]);
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
  Object.assign(configured, saved.context.configured); effective.value = saved.context.effective; notice.value = clearContext ? `已清空 ${saved.clearedContextMessages} 条本群上下文。` : '群配置已保存。';
}
onMounted(load);
</script>

<template>
  <section class="group-rules-view">
    <h1>群管理</h1><p>仅管理员可维护。标签检索遵循“任一已绑定标签匹配即可检索”，全局公开标签始终可用。</p>
    <label>群配置 ID <input v-model.trim="activeGroupId" aria-label="群配置 ID"><button type="button" @click="load">读取配置</button></label>
    <RuleEditor :include-rules="includeRules" :exclude-rules="excludeRules" @add="addRule" @remove="removeRule" />
    <section><h2>知识库标签</h2><p>多选标签按 OR 关系检索；“全局公开”标签无需绑定。</p><label v-for="tag in availableTags" :key="tag.id"><input v-model="boundTagIds" type="checkbox" :value="tag.id">{{ tag.name }}{{ tag.isGlobalPublic ? '（全局公开）' : '' }}</label></section>
    <ContextPolicyForm :configured="configured" :effective="effective" @clear="save(true)" />
    <section><h2>预览群名称</h2><textarea v-model="previewGroupNames" placeholder="每行一个已知群名称" /><button type="button" data-testid="preview-rules" @click="preview">保存前预览</button></section>
    <RulePreview :results="previewResults" />
    <button type="button" :disabled="!canSave" @click="() => save()">保存群配置</button><p aria-live="polite">{{ notice }}</p>
  </section>
</template>
