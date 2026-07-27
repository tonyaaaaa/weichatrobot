<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import { ElAlert, ElButton, ElEmpty, ElSkeleton, ElTag } from 'element-plus';
import {
  knowledgeTagApi,
  type KnowledgeTag,
  type KnowledgeTagApi,
  type KnowledgeTagPage
} from '../../api/knowledgeTags';
import { useAuthStore } from '../../stores/auth';

type StateFilter = 'all' | 'enabled' | 'disabled';
type ScopeFilter = 'all' | 'global' | 'scoped';

interface KnowledgeTagApiError {
  error?: string;
  current?: KnowledgeTag;
  references?: {
    groups: number;
    chunks: number;
    reviews: number;
    indexJobs: number;
  };
}

const props = withDefaults(defineProps<{
  api?: KnowledgeTagApi;
  confirmAction?: (message: string) => boolean | Promise<boolean>;
}>(), {
  api: () => knowledgeTagApi,
  confirmAction: (message: string) => window.confirm(message)
});

const auth = useAuthStore();
const canDelete = computed(() => auth.user?.roles.includes('Admin') === true);
const filters = reactive<{
  q: string;
  state: StateFilter;
  global: ScopeFilter;
  page: number;
  pageSize: number;
}>({
  q: '',
  state: 'all',
  global: 'all',
  page: 1,
  pageSize: 20
});
const page = ref<KnowledgeTagPage>({
  items: [],
  total: 0,
  page: 1,
  pageSize: 20
});
const loading = ref(true);
const busyId = ref('');
const error = ref('');
const notice = ref('');
const dialogOpen = ref(false);
const editing = ref<KnowledgeTag>();
const draftName = ref('');
const draftGlobal = ref(false);
const totalPages = computed(() =>
  Math.max(1, Math.ceil(page.value.total / page.value.pageSize)));

async function load(): Promise<void> {
  loading.value = true;
  error.value = '';
  try {
    page.value = await props.api.list({ ...filters });
  } catch {
    error.value = '标签列表加载失败，请检查权限和后端服务。';
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

function openCreate(): void {
  editing.value = undefined;
  draftName.value = '';
  draftGlobal.value = false;
  dialogOpen.value = true;
  error.value = '';
}

function openEdit(tag: KnowledgeTag): void {
  editing.value = tag;
  draftName.value = tag.name;
  draftGlobal.value = tag.isGlobalPublic;
  dialogOpen.value = true;
  error.value = '';
}

function replaceCurrent(tag: KnowledgeTag): void {
  const index = page.value.items.findIndex(item => item.id === tag.id);
  if (index >= 0) {
    page.value.items.splice(index, 1, tag);
  }
}

async function save(): Promise<void> {
  const name = draftName.value.trim();
  if (!name) {
    error.value = '请输入标签名称。';
    return;
  }

  busyId.value = editing.value?.id ?? 'create';
  error.value = '';
  try {
    const saved = editing.value
      ? await props.api.update(editing.value.id, {
          name,
          isGlobalPublic: draftGlobal.value,
          expectedVersion: editing.value.version
        })
      : await props.api.create({
          name,
          isGlobalPublic: draftGlobal.value
        });
    dialogOpen.value = false;
    editing.value = undefined;
    notice.value = `${saved.name} 已保存。`;
    await load();
  } catch (exception) {
    handleMutationError(exception, '标签保存失败，请检查输入后重试。');
  } finally {
    busyId.value = '';
  }
}

async function toggle(tag: KnowledgeTag): Promise<void> {
  busyId.value = `${tag.id}:toggle`;
  error.value = '';
  try {
    const saved = await props.api.setEnabled(tag.id, {
      isEnabled: !tag.isEnabled,
      expectedVersion: tag.version
    });
    notice.value = `${saved.name} 已${saved.isEnabled ? '启用' : '停用'}。`;
    await load();
  } catch (exception) {
    handleMutationError(exception, `${tag.name} 状态更新失败，请刷新后重试。`);
  } finally {
    busyId.value = '';
  }
}

async function remove(tag: KnowledgeTag): Promise<void> {
  const confirmed = await props.confirmAction(
    `仅未被群、分段、审核或索引任务引用的标签可物理删除。确认删除“${tag.name}”？`
  );
  if (!confirmed) return;

  busyId.value = `${tag.id}:delete`;
  error.value = '';
  try {
    await props.api.delete(tag.id, tag.version);
    notice.value = `${tag.name} 已删除。`;
    if (page.value.items.length === 1 && filters.page > 1) {
      filters.page--;
    }
    await load();
  } catch (exception) {
    handleMutationError(exception, `${tag.name} 删除失败，请稍后重试。`);
  } finally {
    busyId.value = '';
  }
}

function handleMutationError(exception: unknown, fallback: string): void {
  const data = (exception as { response?: { data?: KnowledgeTagApiError } })
    ?.response?.data;
  switch (data?.error) {
    case 'knowledge-tag-concurrency-conflict':
      if (data.current) replaceCurrent(data.current);
      notice.value = '标签已被其他操作员修改，页面已刷新为最新版本。';
      return;
    case 'knowledge-tag-name-conflict':
      error.value = '已有同名标签，请使用其他名称。';
      return;
    case 'knowledge-tag-referenced': {
      const references = data.references;
      const counts = references
        ? `（群 ${references.groups}、分段 ${references.chunks}、审核 ${references.reviews}、索引任务 ${references.indexJobs}）`
        : '';
      error.value = `标签仍被引用，不能删除；可先停用。${counts}`;
      return;
    }
    default:
      error.value = fallback;
  }
}

function createdText(value: string): string {
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime())
    ? value
    : parsed.toLocaleString('zh-CN', { hour12: false });
}

onMounted(load);
</script>

<template>
  <section class="ops-page" aria-labelledby="tags-title">
    <header class="page-header">
      <div>
        <p class="eyebrow">知识库运营</p>
        <h1 id="tags-title">知识库标签</h1>
        <p>标签决定群聊检索范围，不改变公共 OSS 文件本身的访问权限。</p>
      </div>
      <div class="header-actions">
        <ElButton :loading="loading" @click="load">刷新</ElButton>
        <ElButton data-testid="create-tag" type="primary" @click="openCreate">新增标签</ElButton>
      </div>
    </header>

    <section class="panel rule-panel">
      <h2>匹配规则</h2>
      <div class="rule-explanation">
        <div>
          <strong>任一标签匹配（OR）</strong>
          <p>群绑定多个标签时，文档命中其中任一标签即可参与检索。例如“技术部”绑定“产品、售后”，即可检索产品 OR 售后文档。</p>
        </div>
        <div>
          <strong>全局公开</strong>
          <p>标记为“全局公开”的知识无需群标签命中，所有已启用群都可以检索。</p>
        </div>
      </div>
    </section>

    <ElAlert v-if="error" :title="error" type="error" :closable="false" show-icon />
    <ElAlert v-if="notice" :title="notice" type="success" :closable="false" show-icon />

    <section class="panel management-panel" aria-labelledby="tag-management-title">
      <header class="panel-heading">
        <div>
          <h2 id="tag-management-title">标签管理</h2>
          <p>停用后不能用于新绑定、索引或审核；已有历史绑定仍可识别。</p>
        </div>
      </header>

      <div class="filter-bar">
        <label>
          <span>名称</span>
          <input
            v-model="filters.q"
            data-testid="tag-query-filter"
            type="search"
            placeholder="搜索标签名称"
            @keyup.enter="applyFilters"
          >
        </label>
        <label>
          <span>状态</span>
          <select
            v-model="filters.state"
            data-testid="tag-state-filter"
            @change="applyFilters"
          >
            <option value="all">全部</option>
            <option value="enabled">已启用</option>
            <option value="disabled">已停用</option>
          </select>
        </label>
        <label>
          <span>范围</span>
          <select
            v-model="filters.global"
            data-testid="tag-scope-filter"
            @change="applyFilters"
          >
            <option value="all">全部</option>
            <option value="global">全局公开</option>
            <option value="scoped">按标签范围</option>
          </select>
        </label>
        <ElButton data-testid="apply-tag-filters" @click="applyFilters">查询</ElButton>
      </div>

      <ElSkeleton v-if="loading" :rows="6" animated aria-label="正在加载知识标签" />
      <ElEmpty v-else-if="page.items.length === 0" description="暂无符合条件的知识标签。" />
      <div v-else class="table-scroll">
        <table>
          <thead>
            <tr>
              <th>名称</th>
              <th>范围</th>
              <th>状态</th>
              <th>版本</th>
              <th>创建时间</th>
              <th class="actions-column">操作</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="tag in page.items" :key="tag.id" :data-testid="`tag-row-${tag.id}`">
              <td><strong>{{ tag.name }}</strong></td>
              <td>
                <ElTag :type="tag.isGlobalPublic ? 'warning' : 'info'" effect="plain">
                  {{ tag.isGlobalPublic ? '全局公开' : '按标签范围' }}
                </ElTag>
              </td>
              <td>
                <ElTag :type="tag.isEnabled ? 'success' : 'info'" effect="plain">
                  {{ tag.isEnabled ? '已启用' : '已停用' }}
                </ElTag>
              </td>
              <td class="mono">{{ tag.version }}</td>
              <td>{{ createdText(tag.createdAtUtc) }}</td>
              <td>
                <div class="row-actions">
                  <ElButton :data-testid="`edit-tag-${tag.id}`" @click="openEdit(tag)">编辑</ElButton>
                  <ElButton
                    :data-testid="`toggle-tag-${tag.id}`"
                    :loading="busyId === `${tag.id}:toggle`"
                    @click="toggle(tag)"
                  >{{ tag.isEnabled ? '停用' : '启用' }}</ElButton>
                  <ElButton
                    v-if="canDelete"
                    :data-testid="`delete-tag-${tag.id}`"
                    type="danger"
                    plain
                    :loading="busyId === `${tag.id}:delete`"
                    @click="remove(tag)"
                  >物理删除</ElButton>
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
            data-testid="previous-page"
            :disabled="filters.page <= 1"
            @click="goToPage(filters.page - 1)"
          >上一页</ElButton>
          <ElButton
            data-testid="next-page"
            :disabled="filters.page >= totalPages"
            @click="goToPage(filters.page + 1)"
          >下一页</ElButton>
        </div>
      </footer>
    </section>

    <div v-if="dialogOpen" class="dialog-backdrop" role="presentation">
      <section class="tag-dialog" role="dialog" aria-modal="true" aria-labelledby="tag-dialog-title">
        <header>
          <div>
            <p class="eyebrow">{{ editing ? '编辑标签' : '新增标签' }}</p>
            <h2 id="tag-dialog-title">{{ editing ? editing.name : '创建知识标签' }}</h2>
          </div>
          <button class="dialog-close" type="button" aria-label="关闭" @click="dialogOpen = false">×</button>
        </header>
        <label>
          <span>标签名称</span>
          <input
            v-model="draftName"
            data-testid="tag-name"
            maxlength="128"
            autocomplete="off"
          >
        </label>
        <label class="checkbox-line">
          <input v-model="draftGlobal" data-testid="tag-global" type="checkbox">
          <span>设为全局公开（所有已启用群均可检索）</span>
        </label>
        <footer>
          <ElButton @click="dialogOpen = false">取消</ElButton>
          <ElButton
            data-testid="save-tag"
            type="primary"
            :loading="busyId === (editing?.id ?? 'create')"
            @click="save"
          >保存</ElButton>
        </footer>
      </section>
    </div>
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
.pagination-bar,
.tag-dialog > header,
.tag-dialog > footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-lg);
}

.page-header {
  align-items: flex-start;
}

.page-header p,
.panel-heading p {
  margin-bottom: 0;
  color: var(--color-muted-text);
}

.header-actions,
.row-actions,
.pagination-bar > div {
  display: flex;
  flex-wrap: wrap;
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

.rule-explanation {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--space-lg);
}

.rule-explanation > div {
  padding: var(--space-lg);
  border-radius: .6rem;
  background: var(--color-background);
}

.rule-explanation p {
  margin: var(--space-xs) 0 0;
  color: var(--color-muted-text);
}

.management-panel {
  display: grid;
  gap: var(--space-lg);
}

.filter-bar {
  display: grid;
  grid-template-columns: minmax(14rem, 1fr) minmax(9rem, auto) minmax(10rem, auto) auto;
  align-items: end;
  gap: var(--space-md);
}

.filter-bar label,
.tag-dialog > label {
  display: grid;
  gap: var(--space-xs);
  margin: 0;
}

.filter-bar input,
.filter-bar select,
.tag-dialog input[type='text'],
.tag-dialog input:not([type]) {
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

.actions-column {
  min-width: 18rem;
}

.pagination-bar {
  color: var(--color-muted-text);
}

.dialog-backdrop {
  position: fixed;
  z-index: 100;
  inset: 0;
  display: grid;
  place-items: center;
  padding: var(--space-xl);
  background: rgb(15 23 42 / 45%);
}

.tag-dialog {
  display: grid;
  width: min(34rem, 100%);
  gap: var(--space-lg);
  padding: var(--space-xl);
  border-radius: .8rem;
  background: var(--color-surface);
  box-shadow: var(--shadow-lg, 0 1.5rem 4rem rgb(15 23 42 / 25%));
}

.tag-dialog h2 {
  margin-bottom: 0;
}

.dialog-close {
  width: 44px;
  min-height: 44px;
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--color-muted-text);
  font-size: 1.75rem;
}

.checkbox-line {
  display: flex !important;
  align-items: flex-start;
  gap: var(--space-sm);
}

.checkbox-line input {
  width: 1.25rem;
  min-height: 1.25rem;
  margin-top: .125rem;
}

@media (max-width: 860px) {
  .page-header,
  .panel-heading,
  .pagination-bar {
    align-items: stretch;
    flex-direction: column;
  }

  .rule-explanation,
  .filter-bar {
    grid-template-columns: 1fr;
  }
}
</style>
