<script setup lang="ts">
import { onMounted, ref } from 'vue';
import {
  ElAlert, ElButton, ElEmpty, ElInput, ElOption, ElPagination, ElSelect,
  ElSkeleton, ElTable, ElTableColumn, ElTag
} from 'element-plus';
import { knowledgeReviewApi, type CandidateDetail, type CandidateSummary, type KnowledgeReviewApi } from '../../api/knowledge';
import { formatBeijingTime } from '../../utils/beijingTime';
import { safeEvidence } from '../../utils/evidenceRedaction';
import { parseKnowledgeTagIds } from '../../utils/knowledgeTagIds';

const props = withDefaults(defineProps<{ api?: KnowledgeReviewApi }>(), { api: () => knowledgeReviewApi });
const loading = ref(true); const busy = ref(false); const error = ref(''); const notice = ref('');
const items = ref<CandidateSummary[]>([]); const total = ref(0); const page = ref(1); const pageSize = 20;
const status = ref('pending'); const detail = ref<CandidateDetail>(); const revisedAnswer = ref(''); const tagText = ref('');
const tagError = ref('');
async function load() {
  loading.value = true; error.value = '';
  try { const result = await props.api.listCandidates(status.value, page.value, pageSize); items.value = result.items; total.value = result.total; }
  catch { error.value = '待审核知识加载失败，请重试。'; } finally { loading.value = false; }
}
function changeStatus(): void { page.value = 1; void load(); }
function changePage(value: number): void { page.value = value; void load(); }
async function select(id: string) {
  busy.value = true; error.value = '';
  try {
    detail.value = await props.api.getCandidate(id); revisedAnswer.value = detail.value.answer;
    tagText.value = ''; tagError.value = '';
  }
  catch { error.value = '候选知识详情加载失败。'; } finally { busy.value = false; }
}
async function review(decision: 'approve' | 'reject') {
  if (!detail.value) return;
  const parsed = decision === 'approve'
    ? parseKnowledgeTagIds(tagText.value, '批准时至少填写一个有效的知识标签 ID。')
    : { tagIds: [], error: '' };
  tagError.value = parsed.error;
  if (tagError.value) return;
  if (!window.confirm(decision === 'approve' ? '确认批准该答案并进入索引流程？' : '确认拒绝该候选答案？')) return;
  busy.value = true; error.value = '';
  try {
    const result = await props.api.reviewCandidate(detail.value.id, {
      decision, revisedAnswer: revisedAnswer.value, tagIds: parsed.tagIds,
      idempotencyKey: crypto.randomUUID(), expectedVersion: detail.value.version
    });
    notice.value = `审核已提交，状态：${result.status}`; detail.value = undefined; await load();
  } catch { error.value = '审核提交失败，请刷新版本后重试。'; } finally { busy.value = false; }
}
onMounted(load);
</script>

<template>
  <section class="ops-page" aria-labelledby="review-title">
    <header class="page-header"><div><p class="eyebrow">人工答案学习</p><h1 id="review-title">知识审核</h1><p>人工回答必须经授权人员审核并成功建立索引后，才会用于后续机器人检索。</p></div><ElButton @click="load">刷新</ElButton></header>
    <section class="split-layout">
      <div class="panel">
        <div class="toolbar"><label for="review-status">状态</label><ElSelect id="review-status" v-model="status" aria-label="审核状态" @change="changeStatus"><ElOption value="pending" label="待审核" /><ElOption value="revision" label="待修订" /><ElOption value="approved_pending_index" label="待索引" /><ElOption value="indexing" label="索引中" /><ElOption value="published" label="已发布" /><ElOption value="rejected" label="已拒绝" /></ElSelect></div>
        <ElSkeleton v-if="loading" :rows="4" animated aria-label="正在加载审核队列" />
        <ElAlert v-else-if="error && !items.length" :title="error" type="error" :closable="false" show-icon><ElButton @click="load">重试</ElButton></ElAlert>
        <ElEmpty v-else-if="!items.length" description="当前筛选条件下暂无候选知识。" />
        <ElTable v-else :data="items" row-key="id" table-layout="auto">
          <ElTableColumn prop="question" label="问题" min-width="180" />
          <ElTableColumn label="状态" width="120"><template #default="{ row }"><ElTag effect="plain">{{ row.status }}</ElTag></template></ElTableColumn>
          <ElTableColumn label="更新时间" min-width="160"><template #default="{ row }">{{ formatBeijingTime(row.updatedAtUtc) }}</template></ElTableColumn>
          <ElTableColumn label="操作" width="92"><template #default="{ row }"><ElButton :data-testid="`candidate-${row.id}`" @click="select(row.id)">查看</ElButton></template></ElTableColumn>
        </ElTable>
        <div class="pagination"><span>共 {{ total }} 条</span><ElPagination :current-page="page" :page-size="pageSize" :total="total" layout="prev, pager, next" @current-change="changePage" /></div>
      </div>
      <aside class="panel detail-panel">
        <h2>审核详情</h2><ElSkeleton v-if="busy && !detail" :rows="5" animated /><ElEmpty v-else-if="!detail" description="从左侧选择候选答案。" />
        <template v-else>
          <dl><dt>问题</dt><dd>{{ detail.question }}</dd><dt>当前状态</dt><dd><ElTag effect="plain">{{ detail.status }}</ElTag></dd><dt>证据（已移除秘密字段）</dt><dd><pre>{{ safeEvidence(detail.evidenceJson || '无') }}</pre></dd></dl>
          <label for="revised-answer">审核后的答案</label><ElInput id="revised-answer" v-model="revisedAnswer" type="textarea" :rows="6" />
          <label for="candidate-tags">知识标签 ID（批准时必填）</label>
          <ElInput id="candidate-tags" v-model="tagText" :aria-invalid="Boolean(tagError)" aria-describedby="candidate-tag-help candidate-tag-error" @input="tagError = ''" />
          <p id="candidate-tag-help" class="helper">当前页面尚未提供标签列表，请手动填写已启用标签的 UUID；多个标签用逗号分隔，采用任一标签匹配（OR），拒绝时无需填写。</p>
          <p v-if="tagError" id="candidate-tag-error" data-testid="candidate-tag-error" class="field-error" role="alert">{{ tagError }}</p>
          <div class="actions"><ElButton data-testid="approve-candidate" type="primary" :loading="busy" @click="review('approve')">批准并索引</ElButton><ElButton data-testid="reject-candidate" type="danger" plain :disabled="busy" @click="review('reject')">拒绝</ElButton></div>
        </template>
      </aside>
    </section>
    <ElAlert v-if="error && items.length" :title="error" type="error" :closable="false" show-icon /><p aria-live="polite">{{ notice }}</p>
  </section>
</template>
