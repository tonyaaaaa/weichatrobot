<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { ElAlert, ElButton, ElEmpty, ElProgress, ElSkeleton, ElTag } from 'element-plus';
import {
  knowledgeApi,
  type KnowledgeApi,
  type KnowledgeDocumentDetail,
  type KnowledgeDocumentVersionSummary
} from '../../api/knowledge';
import { useAuthStore } from '../../stores/auth';
import { confirmAction as defaultConfirmAction } from '../../utils/dialogs';

type ManagementApi = Pick<
  KnowledgeApi,
  'upload' | 'getDocument' | 'retryDocumentUpload' | 'disableDocument' | 'requestPhysicalDelete'
>;

interface MutationError {
  error?: string;
  current?: {
    id: string;
    status: string;
    stateVersion: number;
  };
}

const props = withDefaults(defineProps<{
  documentId: string;
  api?: ManagementApi;
  confirmAction?: (message: string) => boolean | Promise<boolean>;
}>(), {
  api: () => knowledgeApi,
  confirmAction: defaultConfirmAction
});

const auth = useAuthStore();
const detail = ref<KnowledgeDocumentDetail>();
const loading = ref(true);
const busy = ref('');
const error = ref('');
const notice = ref('');
const selectedFile = ref<File>();
const uploadProgress = ref(0);
const canRequestPhysicalDelete = computed(() =>
  auth.user?.roles.includes('Admin') === true);
const isAutomaticSource = computed(() =>
  detail.value?.document.sourceKind === 'ConversationReview' ||
  detail.value?.document.sourceKind === 'PrivateChatDirect');
const documentSourceLabel = computed(() => ({
  DocumentUpload: '文档上传',
  ConversationReview: '消息审核入库',
  PrivateChatDirect: '私聊直接入库',
  AdministrationRevision: '管理员修订',
  LegacyUnknown: '历史数据'
} as Record<string, string>)[detail.value?.document.sourceKind ?? 'LegacyUnknown']
  ?? '其他来源');
const hasDocumentUploadLineage = computed(() =>
  detail.value?.versions.some(version =>
    version.sourceKind === 'DocumentUpload') === true);
const canUploadNewVersion = computed(() =>
  detail.value?.document.status !== 'disabled' &&
  hasDocumentUploadLineage.value);
const isLegacyDoc = computed(() =>
  selectedFile.value?.name.toLowerCase().endsWith('.doc') ?? false);
const versions = computed(() =>
  [...(detail.value?.versions ?? [])].sort((left, right) => right.version - left.version));

async function load(): Promise<void> {
  loading.value = true;
  error.value = '';
  try {
    detail.value = await props.api.getDocument(props.documentId);
  } catch {
    error.value = '文档详情加载失败，请检查权限和后端服务后重试。';
  } finally {
    loading.value = false;
  }
}

async function retryUpload(): Promise<void> {
  if (!detail.value?.document.canRetryUpload) return;
  await mutate(
    'retry',
    () => props.api.retryDocumentUpload(
      props.documentId,
      detail.value!.document.stateVersion),
    '上传重试已提交，文档状态已刷新。');
}

function chooseNewVersionFile(event: Event): void {
  selectedFile.value = (event.target as HTMLInputElement).files?.[0];
  uploadProgress.value = 0;
  error.value = '';
  notice.value = '';
}

async function uploadNewVersion(): Promise<void> {
  if (!selectedFile.value || !canUploadNewVersion.value || isLegacyDoc.value) return;
  busy.value = 'upload-version';
  error.value = '';
  notice.value = '';
  try {
    const result = await props.api.upload(
      selectedFile.value,
      value => { uploadProgress.value = value; },
      props.documentId);
    uploadProgress.value = 100;
    selectedFile.value = undefined;
    notice.value = `新版本 v${result.version} 已提交处理。`;
    await load();
  } catch {
    error.value = '新版本上传失败，请检查文件和网络后重试。';
  } finally {
    busy.value = '';
  }
}

async function disable(): Promise<void> {
  if (!detail.value) return;
  const confirmed = await props.confirmAction(
    '停用后所有版本将不可用于检索，并会提交相关索引清理。确认继续？');
  if (!confirmed) return;
  await mutate(
    'disable',
    () => props.api.disableDocument(
      props.documentId,
      detail.value!.document.stateVersion),
    '文档已停用，状态已刷新。');
}

async function requestPhysicalDelete(): Promise<void> {
  if (!detail.value || !canRequestPhysicalDelete.value) return;
  const confirmed = await props.confirmAction(
    '这会停用文档并提交异步物理清理，期间不可上传新版本。确认继续？');
  if (!confirmed) return;
  await mutate(
    'physical-delete',
    () => props.api.requestPhysicalDelete(
      props.documentId,
      detail.value!.document.stateVersion),
    '删除请求已受理，等待后台清理');
}

async function mutate(
  operation: string,
  action: () => Promise<unknown>,
  success: string
): Promise<void> {
  busy.value = operation;
  error.value = '';
  notice.value = '';
  try {
    await action();
    notice.value = success;
    await load();
  } catch (exception) {
    const data = (exception as { response?: { data?: MutationError } })
      ?.response?.data;
    if (data?.error === 'document-concurrency-conflict' && data.current) {
      replaceCurrent(data.current);
      notice.value = '文档已被其他操作员修改，页面已更新为服务端当前状态。';
    } else if (data?.error === 'document-delete-requested') {
      error.value = '文档已经提交物理删除请求，请刷新查看清理状态。';
    } else if (data?.error === 'document-not-retryable') {
      error.value = '最新版本已不可重试，请刷新查看当前状态。';
    } else {
      error.value = '操作失败，请检查文档状态后重试。';
    }
  } finally {
    busy.value = '';
  }
}

function replaceCurrent(current: {
  id: string;
  status: string;
  stateVersion: number;
}): void {
  if (!detail.value || detail.value.document.id !== current.id) return;
  detail.value.document = {
    ...detail.value.document,
    status: current.status,
    stateVersion: current.stateVersion,
    canRetryUpload: false
  };
}

function dateText(value: string): string {
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime())
    ? value
    : parsed.toLocaleString('zh-CN', { hour12: false });
}

function sizeText(size: number): string {
  if (size < 1024) return `${size} B`;
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`;
  return `${(size / 1024 / 1024).toFixed(1)} MB`;
}

function evidenceText(version: KnowledgeDocumentVersionSummary): string {
  return [
    `预览 ${version.previewCount} 段`,
    `批准 ${version.approvedChunkCount} 段`,
    `OCR ${version.ocrPageCount} 页（失败 ${version.ocrFailedPageCount} 页）`
  ].join(' · ');
}

function sourceLabel(version: KnowledgeDocumentVersionSummary): string {
  return ({
    DocumentUpload: '文档上传',
    ConversationReview: '消息审核入库',
    PrivateChatDirect: '私聊直接入库',
    AdministrationRevision: '管理员修订',
    LegacyUnknown: '历史数据'
  } as Record<string, string>)[version.sourceKind ?? 'LegacyUnknown']
    ?? '其他来源';
}

function changeKindLabel(changeKind: string | undefined): string {
  return ({
    New: '新增',
    Duplicate: '重复',
    Supplement: '补充',
    Correction: '纠正'
  } as Record<string, string>)[changeKind ?? 'New'] ?? '其他变更';
}

onMounted(load);
</script>

<template>
  <section class="ops-page" aria-labelledby="document-management-title">
    <header class="page-header">
      <div>
        <p class="eyebrow">知识文档 / 管理</p>
        <h1 id="document-management-title">{{ detail?.document.title ?? '文档详情' }}</h1>
        <p class="mono">文档 {{ documentId }}</p>
      </div>
      <div class="header-actions">
        <a class="secondary-link back-link" href="/knowledge/documents">返回文档列表</a>
        <ElButton :loading="loading" @click="load">刷新</ElButton>
      </div>
    </header>

    <ElAlert
      v-if="error"
      :title="error"
      type="error"
      :closable="false"
      show-icon
      role="alert"
    />
    <ElAlert
      v-if="notice"
      :title="notice"
      type="success"
      :closable="false"
      show-icon
      aria-live="polite"
    />

    <ElSkeleton v-if="loading && !detail" :rows="8" animated aria-label="正在加载文档详情" />
    <template v-else-if="detail">
      <section class="panel summary-panel" aria-labelledby="document-state-title">
        <div>
          <p class="eyebrow">持久化状态</p>
          <h2 id="document-state-title">文档状态</h2>
          <p data-testid="document-state">
            <ElTag effect="plain">{{ detail.document.status }}</ElTag>
            <span>状态版本 {{ detail.document.stateVersion }} · 共 {{ detail.document.versionCount }} 个版本</span>
          </p>
          <p v-if="detail.document.latestFailureReason" class="failure-summary">
            {{ detail.document.latestFailureReason }}
          </p>
          <p class="helper">更新时间 {{ dateText(detail.document.updatedAtUtc) }}</p>
          <div class="source-summary">
            <span>来源：{{ documentSourceLabel }}</span>
            <span v-if="detail.document.sourceActorDisplayName">
              来源成员：{{ detail.document.sourceActorDisplayName }}
            </span>
            <span>
              绑定知识库：
              {{ detail.document.tags.length
                ? detail.document.tags.map(tag => tag.name).join('、')
                : '未绑定' }}
            </span>
          </div>
        </div>
        <div class="summary-actions">
          <ElButton
            v-if="detail.document.canRetryUpload"
            data-testid="retry-document-upload"
            :loading="busy === 'retry'"
            @click="retryUpload"
          >重试上传</ElButton>
          <ElButton
            v-if="detail.document.status !== 'disabled'"
            data-testid="disable-document"
            :loading="busy === 'disable'"
            @click="disable"
          >停用文档</ElButton>
          <ElButton
            v-if="canRequestPhysicalDelete"
            data-testid="request-physical-delete"
            type="danger"
            plain
            :loading="busy === 'physical-delete'"
            @click="requestPhysicalDelete"
          >提交物理删除</ElButton>
        </div>
      </section>

      <section
        v-if="hasDocumentUploadLineage"
        class="panel upload-version-panel"
        aria-labelledby="upload-version-title"
      >
        <header class="section-heading">
          <div>
            <h2 id="upload-version-title">上传新版本</h2>
            <p>新文件会登记到当前文档，版本历史、配置和审计继续保留。</p>
          </div>
        </header>

        <template v-if="canUploadNewVersion">
          <div class="upload-version-form">
            <label for="new-version-file">文件（Markdown / TXT / PDF / DOCX）</label>
            <input
              id="new-version-file"
              data-testid="new-version-file"
              type="file"
              accept=".md,.txt,.pdf,.doc,.docx"
              :disabled="busy === 'upload-version'"
              @change="chooseNewVersionFile"
            >
            <p class="helper">旧版 DOC 需要先转换为 DOCX；上传后请在版本历史中查看处理状态。</p>
            <ElAlert
              v-if="isLegacyDoc"
              title="检测到 DOC 文件，请先用 Word 另存为 DOCX 后再上传。"
              type="warning"
              :closable="false"
              show-icon
            />
          </div>
          <ElProgress
            v-if="busy === 'upload-version' || uploadProgress"
            :percentage="uploadProgress"
            :stroke-width="10"
            :aria-label="`新版本上传进度 ${uploadProgress}%`"
          />
          <ElButton
            type="primary"
            data-testid="upload-new-version"
            :loading="busy === 'upload-version'"
            :disabled="!selectedFile || isLegacyDoc"
            @click="uploadNewVersion"
          >{{ busy === 'upload-version' ? '正在上传…' : '上传新版本' }}</ElButton>
        </template>
        <ElAlert
          v-else
          title="当前文档状态不允许上传新版本。"
          type="info"
          :closable="false"
          show-icon
        />
      </section>

      <section class="panel version-panel" aria-labelledby="version-history-title">
        <header class="section-heading">
          <div>
            <h2 id="version-history-title">版本历史</h2>
            <p>按版本号从新到旧展示上传、解析、OCR、分段和索引的持久化证据。</p>
          </div>
        </header>

        <ElEmpty v-if="versions.length === 0" description="该文档尚无版本记录。" />
        <ol v-else class="version-timeline">
          <li v-for="version in versions" :key="version.id" class="version-card">
            <header>
              <div>
                <p class="eyebrow">版本 {{ version.version }}</p>
                <h3>{{ version.originalFileName }}</h3>
              </div>
              <ElTag effect="plain">{{ version.status }}</ElTag>
            </header>

            <dl class="version-facts">
              <div><dt>文件</dt><dd>{{ version.contentType }} · {{ sizeText(version.sizeBytes) }}</dd></div>
              <div><dt>处理证据</dt><dd>{{ evidenceText(version) }}</dd></div>
              <div><dt>发布</dt><dd>{{ version.isPublished ? '已发布' : '未发布' }}</dd></div>
              <div>
                <dt>知识来源</dt>
                <dd>
                  {{ sourceLabel(version) }}
                  <span v-if="version.sourceActorDisplayName">
                    · {{ version.sourceActorDisplayName }}
                  </span>
                  <span v-if="version.changeKind && version.changeKind !== 'New'">
                    · {{ changeKindLabel(version.changeKind) }}
                  </span>
                </dd>
              </div>
              <div>
                <dt>绑定知识库</dt>
                <dd>
                  <div v-if="version.tags.length" class="tag-list">
                    <ElTag
                      v-for="tag in version.tags"
                      :key="tag.id"
                      type="success"
                      effect="plain"
                    >{{ tag.name }}</ElTag>
                  </div>
                  <span v-else class="helper">未绑定</span>
                </dd>
              </div>
              <div><dt>更新时间</dt><dd>{{ dateText(version.updatedAtUtc) }}</dd></div>
            </dl>

            <p v-if="version.failureReason" class="failure-summary">{{ version.failureReason }}</p>

            <div v-if="version.uploadAndParseJobs.length" class="job-list">
              <strong>上传与解析任务</strong>
              <ul>
                <li v-for="job in version.uploadAndParseJobs" :key="job.id">
                  {{ job.jobType }} · {{ job.status }} · 尝试 {{ job.attemptCount }} 次
                </li>
              </ul>
            </div>

            <div v-if="version.indexJobs.length" class="job-list">
              <strong>索引任务</strong>
              <ul>
                <li v-for="job in version.indexJobs" :key="job.id">
                  {{ job.operation }} · {{ job.status }} · 尝试 {{ job.attemptCount }} 次
                </li>
              </ul>
            </div>

            <a
              class="primary-link version-link"
              :data-testid="`open-version-${version.id}`"
              :href="`/knowledge/documents/${encodeURIComponent(documentId)}/versions/${encodeURIComponent(version.id)}`"
            >进入分段与索引</a>
          </li>
        </ol>
      </section>
    </template>
  </section>
</template>

<style scoped>
.ops-page {
  display: grid;
  width: 100%;
  max-width: 1200px;
  margin: 0 auto;
  gap: var(--space-xl);
}

.page-header,
.summary-panel,
.section-heading,
.version-card > header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--space-lg);
}

.page-header p,
.section-heading p {
  margin-bottom: 0;
  color: var(--color-muted-text);
}

.header-actions,
.summary-actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--space-sm);
}

.back-link {
  display: inline-flex;
  min-height: 44px;
  align-items: center;
  padding: 0 var(--space-sm);
}

.panel {
  min-width: 0;
  padding: var(--space-xl);
  border: 1px solid var(--color-border);
  border-radius: .75rem;
  background: var(--color-surface);
  box-shadow: var(--shadow-sm);
}

.summary-panel h2,
.version-card h3 {
  margin-bottom: var(--space-sm);
}

[data-testid='document-state'] {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--space-sm);
}

.failure-summary {
  margin: var(--space-sm) 0;
  color: var(--el-color-danger);
}

.helper {
  color: var(--color-muted-text);
}

.source-summary {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-sm) var(--space-lg);
  margin-top: var(--space-md);
  color: var(--color-muted-text);
}

.upload-version-panel,
.version-panel {
  display: grid;
  gap: var(--space-lg);
}

.upload-version-form {
  display: grid;
  gap: var(--space-sm);
}

.upload-version-form input[type='file'] {
  min-height: 44px;
}

.upload-version-panel .el-button {
  justify-self: start;
}

.version-timeline {
  display: grid;
  gap: var(--space-lg);
  margin: 0;
  padding: 0;
  list-style: none;
}

.version-card {
  display: grid;
  gap: var(--space-md);
  padding: var(--space-lg);
  border: 1px solid var(--color-border);
  border-radius: .7rem;
  background: var(--color-background);
}

.version-facts {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--space-md);
  margin: 0;
}

.version-facts > div {
  display: grid;
  gap: var(--space-xs);
}

.version-facts dt {
  color: var(--color-muted-text);
  font-size: .85rem;
  font-weight: 600;
}

.version-facts dd {
  margin: 0;
}

.tag-list {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-xs);
}

.job-list ul {
  display: grid;
  gap: var(--space-xs);
  margin: var(--space-sm) 0 0;
  padding-left: 1.25rem;
}

.version-link {
  justify-self: start;
}

@media (max-width: 760px) {
  .page-header,
  .summary-panel,
  .section-heading,
  .version-card > header {
    align-items: stretch;
    flex-direction: column;
  }

  .version-facts {
    grid-template-columns: 1fr;
  }
}
</style>
