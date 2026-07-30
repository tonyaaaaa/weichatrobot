<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import {
  ElAlert,
  ElButton,
  ElDialog,
  ElEmpty,
  ElOption,
  ElProgress,
  ElSelect,
  ElSkeleton,
  ElTag
} from 'element-plus';
import { getActivePinia } from 'pinia';
import {
  knowledgeApi,
  type KnowledgeApi,
  type KnowledgeDocumentPage,
  type KnowledgeDocumentSummary,
  type UploadResult
} from '../../api/knowledge';
import {
  knowledgeTagApi,
  type KnowledgeTagApi,
  type KnowledgeTagOption
} from '../../api/knowledgeTags';
import { useAuthStore } from '../../stores/auth';
import { confirmAction as defaultConfirmAction } from '../../utils/dialogs';

type DocumentsApi = Pick<
  KnowledgeApi,
  'upload' | 'listDocuments' | 'retryDocumentUpload' | 'requestPhysicalDelete'
>;

interface DocumentMutationError {
  error?: string;
  current?: {
    id: string;
    status: string;
    stateVersion: number;
  };
}

const props = withDefaults(defineProps<{
  api?: Partial<DocumentsApi>;
  tagApi?: Pick<KnowledgeTagApi, 'options'>;
  confirmAction?: (message: string) => boolean | Promise<boolean>;
}>(), {
  tagApi: () => knowledgeTagApi,
  confirmAction: defaultConfirmAction
});
const activePinia = getActivePinia();
const auth = activePinia ? useAuthStore(activePinia) : undefined;
const selectedFile = ref<File>();
const progress = ref(0);
const uploading = ref(false);
const uploadError = ref('');
const result = ref<UploadResult>();
const uploadDialogVisible = ref(false);
const tagOptions = ref<KnowledgeTagOption[]>([]);
const tagOptionsError = ref('');
const filters = reactive({
  query: '',
  status: '',
  sourceKind: '',
  tagId: '',
  page: 1,
  pageSize: 20
});
const page = ref<KnowledgeDocumentPage>({
  items: [],
  total: 0,
  page: 1,
  pageSize: 20
});
const loading = ref(props.api?.listDocuments !== undefined || props.api === undefined);
const busyId = ref('');
const busyOperation = ref('');
const listError = ref('');
const actionError = ref('');
const notice = ref('');
const canRequestPhysicalDelete = computed(() =>
  auth?.user?.roles.includes('Admin') === true);
const isLegacyDoc = computed(() =>
  selectedFile.value?.name.toLowerCase().endsWith('.doc') ?? false);
const totalPages = computed(() =>
  Math.max(1, Math.ceil(page.value.total / page.value.pageSize)));

function chooseFile(event: Event): void {
  selectedFile.value = (event.target as HTMLInputElement).files?.[0];
  uploadError.value = '';
  result.value = undefined;
  progress.value = 0;
}

function message(errorValue: unknown): string {
  if (errorValue instanceof Error) return errorValue.message;
  return '上传失败，请检查文件和网络后重试。';
}

async function upload(): Promise<void> {
  if (!selectedFile.value) {
    uploadError.value = '请先选择文件。';
    return;
  }

  uploading.value = true;
  uploadError.value = '';
  try {
    const uploadMethod = props.api?.upload ?? knowledgeApi.upload;
    result.value = await uploadMethod(
      selectedFile.value,
      value => { progress.value = value; });
    progress.value = 100;
    if (canListDocuments()) await load();
    uploadDialogVisible.value = false;
    notice.value = `${result.value.safeFileName} 已上传，文档列表已刷新。`;
  } catch (value) {
    uploadError.value = message(value);
  } finally {
    uploading.value = false;
  }
}

function openUploadDialog(): void {
  selectedFile.value = undefined;
  progress.value = 0;
  uploadError.value = '';
  result.value = undefined;
  uploadDialogVisible.value = true;
}

function canListDocuments(): boolean {
  return props.api?.listDocuments !== undefined || props.api === undefined;
}

async function load(): Promise<void> {
  const listMethod = props.api?.listDocuments ?? knowledgeApi.listDocuments;
  loading.value = true;
  listError.value = '';
  try {
    page.value = await listMethod({ ...filters });
    filters.page = page.value.page;
    filters.pageSize = page.value.pageSize;
  } catch {
    listError.value = '文档列表加载失败，请检查权限和后端服务后重试。';
  } finally {
    loading.value = false;
  }
}

async function applyFilters(): Promise<void> {
  filters.page = 1;
  await load();
}

async function resetFilters(): Promise<void> {
  filters.query = '';
  filters.status = '';
  filters.sourceKind = '';
  filters.tagId = '';
  filters.page = 1;
  await load();
}

async function loadTagOptions(): Promise<void> {
  tagOptionsError.value = '';
  try {
    tagOptions.value = await props.tagApi.options();
  } catch {
    tagOptions.value = [];
    tagOptionsError.value = '知识库筛选项加载失败，文档列表仍可正常使用。';
  }
}

async function goToPage(value: number): Promise<void> {
  if (value < 1 || value > totalPages.value || value === filters.page) return;
  filters.page = value;
  await load();
}

async function retryUpload(document: KnowledgeDocumentSummary): Promise<void> {
  const retryMethod = props.api?.retryDocumentUpload ?? knowledgeApi.retryDocumentUpload;
  busyId.value = document.id;
  busyOperation.value = 'retry';
  actionError.value = '';
  notice.value = '';
  try {
    await retryMethod(document.id, document.stateVersion);
    notice.value = `${document.title} 已重新提交并刷新文档状态。`;
    await load();
  } catch (exception) {
    const response = (exception as {
      response?: {
        status?: number;
        data?: DocumentMutationError;
      };
    })?.response;
    if (response?.data?.error === 'document-concurrency-conflict' &&
        response.data.current) {
      replaceCurrent(response.data.current);
      notice.value = '文档已被其他操作员修改，当前行已刷新为最新状态。';
    } else if (response?.data?.error === 'document-not-retryable') {
      actionError.value = '该文档的最新版本已不可重试，请刷新后查看最新状态。';
    } else if (response?.status === 503) {
      actionError.value = '重新上传仍失败，后台已保留失败状态，可稍后再次尝试。';
      await load();
    } else {
      actionError.value = `${document.title} 重试失败，请稍后重试。`;
    }
  } finally {
    busyId.value = '';
    busyOperation.value = '';
  }
}

async function requestPhysicalDelete(document: KnowledgeDocumentSummary): Promise<void> {
  if (!canRequestPhysicalDelete.value) return;
  const confirmed = await props.confirmAction(
    '这会停用文档并提交异步物理清理，期间不可上传新版本。确认继续？');
  if (!confirmed) return;

  const deleteMethod = props.api?.requestPhysicalDelete ??
    knowledgeApi.requestPhysicalDelete;
  busyId.value = document.id;
  busyOperation.value = 'physical-delete';
  actionError.value = '';
  notice.value = '';
  try {
    await deleteMethod(document.id, document.stateVersion);
    notice.value = `${document.title} 删除请求已受理，等待后台清理。`;
    await load();
  } catch (exception) {
    const response = (exception as {
      response?: {
        data?: DocumentMutationError;
      };
    })?.response;
    if (response?.data?.error === 'document-concurrency-conflict' &&
        response.data.current) {
      replaceCurrent(response.data.current);
      notice.value = '文档已被其他操作员修改，当前行已刷新为最新状态。';
    } else if (response?.data?.error === 'document-delete-requested') {
      actionError.value = '文档已经提交物理删除请求，请刷新查看清理状态。';
    } else {
      actionError.value = `${document.title} 删除请求提交失败，请稍后重试。`;
    }
  } finally {
    busyId.value = '';
    busyOperation.value = '';
  }
}

function replaceCurrent(current: {
  id: string;
  status: string;
  stateVersion: number;
}): void {
  const index = page.value.items.findIndex(item => item.id === current.id);
  if (index < 0) return;
  page.value.items.splice(index, 1, {
    ...page.value.items[index],
    status: current.status,
    stateVersion: current.stateVersion,
    canRetryUpload: false
  });
}

function dateText(value: string): string {
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime())
    ? value
    : parsed.toLocaleString('zh-CN', { hour12: false });
}

function statusType(status: string): 'success' | 'warning' | 'danger' | 'info' {
  if (status === 'active' || status === 'uploaded') return 'success';
  if (status === 'failed') return 'danger';
  if (status === 'disabled') return 'info';
  return 'warning';
}

function statusLabel(status: string): string {
  return ({
    uploading: '上传中',
    failed: '处理失败',
    uploaded: '已上传',
    parsing: '解析中',
    preview: '待审核分段',
    approved: '分段已批准',
    indexing: '索引中',
    active: '已生效',
    disabled: '已停用'
  } as Record<string, string>)[status] ?? status;
}

function sourceLabel(sourceKind: string): string {
  return ({
    DocumentUpload: '文档上传',
    ConversationReview: '消息审核入库',
    PrivateChatDirect: '私聊直接入库',
    LegacyUnknown: '历史数据'
  } as Record<string, string>)[sourceKind] ?? '其他来源';
}

onMounted(() => {
  if (canListDocuments()) {
    void load();
    void loadTagOptions();
  }
});
</script>

<template>
  <section class="ops-page" aria-labelledby="documents-title">
    <header class="page-header">
      <div>
        <p class="eyebrow">知识库运营</p>
        <h1 id="documents-title">知识文档</h1>
        <p>统一查看知识来源、绑定知识库和处理状态，再进入详情处理版本、分段与索引。</p>
      </div>
      <div class="header-actions">
        <ElButton
          type="primary"
          data-testid="open-document-upload"
          @click="openUploadDialog"
        >上传文档</ElButton>
        <ElButton v-if="canListDocuments()" :loading="loading" @click="load">刷新列表</ElButton>
      </div>
    </header>

    <ElDialog
      v-model="uploadDialogVisible"
      title="上传知识文档"
      width="min(42rem, calc(100vw - 2rem))"
      append-to-body
      :close-on-click-modal="!uploading"
      :close-on-press-escape="!uploading"
      :show-close="!uploading"
    >
      <div data-testid="document-upload-dialog" class="upload-dialog-content">
        <p class="helper">上传成功只代表源文件已接收；后续解析、分段与索引状态请从文档详情查看。</p>
        <div class="form-row">
          <label for="knowledge-file">文件（Markdown / TXT / PDF / DOCX）</label>
          <input id="knowledge-file" type="file" accept=".md,.txt,.pdf,.doc,.docx" @change="chooseFile">
          <p class="helper">旧版 DOC 需要先转换为 DOCX；系统不会在后台静默转换格式。</p>
          <ElAlert
            v-if="isLegacyDoc"
            title="检测到 DOC 文件，请先用 Word 另存为 DOCX 后再上传。"
            type="warning"
            :closable="false"
            show-icon
          />
        </div>
        <div v-if="uploading || progress" class="progress-block" aria-live="polite">
          <ElProgress
            :percentage="progress"
            :stroke-width="10"
            :aria-label="`上传进度 ${progress}%`"
          />
        </div>
        <ElAlert
          v-if="uploadError"
          :title="`${uploadError} 请修正后重新上传。`"
          type="error"
          :closable="false"
          show-icon
          role="alert"
        />
      </div>
      <template #footer>
        <ElButton :disabled="uploading" @click="uploadDialogVisible = false">取消</ElButton>
        <ElButton
          type="primary"
          data-testid="upload-document"
          :loading="uploading"
          :disabled="isLegacyDoc"
          @click="upload"
        >{{ uploading ? '正在上传…' : '开始上传' }}</ElButton>
      </template>
    </ElDialog>

    <div v-if="result" class="notice success upload-result" aria-live="polite">
      <p>已上传 {{ result.safeFileName }}，当前状态：{{ statusLabel(result.state) }}。</p>
      <a
        class="primary-link"
        data-testid="open-document-detail"
        :href="`/knowledge/documents/${result.documentId}/versions/${result.versionId}`"
      >进入分段与索引</a>
    </div>

    <ElAlert
      v-if="listError"
      data-testid="document-list-error"
      :title="listError"
      type="error"
      :closable="false"
      show-icon
      role="alert"
    />
    <ElAlert
      v-if="actionError"
      data-testid="document-action-error"
      :title="actionError"
      type="error"
      :closable="false"
      show-icon
      role="alert"
    />
    <ElAlert
      v-if="tagOptionsError"
      data-testid="document-tag-options-error"
      :title="tagOptionsError"
      type="warning"
      :closable="false"
      show-icon
    >
      <ElButton @click="loadTagOptions">重试加载筛选项</ElButton>
    </ElAlert>
    <ElAlert
      v-if="notice"
      data-testid="document-list-notice"
      :title="notice"
      type="success"
      :closable="false"
      show-icon
    />

    <section class="panel management-panel" aria-labelledby="document-management-title">
      <header class="panel-heading">
        <div>
          <h2 id="document-management-title">文档列表</h2>
          <p>展示数据库中的真实文档与最新版本状态；失败原因和可重试性由服务端判定。</p>
        </div>
      </header>

      <div class="filter-bar">
        <label class="query-filter">
          <span>文档名称</span>
          <input
            v-model.trim="filters.query"
            data-testid="document-query-filter"
            type="search"
            placeholder="搜索文档名称"
            @keyup.enter="applyFilters"
          >
        </label>
        <label>
          <span>绑定知识库</span>
          <ElSelect
            v-model="filters.tagId"
            data-testid="document-tag-filter"
            placeholder="全部知识库"
            clearable
          >
            <ElOption
              v-for="tag in tagOptions"
              :key="tag.id"
              :label="tag.name"
              :value="tag.id"
            />
          </ElSelect>
        </label>
        <label>
          <span>来源</span>
          <ElSelect
            v-model="filters.sourceKind"
            data-testid="document-source-filter"
            placeholder="全部来源"
            clearable
          >
            <ElOption label="文档上传" value="DocumentUpload" />
            <ElOption label="消息审核入库" value="ConversationReview" />
            <ElOption label="私聊直接入库" value="PrivateChatDirect" />
            <ElOption label="历史数据" value="LegacyUnknown" />
          </ElSelect>
        </label>
        <label>
          <span>状态</span>
          <ElSelect
            v-model="filters.status"
            data-testid="document-status-filter"
            placeholder="全部状态"
            clearable
          >
            <ElOption label="上传中" value="uploading" />
            <ElOption label="处理失败" value="failed" />
            <ElOption label="已上传" value="uploaded" />
            <ElOption label="解析中" value="parsing" />
            <ElOption label="待审核分段" value="preview" />
            <ElOption label="分段已批准" value="approved" />
            <ElOption label="索引中" value="indexing" />
            <ElOption label="已生效" value="active" />
            <ElOption label="已停用" value="disabled" />
          </ElSelect>
        </label>
        <div class="filter-actions">
          <ElButton
            data-testid="reset-document-filters"
            :disabled="loading"
            @click="resetFilters"
          >重置</ElButton>
          <ElButton
            type="primary"
            data-testid="apply-document-filters"
            :loading="loading"
            @click="applyFilters"
          >查询</ElButton>
        </div>
      </div>

      <ElSkeleton
        v-if="loading"
        :rows="6"
        animated
        aria-label="正在加载知识文档"
      />
      <ElEmpty
        v-else-if="page.items.length === 0"
        description="暂无符合条件的知识文档。"
      />
      <div v-else class="table-scroll">
        <table>
          <thead>
            <tr>
              <th>文档</th>
              <th>绑定知识库</th>
              <th>来源</th>
              <th>状态</th>
              <th>更新时间</th>
              <th class="actions-column">操作</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="document in page.items"
              :key="document.id"
              :data-testid="`document-row-${document.id}`"
            >
              <td>
                <strong>{{ document.title }}</strong>
                <span class="secondary-line">
                  {{ document.latestVersion === null ? '暂无版本' : `最新 v${document.latestVersion}` }}
                  · 共 {{ document.versionCount }} 个版本
                </span>
              </td>
              <td class="tag-cell">
                <div v-if="document.tags.length" class="tag-list">
                  <ElTag
                    v-for="tag in document.tags"
                    :key="tag.id"
                    effect="plain"
                    type="success"
                  >{{ tag.name }}</ElTag>
                </div>
                <span v-else class="muted">未绑定</span>
              </td>
              <td>
                <strong>{{ sourceLabel(document.sourceKind) }}</strong>
                <span v-if="document.sourceActorDisplayName" class="secondary-line">
                  {{ document.sourceActorDisplayName }}
                </span>
              </td>
              <td class="status-cell">
                <ElTag :type="statusType(document.status)" effect="plain">
                  {{ statusLabel(document.status) }}
                </ElTag>
                <span v-if="document.latestFailureReason" class="failure-summary secondary-line">
                  {{ document.latestFailureReason }}
                </span>
              </td>
              <td>{{ dateText(document.updatedAtUtc) }}</td>
              <td>
                <div class="row-actions">
                  <a
                    class="secondary-link action-link"
                    :data-testid="`open-document-${document.id}`"
                    :href="`/knowledge/documents/${encodeURIComponent(document.id)}`"
                  >查看详情</a>
                  <ElButton
                    v-if="document.canRetryUpload"
                    :data-testid="`retry-document-${document.id}`"
                    :loading="busyId === document.id && busyOperation === 'retry'"
                    @click="retryUpload(document)"
                  >重试上传</ElButton>
                  <ElButton
                    v-if="canRequestPhysicalDelete"
                    :data-testid="`delete-document-${document.id}`"
                    type="danger"
                    plain
                    :loading="busyId === document.id && busyOperation === 'physical-delete'"
                    @click="requestPhysicalDelete(document)"
                  >提交物理删除</ElButton>
                </div>
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <div v-if="!loading && page.items.length" class="document-cards">
        <article
          v-for="document in page.items"
          :key="document.id"
          class="document-card"
          :data-testid="`document-card-${document.id}`"
        >
          <header>
            <div>
              <h3>{{ document.title }}</h3>
              <p class="secondary-line">
                {{ document.latestVersion === null ? '暂无版本' : `最新 v${document.latestVersion}` }}
                · 共 {{ document.versionCount }} 个版本
              </p>
            </div>
            <ElTag :type="statusType(document.status)" effect="plain">
              {{ statusLabel(document.status) }}
            </ElTag>
          </header>
          <dl>
            <div>
              <dt>绑定知识库</dt>
              <dd>
                <span v-if="document.tags.length">
                  {{ document.tags.map(tag => tag.name).join('、') }}
                </span>
                <span v-else class="muted">未绑定</span>
              </dd>
            </div>
            <div>
              <dt>来源</dt>
              <dd>
                {{ sourceLabel(document.sourceKind) }}
                <span v-if="document.sourceActorDisplayName">
                  · {{ document.sourceActorDisplayName }}
                </span>
              </dd>
            </div>
            <div v-if="document.latestFailureReason">
              <dt>失败摘要</dt>
              <dd class="failure-summary">{{ document.latestFailureReason }}</dd>
            </div>
            <div><dt>更新时间</dt><dd>{{ dateText(document.updatedAtUtc) }}</dd></div>
          </dl>
          <div class="row-actions">
            <a
              class="secondary-link action-link"
              :href="`/knowledge/documents/${encodeURIComponent(document.id)}`"
            >查看详情</a>
            <ElButton
              v-if="document.canRetryUpload"
              :loading="busyId === document.id && busyOperation === 'retry'"
              @click="retryUpload(document)"
            >重试上传</ElButton>
            <ElButton
              v-if="canRequestPhysicalDelete"
              type="danger"
              plain
              :loading="busyId === document.id && busyOperation === 'physical-delete'"
              @click="requestPhysicalDelete(document)"
            >提交物理删除</ElButton>
          </div>
        </article>
      </div>

      <footer class="pagination-bar">
        <span>共 {{ page.total }} 条 · 第 {{ filters.page }} / {{ totalPages }} 页</span>
        <div>
          <ElButton
            data-testid="previous-document-page"
            :disabled="filters.page <= 1"
            @click="goToPage(filters.page - 1)"
          >上一页</ElButton>
          <ElButton
            data-testid="next-document-page"
            :disabled="filters.page >= totalPages"
            @click="goToPage(filters.page + 1)"
          >下一页</ElButton>
        </div>
      </footer>
    </section>
  </section>
</template>

<style scoped>
.ops-page {
  display: grid;
  width: 100%;
  max-width: 1440px;
  margin: 0 auto;
  gap: var(--space-xl);
}

.page-header,
.panel-heading,
.pagination-bar {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--space-lg);
}

.page-header p,
.panel-heading p {
  margin-bottom: 0;
  color: var(--color-muted-text);
}

.header-actions,
.filter-actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--space-sm);
}

.panel {
  min-width: 0;
  padding: var(--space-xl);
  border: 1px solid var(--color-border);
  border-radius: .75rem;
  background: var(--color-surface);
  box-shadow: var(--shadow-sm);
}

.management-panel {
  display: grid;
  gap: var(--space-lg);
}

.upload-dialog-content {
  display: grid;
  min-width: 0;
  gap: var(--space-lg);
}

.upload-result {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-md);
}

.upload-result p {
  margin: 0;
}

.form-row {
  display: grid;
  gap: var(--space-sm);
}

.form-row input[type='file'] {
  min-height: 44px;
}

.helper,
.secondary-line,
.muted {
  color: var(--color-muted-text);
}

.secondary-line {
  display: block;
  margin-top: var(--space-xs);
  font-size: .875rem;
}

.progress-block {
  max-width: 48rem;
}

.filter-bar {
  display: grid;
  grid-template-columns:
    minmax(14rem, 1.4fr)
    minmax(11rem, 1fr)
    minmax(11rem, 1fr)
    minmax(11rem, 1fr)
    auto;
  align-items: end;
  gap: var(--space-md);
}

.filter-bar label {
  display: grid;
  gap: var(--space-xs);
  margin: 0;
}

.filter-bar input,
.filter-bar .el-select {
  width: 100%;
  min-height: 44px;
}

.table-scroll {
  overflow-x: auto;
}

table {
  width: 100%;
  border-collapse: collapse;
}

th,
td {
  padding: var(--space-md);
  border-bottom: 1px solid var(--color-border);
  text-align: left;
  vertical-align: middle;
}

th {
  color: var(--color-muted-text);
  font-size: .85rem;
  font-weight: 600;
}

.tag-cell {
  min-width: 12rem;
}

.tag-list {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-xs);
}

.status-cell {
  min-width: 12rem;
}

.failure-summary {
  color: var(--el-color-danger);
}

.actions-column {
  min-width: 13rem;
}

.row-actions,
.pagination-bar > div {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--space-sm);
}

.action-link {
  display: inline-flex;
  min-height: 44px;
  align-items: center;
  padding: 0 var(--space-sm);
}

.pagination-bar {
  align-items: center;
  color: var(--color-muted-text);
}

.document-cards {
  display: none;
}

.document-card {
  min-width: 0;
  padding: var(--space-lg);
  border: 1px solid var(--color-border);
  border-radius: .75rem;
  background: var(--color-background);
}

.document-card header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--space-md);
}

.document-card h3,
.document-card p {
  margin: 0;
}

.document-card dl {
  display: grid;
  gap: var(--space-md);
  margin: var(--space-lg) 0;
}

.document-card dl div {
  display: grid;
  gap: var(--space-xs);
}

.document-card dt {
  color: var(--color-muted-text);
  font-size: .85rem;
  font-weight: 600;
}

.document-card dd {
  margin: 0;
}

@media (max-width: 860px) {
  .page-header,
  .panel-heading,
  .pagination-bar {
    align-items: stretch;
    flex-direction: column;
  }

  .filter-bar {
    grid-template-columns: 1fr;
  }

  .table-scroll {
    display: none;
  }

  .document-cards {
    display: grid;
    gap: var(--space-md);
  }

  .upload-result {
    align-items: flex-start;
    flex-direction: column;
  }
}
</style>
