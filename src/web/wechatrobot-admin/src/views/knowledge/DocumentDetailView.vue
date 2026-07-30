<script setup lang="ts">
import { computed, getCurrentInstance, onMounted, ref } from 'vue';
import type { Pinia } from 'pinia';
import {
  ElAlert,
  ElButton,
  ElDropdown,
  ElDropdownItem,
  ElDropdownMenu,
  ElEmpty,
  ElSkeleton,
  ElTabPane,
  ElTabs,
  ElTag
} from 'element-plus';
import {
  knowledgeApi,
  type ChunkPolicy,
  type IndexStatus,
  type KnowledgeApi,
  type KnowledgeDocumentVersionSummary,
  type KnowledgeDocumentWorkbench,
  type PreviewItem,
  type PreviewSet
} from '../../api/knowledge';
import { knowledgeTagApi, type KnowledgeTagApi } from '../../api/knowledgeTags';
import KnowledgeTagSelector from '../../components/knowledge/KnowledgeTagSelector.vue';
import { useAuthStore } from '../../stores/auth';
import {
  confirmAction as defaultConfirmAction,
  promptAction as defaultPromptAction
} from '../../utils/dialogs';

const props = withDefaults(defineProps<{
  documentId: string;
  versionId: string;
  api?: KnowledgeApi;
  tagApi?: Pick<KnowledgeTagApi, 'options'>;
  confirmAction?: (message: string) => boolean | Promise<boolean>;
  promptAction?: (
    message: string,
    options?: {
      inputValue?: string;
      inputPattern?: RegExp;
      inputErrorMessage?: string;
    }
  ) => string | null | Promise<string | null>;
  navigate?: (path: string) => void;
}>(), {
  api: () => knowledgeApi,
  tagApi: () => knowledgeTagApi,
  confirmAction: defaultConfirmAction,
  promptAction: defaultPromptAction,
  navigate: (path: string) => window.location.assign(path)
});

const pinia = getCurrentInstance()?.appContext.config.globalProperties
  .$pinia as Pinia | undefined;
const auth = pinia ? useAuthStore(pinia) : undefined;
const loading = ref(true);
const busy = ref(false);
const error = ref('');
const notice = ref('');
const activeTab = ref('content');
const workbench = ref<KnowledgeDocumentWorkbench>();
const versions = ref<KnowledgeDocumentVersionSummary[]>([]);
const revision = ref(0);
const previews = ref<PreviewItem[]>([]);
const drafts = ref<Record<string, string>>({});
const selected = ref<string[]>([]);
const selectedTagIds = ref<string[]>([]);
const initialTagIds = ref<string[]>([]);
const tagError = ref('');
const policyKind = ref<ChunkPolicy['kind']>('smart');
const targetTokens = ref(800);
const overlapTokens = ref(120);
const maximumTokens = ref(1000);
const separator = ref('\\n---\\n');
const regexPattern = ref('\\n#{1,3}\\s');
const qaEntriesText = ref('');
const indexStatus = ref<IndexStatus>({
  documentId: props.documentId,
  documentStatus: 'unknown',
  approvedChunkCount: 0,
  consistency: 'not-checked',
  driftDetails: [],
  jobs: []
});

const currentVersion = computed(() => workbench.value?.version);
const versionStatus = computed(() => currentVersion.value?.status ?? 'unknown');
const isAutomaticSource = computed(() =>
  currentVersion.value?.sourceKind === 'ConversationReview' ||
  currentVersion.value?.sourceKind === 'PrivateChatDirect');
const isAdministrationRevision = computed(() =>
  currentVersion.value?.sourceKind === 'AdministrationRevision');
const isLegacySource = computed(() =>
  currentVersion.value?.sourceKind === 'LegacyUnknown');
const canMutatePreviews = computed(() =>
  versionStatus.value === 'preview' &&
  (isAdministrationRevision.value ||
    currentVersion.value?.sourceKind === 'DocumentUpload' ||
    isLegacySource.value));
const canGenerateFromSource = computed(() =>
  currentVersion.value?.sourceKind === 'DocumentUpload' &&
  (versionStatus.value === 'uploaded' || versionStatus.value === 'preview'));
const canRequestPhysicalDelete = computed(() =>
  auth?.user?.roles.includes('Admin') === true);
const sourceText = computed(() => ({
  DocumentUpload: '文档上传',
  ConversationReview: '消息审核入库',
  PrivateChatDirect: '私聊直接入库',
  AdministrationRevision: '管理员修订',
  LegacyUnknown: '历史数据'
} as Record<string, string>)[currentVersion.value?.sourceKind ?? 'LegacyUnknown']
  ?? '其他来源');
const selectedPreviews = computed(() => selected.value
  .map(id => previews.value.find(item => item.id === id))
  .filter((item): item is PreviewItem => item !== undefined)
  .sort((left, right) => left.sequence - right.sequence));
const canMerge = computed(() =>
  canMutatePreviews.value &&
  selectedPreviews.value.length >= 2 &&
  selectedPreviews.value.every((item, index, items) =>
    index === 0 || item.sequence === items[index - 1].sequence + 1));
const latestFailedJob = computed(() =>
  indexStatus.value.jobs.find(job => job.status === 'failed'));
const hasRunningIndexJob = computed(() =>
  indexStatus.value.jobs.some(job =>
    ['pending', 'retrying', 'leased', 'dispatching', 'activating']
      .includes(job.status)));
const tagsChanged = computed(() =>
  sortedIds(selectedTagIds.value) !== sortedIds(initialTagIds.value));
const isCurrentActiveVersion = computed(() =>
  workbench.value?.activeVersionId === props.versionId ||
  indexStatus.value.activeVersionId === props.versionId);
const indexActionLabel = computed(() => {
  if (!isCurrentActiveVersion.value) return '建立索引';
  return tagsChanged.value ? '保存标签并重新索引' : '重新索引当前版本';
});
const canCreateRevision = computed(() =>
  workbench.value?.canCreateRevision === true);
const editableRevision = computed(() => workbench.value?.editableRevision);
const sourceUnavailableTitle = computed(() => {
  if (isAutomaticSource.value) return '历史来源证据不完整';
  if (isAdministrationRevision.value) return '当前版本是管理员修订';
  return '当前来源没有原始消息';
});
const sourceUnavailableDescription = computed(() => {
  if (isAutomaticSource.value) {
    return '当前版本没有可确认关联的原始消息，系统不会通过昵称或文件名推断来源。';
  }
  if (isAdministrationRevision.value) {
    return '修订版本保留被修订版本关系，请从版本历史查看原始来源；系统不会伪造消息证据。';
  }
  return '文档上传等来源没有消息回调证据，可在已入库内容中查看正文。';
});

function sortedIds(ids: string[]): string {
  return [...ids].sort().join(',');
}

function applySet(value: PreviewSet | PreviewItem[]): void {
  if (Array.isArray(value)) {
    previews.value = value;
  } else {
    previews.value = value.items;
    revision.value = value.revision;
  }
  drafts.value = Object.fromEntries(
    previews.value.map(item => [item.id, item.text]));
}

function applyApprovedChunks(value: KnowledgeDocumentWorkbench): void {
  previews.value = value.chunks.map(item => ({
    id: item.id,
    sequence: item.sequence,
    text: item.text,
    pageNumber: item.pageNumber ?? undefined,
    status: item.status
  }));
  drafts.value = Object.fromEntries(
    previews.value.map(item => [item.id, item.text]));
}

async function load(): Promise<void> {
  loading.value = true;
  error.value = '';
  try {
    const [workbenchValue, status, versionItems] = await Promise.all([
      props.api.getWorkbench(props.documentId, props.versionId),
      props.api.getIndexStatus(props.documentId),
      props.api.getDocumentVersions(props.documentId)
    ]);
    workbench.value = workbenchValue;
    indexStatus.value = status;
    versions.value = [...versionItems].sort((left, right) =>
      right.version - left.version);
    selectedTagIds.value = workbenchValue.version.tags.map(tag => tag.id);
    initialTagIds.value = [...selectedTagIds.value];
    tagError.value = selectedTagIds.value.length
      ? ''
      : '建立索引时至少选择一个已启用的知识标签。';
    revision.value = 0;
    selected.value = [];

    const needsPreview = (
      workbenchValue.version.sourceKind === 'AdministrationRevision' &&
      workbenchValue.version.status === 'preview'
    ) || (
      workbenchValue.version.sourceKind === 'DocumentUpload' &&
      ['uploaded', 'preview'].includes(workbenchValue.version.status)
    ) || (
      workbenchValue.version.sourceKind === 'LegacyUnknown' &&
      ['uploaded', 'preview'].includes(workbenchValue.version.status)
    );
    if (needsPreview) {
      applySet(await props.api.getPreviews(props.versionId));
    } else {
      applyApprovedChunks(workbenchValue);
    }
  } catch {
    error.value = '详情加载失败，请检查服务后重试。';
  } finally {
    loading.value = false;
  }
}

async function mutate(
  action: () => Promise<PreviewSet | PreviewItem[]>,
  success: string
): Promise<void> {
  busy.value = true;
  error.value = '';
  try {
    applySet(await action());
    notice.value = success;
    selected.value = [];
  } catch {
    error.value = '操作失败，数据可能已被其他用户更新，请刷新后重试。';
  } finally {
    busy.value = false;
  }
}

async function edit(item: PreviewItem): Promise<void> {
  await mutate(
    () => props.api.editPreview(
      props.versionId,
      item.id,
      drafts.value[item.id] ?? item.text,
      revision.value),
    '分段已保存。');
}

async function split(item: PreviewItem): Promise<void> {
  const draft = drafts.value[item.id] ?? item.text;
  if (draft.length < 2) {
    error.value = '分段至少需要两个字符才能拆分。';
    return;
  }
  if (!await props.confirmAction(
    `确认拆分第 ${item.sequence + 1} 段？拆分前会先保存当前编辑内容。`
  )) return;
  const entered = await props.promptAction(
    `请输入拆分位置（1-${draft.length - 1}）`,
    {
      inputValue: String(Math.floor(draft.length / 2)),
      inputPattern: new RegExp('^(?:[1-9]\\d*)$'),
      inputErrorMessage: `拆分位置必须是 1 到 ${draft.length - 1} 之间的整数。`
    });
  if (entered === null) return;
  const offset = Number(entered);
  if (!Number.isInteger(offset) || offset < 1 || offset >= draft.length) {
    error.value = `拆分位置必须是 1 到 ${draft.length - 1} 之间的整数。`;
    return;
  }
  busy.value = true;
  error.value = '';
  try {
    if (draft !== item.text) {
      applySet(await props.api.editPreview(
        props.versionId,
        item.id,
        draft,
        revision.value));
    }
    applySet(await props.api.splitPreview(
      props.versionId,
      item.id,
      offset,
      revision.value));
    notice.value = '分段已拆分。';
    selected.value = [];
  } catch {
    error.value = '操作失败，数据可能已被其他用户更新，请刷新后重试。';
  } finally {
    busy.value = false;
  }
}

async function merge(): Promise<void> {
  if (!canMerge.value) {
    error.value = '合并前请选择两个或更多连续分段。';
    return;
  }
  if (!await props.confirmAction(
    `确认合并所选的 ${selectedPreviews.value.length} 个连续分段？此操作会替换当前分段结构。`
  )) return;
  await mutate(
    () => props.api.mergePreviews(
      props.versionId,
      selectedPreviews.value.map(item => item.id),
      revision.value),
    '分段已合并。');
}

async function retry(): Promise<void> {
  if (!latestFailedJob.value) return;
  busy.value = true;
  error.value = '';
  try {
    await props.api.retryIndex(latestFailedJob.value.id);
    indexStatus.value = await props.api.getIndexStatus(props.documentId);
    notice.value = '索引任务已重新排队。';
  } catch {
    error.value = '重试失败，请稍后再试。';
  } finally {
    busy.value = false;
  }
}

async function generate(): Promise<void> {
  if (!await props.confirmAction(
    '确认重新生成分段预览？当前分段结构可能被替换。'
  )) return;
  const policy = buildPolicy();
  if (!policy) return;
  await mutate(
    () => props.api.generatePreviews(
      props.versionId,
      revision.value,
      policy),
    '分段预览已重新生成，请审核内容。');
}

function buildPolicy(): ChunkPolicy | undefined {
  const lengths = {
    targetTokens: Number(targetTokens.value),
    overlapTokens: Number(overlapTokens.value),
    maximumTokens: Number(maximumTokens.value)
  };
  if (!Number.isInteger(lengths.targetTokens) ||
      !Number.isInteger(lengths.overlapTokens) ||
      !Number.isInteger(lengths.maximumTokens) ||
      lengths.targetTokens < 1 ||
      lengths.overlapTokens < 0 ||
      lengths.overlapTokens >= lengths.targetTokens ||
      lengths.maximumTokens < lengths.targetTokens) {
    error.value =
      '分段长度必须为整数，最大长度不小于目标长度，重叠长度小于目标长度。';
    return;
  }
  if (policyKind.value === 'separator') {
    const decoded = separator.value
      .replaceAll('\\r', '\r')
      .replaceAll('\\n', '\n')
      .replaceAll('\\t', '\t');
    if (!decoded) {
      error.value = '请输入分隔符。';
      return;
    }
    return { kind: 'separator', ...lengths, separator: decoded };
  }
  if (policyKind.value === 'regex') {
    if (!regexPattern.value.trim()) {
      error.value = '请输入正则表达式。';
      return;
    }
    return { kind: 'regex', ...lengths, regexPattern: regexPattern.value };
  }
  if (policyKind.value === 'qa') {
    const qaEntries = qaEntriesText.value
      .split(/\r?\n/)
      .filter(line => line.trim())
      .map(line => {
        const [question = '', synonyms = '', answer = ''] = line.split('|');
        return {
          question: question.trim(),
          synonyms: synonyms.split(',').map(value => value.trim()).filter(Boolean),
          answer: answer.trim()
        };
      });
    if (!qaEntries.length ||
        qaEntries.some(entry => !entry.question || !entry.answer)) {
      error.value =
        'QA 策略每行格式必须为“问题|同义问法1,同义问法2|答案”。';
      return;
    }
    return { kind: 'qa', ...lengths, qaEntries };
  }
  return { kind: 'smart', ...lengths };
}

async function remove(item: PreviewItem): Promise<void> {
  if (!await props.confirmAction(
    `确认删除第 ${item.sequence + 1} 段预览？删除后需要重新审核分段。`
  )) return;
  await mutate(
    () => props.api.deletePreview(
      props.versionId,
      item.id,
      revision.value),
    '分段预览已删除。');
}

async function approve(): Promise<void> {
  if (!await props.confirmAction(
    '确认批准当前分段？批准后需要建立索引才能用于机器人检索。'
  )) return;
  busy.value = true;
  error.value = '';
  try {
    await props.api.approvePreviews(props.versionId, revision.value);
    notice.value = '分段已批准，可以提交索引。';
    await load();
  } catch {
    error.value = '批准失败，可能存在并发修改，请刷新后重试。';
  } finally {
    busy.value = false;
  }
}

async function queueIndex(): Promise<void> {
  if (selectedTagIds.value.length === 0) {
    tagError.value = '建立索引时至少选择一个已启用的知识标签。';
    return;
  }
  if (hasRunningIndexJob.value) return;
  busy.value = true;
  error.value = '';
  try {
    await props.api.queueIndex(
      props.documentId,
      props.versionId,
      selectedTagIds.value,
      isCurrentActiveVersion.value);
    initialTagIds.value = [...selectedTagIds.value];
    indexStatus.value = await props.api.getIndexStatus(props.documentId);
    notice.value = '索引任务已排队。';
  } catch {
    error.value = '索引任务提交失败，请检查所选标签和文档状态。';
  } finally {
    busy.value = false;
  }
}

function revisionPath(versionId: string): string {
  return `/knowledge/documents/${encodeURIComponent(props.documentId)}` +
    `/versions/${encodeURIComponent(versionId)}`;
}

async function createRevision(): Promise<void> {
  if (!workbench.value || !canCreateRevision.value) return;
  if (!await props.confirmAction(
    '将从当前已批准内容创建新的可编辑修订版本，当前生效版本会继续提供检索。确认继续？'
  )) return;
  busy.value = true;
  error.value = '';
  try {
    const result = await props.api.createRevision(
      props.documentId,
      props.versionId,
      workbench.value.documentStateVersion);
    props.navigate(revisionPath(result.versionId));
  } catch (exception) {
    const data = (exception as {
      response?: {
        data?: {
          error?: string;
          existingRevision?: { versionId: string };
          current?: { stateVersion: number };
        };
      };
    }).response?.data;
    if (data?.error === 'revision-already-editable' &&
        data.existingRevision) {
      props.navigate(revisionPath(data.existingRevision.versionId));
    } else if (data?.error === 'document-concurrency-conflict') {
      error.value = '文档已被其他操作员更新，请刷新后重试。';
    } else {
      error.value = '创建修订版本失败，请检查文档状态后重试。';
    }
  } finally {
    busy.value = false;
  }
}

function continueRevision(): void {
  if (!editableRevision.value) return;
  props.navigate(revisionPath(editableRevision.value.versionId));
}

function updateSelectedTags(): void {
  tagError.value = selectedTagIds.value.length
    ? ''
    : '建立索引时至少选择一个已启用的知识标签。';
}

async function handleDocumentCommand(command: string): Promise<void> {
  if (command === 'manage') {
    props.navigate(`/knowledge/documents/${encodeURIComponent(props.documentId)}`);
    return;
  }
  if (!workbench.value) return;
  if (command === 'disable') {
    if (!await props.confirmAction(
      '停用后文档将不可用于检索，并会提交索引清理。确认继续？'
    )) return;
    busy.value = true;
    try {
      await props.api.disableDocument(
        props.documentId,
        workbench.value.documentStateVersion);
      notice.value = '文档已停用。';
      await load();
    } catch {
      error.value = '停用失败，请刷新后重试。';
    } finally {
      busy.value = false;
    }
    return;
  }
  if (command === 'delete' && canRequestPhysicalDelete.value) {
    if (!await props.confirmAction(
      '这会停用文档并提交异步物理清理。确认继续？'
    )) return;
    busy.value = true;
    try {
      await props.api.requestPhysicalDelete(
        props.documentId,
        workbench.value.documentStateVersion);
      notice.value = '删除请求已受理，等待后台清理。';
      await load();
    } catch {
      error.value = '提交物理删除失败，请刷新后重试。';
    } finally {
      busy.value = false;
    }
  }
}

function dateText(value: string): string {
  if (!value) return '未知';
  return new Intl.DateTimeFormat('zh-CN', {
    timeZone: 'Asia/Shanghai',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit'
  }).format(new Date(value));
}

onMounted(load);
</script>

<template>
  <section class="ops-page" aria-labelledby="document-detail-title">
    <header class="page-header">
      <div>
        <p class="eyebrow">知识文档 / 版本工作台</p>
        <h1 id="document-detail-title">
          {{ workbench?.documentTitle || '知识版本工作台' }}
        </h1>
        <p>
          入库内容与索引 · v{{ currentVersion?.version ?? '-' }}
          · {{ sourceText }}
        </p>
      </div>
      <div class="header-actions">
        <ElButton :disabled="busy" @click="load">刷新</ElButton>
        <ElDropdown trigger="click" @command="handleDocumentCommand">
          <ElButton data-testid="document-more-actions">更多操作</ElButton>
          <template #dropdown>
            <ElDropdownMenu>
              <ElDropdownItem command="manage">返回文档管理</ElDropdownItem>
              <ElDropdownItem
                command="disable"
                :disabled="workbench?.documentStatus === 'disabled'"
              >停用文档</ElDropdownItem>
              <ElDropdownItem
                v-if="canRequestPhysicalDelete"
                command="delete"
                divided
              >提交物理删除</ElDropdownItem>
            </ElDropdownMenu>
          </template>
        </ElDropdown>
      </div>
    </header>

    <ElSkeleton
      v-if="loading"
      :rows="8"
      animated
      aria-label="正在加载知识版本工作台"
    />
    <ElAlert
      v-else-if="error && !workbench"
      :title="error"
      type="error"
      :closable="false"
      show-icon
    >
      <ElButton @click="load">重试</ElButton>
    </ElAlert>

    <template v-else-if="workbench">
      <section class="summary-strip" aria-label="版本摘要">
        <div>
          <span>当前查看</span>
          <strong>版本 {{ currentVersion?.version }}</strong>
        </div>
        <div>
          <span>当前生效</span>
          <strong>
            {{ isCurrentActiveVersion ? `版本 ${currentVersion?.version}` : '其他版本' }}
          </strong>
        </div>
        <div>
          <span>文档状态</span>
          <ElTag effect="plain">{{ indexStatus.documentStatus }}</ElTag>
        </div>
        <div>
          <span>索引一致性</span>
          <strong>{{ indexStatus.consistency }}</strong>
        </div>
      </section>

      <ElAlert
        v-if="isLegacySource"
        title="该版本来自历史数据，来源信息可能不完整；页面只展示能够由数据库确认的内容。"
        type="warning"
        :closable="false"
        show-icon
      />

      <div class="workbench-grid">
        <section class="panel workbench-main">
          <ElTabs v-model="activeTab" class="workbench-tabs">
            <ElTabPane name="content">
              <template #label>
                <span data-testid="tab-content">已入库内容</span>
              </template>

              <div
                v-if="canGenerateFromSource"
                class="policy-grid"
                aria-label="分段策略"
              >
                <label>分段策略
                  <select v-model="policyKind" data-testid="chunk-policy-kind">
                    <option value="smart">智能分段</option>
                    <option value="separator">指定分隔符</option>
                    <option value="regex">正则分段</option>
                    <option value="qa">问答对</option>
                  </select>
                </label>
                <label>目标长度（Token）
                  <input
                    v-model.number="targetTokens"
                    data-testid="chunk-target-tokens"
                    type="number"
                    min="1"
                  >
                </label>
                <label>重叠长度（Token）
                  <input
                    v-model.number="overlapTokens"
                    data-testid="chunk-overlap-tokens"
                    type="number"
                    min="0"
                  >
                </label>
                <label>最大长度（Token）
                  <input
                    v-model.number="maximumTokens"
                    data-testid="chunk-maximum-tokens"
                    type="number"
                    min="1"
                  >
                </label>
                <label
                  v-if="policyKind === 'separator'"
                  class="policy-wide"
                >分隔符（支持 \n、\r、\t）
                  <input v-model="separator" data-testid="chunk-separator">
                </label>
                <label
                  v-if="policyKind === 'regex'"
                  class="policy-wide"
                >正则表达式
                  <input v-model="regexPattern" data-testid="chunk-regex">
                </label>
                <label
                  v-if="policyKind === 'qa'"
                  class="policy-wide"
                >QA 条目（每行：问题|同义问法1,同义问法2|答案）
                  <textarea
                    v-model="qaEntriesText"
                    data-testid="chunk-qa-entries"
                    rows="5"
                  />
                </label>
              </div>

              <div class="section-heading">
                <div>
                  <h2>{{ canMutatePreviews ? '分段预览' : '已批准内容' }}</h2>
                  <p>
                    共 {{ previews.length }} 段。
                    <template v-if="canMutatePreviews">
                      修订号 {{ revision }}，可编辑、拆分、连续多段合并或删除。
                    </template>
                    <template v-else>
                      分段内容已锁定，当前内容只读；修改请创建新的修订版本。
                    </template>
                  </p>
                </div>
                <div v-if="canMutatePreviews" class="actions">
                  <ElButton
                    v-if="canGenerateFromSource"
                    data-testid="generate-previews"
                    :disabled="busy"
                    @click="generate"
                  >重新生成预览</ElButton>
                  <ElButton
                    data-testid="merge-selected"
                    :disabled="busy || selected.length < 2"
                    @click="merge"
                  >合并所选{{ selected.length ? `（${selected.length}）` : '' }}</ElButton>
                  <ElButton
                    data-testid="approve-previews"
                    type="primary"
                    :disabled="busy || !previews.length"
                    @click="approve"
                  >批准分段</ElButton>
                </div>
              </div>

              <ElEmpty
                v-if="!previews.length"
                description="当前版本暂无可展示内容。"
              />
              <ol v-else class="chunk-list">
                <li
                  v-for="(item, index) in previews"
                  :key="item.id"
                  class="chunk-card"
                >
                  <label v-if="canMutatePreviews" class="chunk-select">
                    <input
                      v-model="selected"
                      type="checkbox"
                      :value="item.id"
                      :data-testid="`select-${item.id}`"
                    >
                    用于合并：选择第 {{ item.sequence + 1 }} 段
                  </label>

                  <template v-if="!canMutatePreviews && workbench.chunks[index]?.question">
                    <p class="chunk-kicker">问题</p>
                    <h3>{{ workbench.chunks[index]?.question }}</h3>
                    <p
                      v-if="workbench.chunks[index]?.synonyms.length"
                      class="helper"
                    >
                      同义问法：{{ workbench.chunks[index]?.synonyms.join('、') }}
                    </p>
                    <p class="chunk-answer">
                      {{ workbench.chunks[index]?.answer || item.text }}
                    </p>
                  </template>

                  <template v-else>
                    <div class="chunk-meta">
                      <strong>第 {{ item.sequence + 1 }} 段</strong>
                      <span v-if="item.pageNumber">第 {{ item.pageNumber }} 页</span>
                    </div>
                    <label class="sr-only" :for="`text-${item.id}`">
                      第 {{ item.sequence + 1 }} 段内容
                    </label>
                    <textarea
                      :id="`text-${item.id}`"
                      v-model="drafts[item.id]"
                      :readonly="!canMutatePreviews"
                      :data-testid="`text-${item.id}`"
                      rows="5"
                    />
                  </template>

                  <div v-if="canMutatePreviews" class="actions">
                    <ElButton
                      :data-testid="`edit-${item.id}`"
                      :disabled="busy"
                      @click="edit(item)"
                    >编辑</ElButton>
                    <ElButton
                      :data-testid="`split-${item.id}`"
                      :disabled="busy"
                      @click="split(item)"
                    >拆分</ElButton>
                    <ElButton
                      :data-testid="`delete-${item.id}`"
                      type="danger"
                      plain
                      :disabled="busy"
                      @click="remove(item)"
                    >删除</ElButton>
                  </div>
                </li>
              </ol>
            </ElTabPane>

            <ElTabPane name="source">
              <template #label>
                <span data-testid="tab-source">原始消息</span>
              </template>
              <section v-if="workbench.sourceEvidence" class="source-message">
                <dl>
                  <div>
                    <dt>来源成员</dt>
                    <dd>{{ workbench.sourceEvidence.actorDisplayName }}</dd>
                  </div>
                  <div>
                    <dt>会话类型</dt>
                    <dd>{{ workbench.sourceEvidence.channelType }}</dd>
                  </div>
                  <div>
                    <dt>接收时间</dt>
                    <dd>{{ dateText(workbench.sourceEvidence.receivedAtUtc) }}</dd>
                  </div>
                </dl>
                <article>{{ workbench.sourceEvidence.text }}</article>
              </section>
              <ElAlert
                v-else
                :title="sourceUnavailableTitle"
                :description="sourceUnavailableDescription"
                type="info"
                :closable="false"
                show-icon
              />
            </ElTabPane>

            <ElTabPane name="history">
              <template #label>
                <span data-testid="tab-history">版本历史</span>
              </template>
              <ElEmpty
                v-if="!versions.length"
                description="该文档暂无版本记录。"
              />
              <ol v-else class="history-list">
                <li v-for="version in versions" :key="version.id">
                  <div>
                    <strong>版本 {{ version.version }} · {{ version.originalFileName }}</strong>
                    <p>
                      {{ ({
                        DocumentUpload: '文档上传',
                        ConversationReview: '消息审核入库',
                        PrivateChatDirect: '私聊直接入库',
                        AdministrationRevision: '管理员修订',
                        LegacyUnknown: '历史数据'
                      } as Record<string, string>)[version.sourceKind ?? 'LegacyUnknown'] ?? '其他来源' }}
                      · {{ dateText(version.updatedAtUtc) }}
                    </p>
                    <p v-if="version.supersedesVersionId" class="helper">
                      修订自版本 ID：{{ version.supersedesVersionId }}
                    </p>
                  </div>
                  <div class="history-actions">
                    <ElTag effect="plain">{{ version.status }}</ElTag>
                    <a
                      :href="revisionPath(version.id)"
                      @click.prevent="props.navigate(revisionPath(version.id))"
                    >查看</a>
                  </div>
                </li>
              </ol>
            </ElTabPane>
          </ElTabs>
        </section>

        <aside class="workbench-side">
          <section class="panel">
            <h2>内容维护</h2>
            <p v-if="canMutatePreviews" class="helper">
              当前是可编辑修订版本。批准后再建立索引，旧版本会继续生效到新索引激活。
            </p>
            <template v-else>
              <ElButton
                v-if="editableRevision"
                data-testid="continue-revision"
                type="primary"
                @click="continueRevision"
              >继续编辑修订 v{{ editableRevision.version }}</ElButton>
              <ElButton
                v-else-if="canCreateRevision"
                data-testid="create-revision"
                type="primary"
                :loading="busy"
                @click="createRevision"
              >创建修订版本</ElButton>
              <p v-else class="helper">
                当前状态不能创建修订版本。
              </p>
            </template>
          </section>

          <section class="panel">
            <div class="section-heading compact">
              <div>
                <h2>知识标签与索引</h2>
                <p>
                  标签将在索引成功时生效，不会提前修改线上检索范围。
                </p>
              </div>
            </div>
            <KnowledgeTagSelector
              v-model="selectedTagIds"
              :api="tagApi"
              aria-label="索引知识标签"
              @update:model-value="updateSelectedTags"
            />
            <p
              v-if="tagError"
              id="index-tag-error"
              data-testid="index-tag-error"
              class="field-error"
              role="alert"
            >{{ tagError }}</p>
            <ElAlert
              v-if="hasRunningIndexJob"
              title="索引任务正在处理中，请完成后再提交。"
              type="info"
              :closable="false"
              show-icon
            />
            <div class="stack-actions">
              <ElButton
                v-if="latestFailedJob"
                data-testid="retry-index"
                :disabled="busy"
                @click="retry"
              >重试索引</ElButton>
              <ElButton
                data-testid="queue-index"
                type="primary"
                :loading="busy"
                :disabled="hasRunningIndexJob || !selectedTagIds.length"
                @click="queueIndex"
              >{{ indexActionLabel }}</ElButton>
            </div>
          </section>

          <section class="panel source-summary">
            <h2>来源摘要</h2>
            <dl>
              <div><dt>知识来源</dt><dd>{{ sourceText }}</dd></div>
              <div v-if="currentVersion?.sourceActorDisplayName">
                <dt>来源成员</dt>
                <dd>{{ currentVersion.sourceActorDisplayName }}</dd>
              </div>
              <div>
                <dt>变更类型</dt>
                <dd>{{ currentVersion?.changeKind || 'New' }}</dd>
              </div>
            </dl>
          </section>
        </aside>
      </div>
    </template>

    <ElAlert
      v-if="error && workbench"
      :title="error"
      type="error"
      :closable="false"
      show-icon
    />
    <p class="sr-live" aria-live="polite">{{ notice }}</p>
  </section>
</template>

<style scoped>
.ops-page {
  display: grid;
  width: 100%;
  max-width: 1280px;
  margin: 0 auto;
  gap: var(--space-lg);
}

.page-header,
.header-actions,
.section-heading,
.history-list li,
.history-actions {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--space-md);
}

.page-header p,
.section-heading p,
.history-list p {
  margin-bottom: 0;
  color: var(--color-muted-text);
}

.header-actions {
  flex-wrap: wrap;
}

.summary-strip {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 1px;
  overflow: hidden;
  border: 1px solid var(--color-border);
  border-radius: .75rem;
  background: var(--color-border);
}

.summary-strip > div {
  display: grid;
  min-width: 0;
  align-items: start;
  gap: var(--space-xs);
  padding: var(--space-md);
  background: var(--color-surface);
}

.summary-strip .el-tag {
  justify-self: start;
}

.summary-strip span,
.source-summary dt {
  color: var(--color-muted-text);
  font-size: .85rem;
}

.workbench-grid {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(280px, 340px);
  align-items: start;
  gap: var(--space-lg);
}

.workbench-main,
.workbench-side,
.workbench-tabs {
  min-width: 0;
}

.workbench-side {
  display: grid;
  gap: var(--space-lg);
}

.panel {
  padding: var(--space-lg);
  border: 1px solid var(--color-border);
  border-radius: .75rem;
  background: var(--color-surface);
}

.section-heading {
  margin-bottom: var(--space-lg);
}

.section-heading.compact {
  margin-bottom: var(--space-md);
}

.policy-grid {
  margin-bottom: var(--space-lg);
}

.chunk-list,
.history-list {
  display: grid;
  gap: var(--space-md);
  margin: 0;
  padding: 0;
  list-style: none;
}

.chunk-card,
.history-list li,
.source-message {
  min-width: 0;
  padding: var(--space-md);
  border: 1px solid var(--color-border);
  border-radius: .65rem;
  background: var(--color-background);
}

.chunk-meta {
  display: flex;
  justify-content: space-between;
  margin-bottom: var(--space-sm);
  color: var(--color-muted-text);
}

.chunk-card textarea {
  width: 100%;
  resize: vertical;
}

.chunk-kicker {
  margin-bottom: var(--space-xs);
  color: var(--color-primary);
  font-size: .8rem;
  font-weight: 700;
}

.chunk-answer {
  margin: var(--space-md) 0 0;
  white-space: pre-wrap;
  line-height: 1.75;
}

.source-message dl,
.source-summary dl {
  display: grid;
  gap: var(--space-sm);
  margin: 0 0 var(--space-md);
}

.source-message dl {
  grid-template-columns: repeat(3, minmax(0, 1fr));
}

.source-message dl > div,
.source-summary dl > div {
  display: grid;
  min-width: 0;
  gap: var(--space-xs);
}

.source-message dt,
.source-summary dt {
  color: var(--color-muted-text);
}

.source-message dd,
.source-summary dd {
  margin: 0;
}

.source-message article {
  padding: var(--space-md);
  border-radius: .5rem;
  background: var(--color-surface);
  white-space: pre-wrap;
  line-height: 1.75;
}

.history-actions {
  flex-shrink: 0;
  align-items: center;
}

.stack-actions {
  display: grid;
  gap: var(--space-sm);
  margin-top: var(--space-md);
}

.stack-actions .el-button,
.workbench-side > .panel > .el-button {
  width: 100%;
  margin-left: 0;
}

@media (max-width: 900px) {
  .summary-strip {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .workbench-grid {
    grid-template-columns: 1fr;
  }

  .workbench-side {
    grid-row: auto;
  }
}

@media (max-width: 640px) {
  .page-header,
  .section-heading,
  .history-list li {
    align-items: stretch;
    flex-direction: column;
  }

  .summary-strip,
  .source-message dl {
    grid-template-columns: 1fr;
  }
}
</style>
