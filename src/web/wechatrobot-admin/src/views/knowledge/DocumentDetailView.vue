<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { ElAlert, ElButton, ElEmpty, ElInput, ElSkeleton, ElTag } from 'element-plus';
import { knowledgeApi, type ChunkPolicy, type IndexStatus, type KnowledgeApi, type PreviewItem, type PreviewSet } from '../../api/knowledge';
import { knowledgeTagApi, type KnowledgeTagApi } from '../../api/knowledgeTags';
import KnowledgeTagSelector from '../../components/knowledge/KnowledgeTagSelector.vue';

const props = withDefaults(defineProps<{
  documentId: string;
  versionId: string;
  api?: KnowledgeApi;
  tagApi?: Pick<KnowledgeTagApi, 'options'>;
}>(), {
  api: () => knowledgeApi,
  tagApi: () => knowledgeTagApi
});
const loading = ref(true);
const busy = ref(false);
const error = ref('');
const notice = ref('');
const revision = ref(0);
const previews = ref<PreviewItem[]>([]);
const drafts = ref<Record<string, string>>({});
const selected = ref<string[]>([]);
const selectedTagIds = ref<string[]>([]);
const tagError = ref('');
const policyKind = ref<ChunkPolicy['kind']>('smart');
const targetTokens = ref(800);
const overlapTokens = ref(120);
const maximumTokens = ref(1000);
const separator = ref('\\n---\\n');
const regexPattern = ref('\\n#{1,3}\\s');
const qaEntriesText = ref('');
const indexStatus = ref<IndexStatus>({
  documentId: props.documentId, documentStatus: 'unknown', approvedChunkCount: 0,
  consistency: 'not-checked', driftDetails: [], jobs: []
});
const canMerge = computed(() => selected.value.length === 2);
const latestFailedJob = computed(() => indexStatus.value.jobs.find(job => job.status === 'failed'));
const isActive = computed(() => indexStatus.value.documentStatus === 'active');

function applySet(value: PreviewSet | PreviewItem[]) {
  if (Array.isArray(value)) previews.value = value;
  else { previews.value = value.items; revision.value = value.revision; }
  drafts.value = Object.fromEntries(previews.value.map(item => [item.id, item.text]));
}
async function load() {
  loading.value = true; error.value = '';
  try {
    const [previewValue, status] = await Promise.all([props.api.getPreviews(props.versionId), props.api.getIndexStatus(props.documentId)]);
    applySet(previewValue); indexStatus.value = status;
  } catch { error.value = '详情加载失败，请检查服务后重试。'; }
  finally { loading.value = false; }
}
async function mutate(action: () => Promise<PreviewSet | PreviewItem[]>, success: string) {
  busy.value = true; error.value = '';
  try { applySet(await action()); notice.value = success; selected.value = []; }
  catch { error.value = '操作失败，数据可能已被其他用户更新，请刷新后重试。'; }
  finally { busy.value = false; }
}
async function edit(item: PreviewItem) {
  await mutate(() => props.api.editPreview(props.versionId, item.id, drafts.value[item.id] ?? item.text, revision.value), '分段已保存。');
}
async function split(item: PreviewItem) {
  const draft = drafts.value[item.id] ?? item.text;
  if (draft.length < 2) { error.value = '分段至少需要两个字符才能拆分。'; return; }
  if (!window.confirm(`确认拆分第 ${item.sequence} 段？拆分前会先保存当前编辑内容。`)) return;
  const entered = window.prompt(`请输入拆分位置（1-${draft.length - 1}）`, String(Math.floor(draft.length / 2)));
  if (entered === null) return;
  const offset = Number(entered);
  if (!Number.isInteger(offset) || offset < 1 || offset >= draft.length) {
    error.value = `拆分位置必须是 1 到 ${draft.length - 1} 之间的整数。`;
    return;
  }
  busy.value = true; error.value = '';
  try {
    if (draft !== item.text) applySet(await props.api.editPreview(props.versionId, item.id, draft, revision.value));
    applySet(await props.api.splitPreview(props.versionId, item.id, offset, revision.value));
    notice.value = '分段已拆分。'; selected.value = [];
  } catch { error.value = '操作失败，数据可能已被其他用户更新，请刷新后重试。'; }
  finally { busy.value = false; }
}
async function merge() {
  if (selected.value.length !== 2) { error.value = '请选择恰好两个相邻分段后再合并。'; return; }
  const chosen = selected.value
    .map(id => previews.value.find(item => item.id === id))
    .filter((item): item is PreviewItem => item !== undefined)
    .sort((left, right) => left.sequence - right.sequence);
  if (chosen.length !== 2 || chosen[1].sequence !== chosen[0].sequence + 1) {
    error.value = '只能合并序号相邻的两个分段，请重新选择。';
    return;
  }
  if (!window.confirm('确认合并所选的两个相邻分段？此操作会替换当前分段结构。')) return;
  await mutate(() => props.api.mergePreviews(props.versionId, chosen[0].id, chosen[1].id, revision.value), '分段已合并。');
}
async function retry() {
  if (!latestFailedJob.value) return;
  const jobId = latestFailedJob.value.id;
  busy.value = true;
  try {
    await props.api.retryIndex(jobId);
    indexStatus.value = await props.api.getIndexStatus(props.documentId);
    notice.value = '索引任务已重新排队。';
  }
  catch { error.value = '重试失败，请稍后再试。'; }
  finally { busy.value = false; }
}
async function generate() {
  if (!window.confirm('确认重新生成分段预览？当前分段结构可能被替换。')) return;
  const policy = buildPolicy();
  if (!policy) return;
  await mutate(() => props.api.generatePreviews(props.versionId, revision.value, policy), '分段预览已重新生成，请审核内容。');
}
function buildPolicy(): ChunkPolicy | undefined {
  const lengths = {
    targetTokens: Number(targetTokens.value),
    overlapTokens: Number(overlapTokens.value),
    maximumTokens: Number(maximumTokens.value)
  };
  if (!Number.isInteger(lengths.targetTokens) || !Number.isInteger(lengths.overlapTokens) ||
      !Number.isInteger(lengths.maximumTokens) || lengths.targetTokens < 1 ||
      lengths.overlapTokens < 0 || lengths.overlapTokens >= lengths.targetTokens ||
      lengths.maximumTokens < lengths.targetTokens) {
    error.value = '分段长度必须为整数，最大长度不小于目标长度，重叠长度小于目标长度。';
    return;
  }
  if (policyKind.value === 'separator') {
    const decoded = separator.value.replaceAll('\\r', '\r').replaceAll('\\n', '\n').replaceAll('\\t', '\t');
    if (!decoded) { error.value = '请输入分隔符。'; return; }
    return { kind: 'separator', ...lengths, separator: decoded };
  }
  if (policyKind.value === 'regex') {
    if (!regexPattern.value.trim()) { error.value = '请输入正则表达式。'; return; }
    return { kind: 'regex', ...lengths, regexPattern: regexPattern.value };
  }
  if (policyKind.value === 'qa') {
    const qaEntries = qaEntriesText.value.split(/\r?\n/).filter(line => line.trim()).map(line => {
      const [question = '', synonyms = '', answer = ''] = line.split('|');
      return {
        question: question.trim(),
        synonyms: synonyms.split(',').map(value => value.trim()).filter(Boolean),
        answer: answer.trim()
      };
    });
    if (!qaEntries.length || qaEntries.some(entry => !entry.question || !entry.answer)) {
      error.value = 'QA 策略每行格式必须为“问题|同义问法1,同义问法2|答案”。';
      return;
    }
    return { kind: 'qa', ...lengths, qaEntries };
  }
  return { kind: 'smart', ...lengths };
}
async function remove(item: PreviewItem) {
  if (!window.confirm(`确认删除第 ${item.sequence} 段预览？删除后需要重新审核分段。`)) return;
  await mutate(
    () => props.api.deletePreview(props.versionId, item.id, revision.value),
    '分段预览已删除。');
}
async function approve() {
  if (!window.confirm('确认批准当前分段？批准后需要建立索引才能用于机器人检索。')) return;
  busy.value = true; error.value = '';
  try { await props.api.approvePreviews(props.versionId, revision.value); notice.value = '分段已批准，可以提交索引。'; }
  catch { error.value = '批准失败，可能存在并发修改，请刷新后重试。'; } finally { busy.value = false; }
}
async function queueIndex() {
  if (selectedTagIds.value.length === 0) {
    tagError.value = '建立索引时至少选择一个已启用的知识标签。';
    return;
  }
  busy.value = true; error.value = '';
  try {
    await props.api.queueIndex(
      props.documentId,
      props.versionId,
      selectedTagIds.value,
      isActive.value
    );
    indexStatus.value = await props.api.getIndexStatus(props.documentId);
    notice.value = '索引任务已排队。';
  } catch { error.value = '索引任务提交失败，请检查所选标签和文档状态。'; } finally { busy.value = false; }
}
onMounted(load);
</script>

<template>
  <section class="ops-page" aria-labelledby="document-detail-title">
    <header class="page-header"><div><p class="eyebrow">知识文档 / 版本</p><h1 id="document-detail-title">分段与索引</h1><p class="mono">文档 {{ documentId }} · 版本 {{ versionId }}</p></div><ElButton @click="load">刷新</ElButton></header>
    <ElSkeleton v-if="loading" :rows="7" animated aria-label="正在加载分段和索引状态" />
    <ElAlert v-else-if="error && !previews.length" :title="error" type="error" :closable="false" show-icon><ElButton @click="load">重试</ElButton></ElAlert>
    <template v-else>
      <section class="panel">
        <div class="section-heading"><div><h2>索引状态</h2><p>文档状态：<ElTag effect="plain">{{ indexStatus.documentStatus }}</ElTag> · 一致性：{{ indexStatus.consistency }}</p><p v-if="latestFailedJob" class="helper">最近失败任务：{{ latestFailedJob.operation }}，已尝试 {{ latestFailedJob.attemptCount }} 次<span v-if="latestFailedJob.failureReason"> · {{ latestFailedJob.failureReason }}</span></p></div>
          <div class="actions"><ElButton v-if="latestFailedJob" data-testid="retry-index" :disabled="busy" @click="retry">重试索引</ElButton><ElButton data-testid="queue-index" type="primary" :loading="busy" @click="queueIndex">{{ isActive ? '重新索引' : '建立索引' }}</ElButton></div>
        </div>
        <label>知识标签（必填）</label>
        <KnowledgeTagSelector
          v-model="selectedTagIds"
          :api="tagApi"
          required
          aria-label="索引知识标签"
          @update:model-value="tagError = ''"
        />
        <p class="helper">可选择一个或多个已启用标签，多个标签按任一匹配（OR）参与检索。</p>
        <p v-if="tagError" id="index-tag-error" data-testid="index-tag-error" class="field-error" role="alert">{{ tagError }}</p>
      </section>
      <section class="panel">
        <div class="policy-grid">
          <label>分段策略
            <select v-model="policyKind" data-testid="chunk-policy-kind">
              <option value="smart">智能分段</option>
              <option value="separator">指定分隔符</option>
              <option value="regex">正则分段</option>
              <option value="qa">问答对</option>
            </select>
          </label>
          <label>目标长度（Token）<input v-model.number="targetTokens" data-testid="chunk-target-tokens" type="number" min="1"></label>
          <label>重叠长度（Token）<input v-model.number="overlapTokens" data-testid="chunk-overlap-tokens" type="number" min="0"></label>
          <label>最大长度（Token）<input v-model.number="maximumTokens" data-testid="chunk-maximum-tokens" type="number" min="1"></label>
          <label v-if="policyKind === 'separator'" class="policy-wide">分隔符（支持 \n、\r、\t）
            <input v-model="separator" data-testid="chunk-separator">
          </label>
          <label v-if="policyKind === 'regex'" class="policy-wide">正则表达式
            <input v-model="regexPattern" data-testid="chunk-regex">
          </label>
          <label v-if="policyKind === 'qa'" class="policy-wide">QA 条目（每行：问题|同义问法1,同义问法2|答案）
            <textarea v-model="qaEntriesText" data-testid="chunk-qa-entries" rows="5" />
          </label>
        </div>
        <div class="section-heading"><div><h2>分段预览</h2><p>修订号 {{ revision }}。合并前请选择两个相邻分段。</p></div>
          <div class="actions"><ElButton data-testid="generate-previews" :disabled="busy" @click="generate">重新生成预览</ElButton><ElButton data-testid="merge-selected" :disabled="busy || !canMerge" @click="merge">合并所选</ElButton><ElButton data-testid="approve-previews" type="primary" :disabled="busy || !previews.length" @click="approve">批准分段</ElButton></div>
        </div>
        <ElEmpty v-if="!previews.length" description="暂无分段。请先通过生成预览接口创建分段。" />
        <ol v-else class="chunk-list">
          <li v-for="item in previews" :key="item.id" class="chunk-card">
            <label class="chunk-select"><input v-model="selected" type="checkbox" :value="item.id" :data-testid="`select-${item.id}`"> 选择第 {{ item.sequence }} 段</label>
            <label :for="`text-${item.id}`">分段内容</label><textarea :id="`text-${item.id}`" v-model="drafts[item.id]" :data-testid="`text-${item.id}`" rows="4" />
            <div class="actions"><ElButton :data-testid="`edit-${item.id}`" :disabled="busy" @click="edit(item)">编辑</ElButton><ElButton :data-testid="`split-${item.id}`" :disabled="busy" @click="split(item)">拆分</ElButton><ElButton :data-testid="`delete-${item.id}`" type="danger" plain :disabled="busy" @click="remove(item)">删除</ElButton></div>
          </li>
        </ol>
      </section>
    </template>
    <ElAlert v-if="error && previews.length" :title="error" type="error" :closable="false" show-icon />
    <p class="sr-live" aria-live="polite">{{ notice }}</p>
  </section>
</template>
