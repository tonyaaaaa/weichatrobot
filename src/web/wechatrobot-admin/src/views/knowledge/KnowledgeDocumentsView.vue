<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import { ElAlert, ElButton, ElEmpty, ElProgress, ElSkeleton, ElTag } from 'element-plus';
import PublicOssWarning from '../../components/PublicOssWarning.vue';
import {
  knowledgeApi,
  type KnowledgeApi,
  type KnowledgeDocumentPage,
  type KnowledgeDocumentSummary,
  type UploadResult
} from '../../api/knowledge';

type DocumentsApi = Pick<
  KnowledgeApi,
  'upload' | 'listDocuments' | 'retryDocumentUpload'
>;

interface DocumentMutationError {
  error?: string;
  current?: {
    id: string;
    status: string;
    stateVersion: number;
  };
}

const props = defineProps<{ api?: Partial<DocumentsApi> }>();
const selectedFile = ref<File>();
const progress = ref(0);
const uploading = ref(false);
const uploadError = ref('');
const result = ref<UploadResult>();
const filters = reactive({
  query: '',
  status: '',
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
const listError = ref('');
const actionError = ref('');
const notice = ref('');
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
  } catch (value) {
    uploadError.value = message(value);
  } finally {
    uploading.value = false;
  }
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

async function goToPage(value: number): Promise<void> {
  if (value < 1 || value > totalPages.value || value === filters.page) return;
  filters.page = value;
  await load();
}

async function retryUpload(document: KnowledgeDocumentSummary): Promise<void> {
  const retryMethod = props.api?.retryDocumentUpload ?? knowledgeApi.retryDocumentUpload;
  busyId.value = document.id;
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

onMounted(() => {
  if (canListDocuments()) void load();
});
</script>

<template>
  <section class="ops-page" aria-labelledby="documents-title">
    <header class="page-header">
      <div>
        <p class="eyebrow">知识库运营</p>
        <h1 id="documents-title">知识文档</h1>
        <p>上传并管理 Markdown、TXT、PDF 或 DOCX，按持久化状态进入版本、分段和索引流程。</p>
      </div>
      <ElButton v-if="canListDocuments()" :loading="loading" @click="load">刷新列表</ElButton>
    </header>

    <PublicOssWarning />

    <section class="panel upload-panel" aria-labelledby="upload-title">
      <div class="panel-heading">
        <div>
          <h2 id="upload-title">上传文档</h2>
          <p>上传成功只代表源文件已接收；解析、OCR、分段审核与索引状态以文档列表和详情为准。</p>
        </div>
      </div>
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
      <div v-if="result" class="notice success" aria-live="polite">
        <p>已上传 {{ result.safeFileName }}，当前状态：{{ result.state }}。</p>
        <a
          class="primary-link"
          data-testid="open-document-detail"
          :href="`/knowledge/documents/${result.documentId}/versions/${result.versionId}`"
        >进入分段与索引</a>
      </div>
      <ElButton
        type="primary"
        data-testid="upload-document"
        :loading="uploading"
        :disabled="isLegacyDoc"
        @click="upload"
      >
        {{ uploading ? '正在上传…' : '上传文档' }}
      </ElButton>
    </section>

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
        <label>
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
          <span>持久化状态</span>
          <select
            v-model="filters.status"
            data-testid="document-status-filter"
            @change="applyFilters"
          >
            <option value="">全部状态</option>
            <option value="uploading">uploading</option>
            <option value="failed">failed</option>
            <option value="uploaded">uploaded</option>
            <option value="parsing">parsing</option>
            <option value="preview">preview</option>
            <option value="approved">approved</option>
            <option value="indexing">indexing</option>
            <option value="active">active</option>
            <option value="disabled">disabled</option>
          </select>
        </label>
        <ElButton
          data-testid="apply-document-filters"
          :loading="loading"
          @click="applyFilters"
        >查询</ElButton>
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
              <th>文档状态</th>
              <th>最新版本</th>
              <th>失败摘要</th>
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
                <span class="secondary-line">共 {{ document.versionCount }} 个版本</span>
              </td>
              <td>
                <ElTag :type="statusType(document.status)" effect="plain">
                  {{ document.status }}
                </ElTag>
              </td>
              <td>
                <span v-if="document.latestVersion !== null">
                  v{{ document.latestVersion }} · {{ document.latestVersionStatus ?? 'unknown' }}
                </span>
                <span v-else class="muted">暂无版本</span>
              </td>
              <td class="failure-cell">
                {{ document.latestFailureReason ?? '—' }}
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
                    :loading="busyId === document.id"
                    @click="retryUpload(document)"
                  >重试上传</ElButton>
                </div>
              </td>
            </tr>
          </tbody>
        </table>
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

.panel {
  min-width: 0;
  padding: var(--space-xl);
  border: 1px solid var(--color-border);
  border-radius: .75rem;
  background: var(--color-surface);
  box-shadow: var(--shadow-sm);
}

.upload-panel,
.management-panel {
  display: grid;
  gap: var(--space-lg);
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
  grid-template-columns: minmax(16rem, 1fr) minmax(11rem, auto) auto;
  align-items: end;
  gap: var(--space-md);
}

.filter-bar label {
  display: grid;
  gap: var(--space-xs);
  margin: 0;
}

.filter-bar input,
.filter-bar select {
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

.failure-cell {
  min-width: 16rem;
  max-width: 28rem;
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
}
</style>
