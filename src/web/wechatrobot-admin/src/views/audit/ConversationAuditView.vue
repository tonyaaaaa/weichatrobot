<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { ElAlert, ElButton, ElEmpty, ElMessage, ElOption, ElPagination, ElSelect, ElSkeleton, ElTag } from 'element-plus';
import { auditApi, type AuditApi, type AuditGroupOption, type AuditPage } from '../../api/audit';
import { formatBeijingTime } from '../../utils/beijingTime';
import { safeEvidence, safeEvidenceText } from '../../utils/evidenceRedaction';
import { confirmAction } from '../../utils/dialogs';

const props = withDefaults(
  defineProps<{ api?: AuditApi; initialGroupId?: string }>(),
  { api: () => auditApi, initialGroupId: '' }
);
const loading = ref(true); const error = ref('');
const capability = ref<AuditPage>({ available: false, items: [], total: 0, page: 1, pageSize: 20 });
const groupOptions = ref<AuditGroupOption[]>([]);
const groupId = ref(props.initialGroupId);
const fromLocal = ref('');
const toLocal = ref('');
async function load(requestedPage = capability.value.page) {
  loading.value = true; error.value = '';
  try {
    capability.value = await props.api.capability({
      groupId: groupId.value.trim() || undefined,
      fromUtc: toUtc(fromLocal.value),
      toUtc: toUtc(toLocal.value),
      page: requestedPage,
      pageSize: capability.value.pageSize
    });
  } catch { error.value = '审计查询失败，请检查群 ID 和时间范围。'; }
  finally { loading.value = false; }
}
async function applyFilters() { await load(1); }
function toUtc(value: string): string | undefined {
  if (!value) return undefined;
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? undefined : parsed.toISOString();
}
function sourceLabel(source: string) {
  return ({
    knowledge: '知识库',
    web_search: '联网搜索',
    model_knowledge: '模型知识',
    insufficient: '无可靠证据',
    clarification: '需要补充信息',
    system_failure: '系统失败',
    none: '未分类'
  } as Record<string, string>)[source] ?? '未分类';
}
function sourceTagType(source: string): 'success' | 'warning' | 'info' | 'danger' {
  if (source === 'knowledge') return 'success';
  if (source === 'web_search') return 'warning';
  if (source === 'system_failure') return 'danger';
  return 'info';
}
async function createKnowledgeCandidate(item: AuditPage['items'][number]) {
  const answer = typeof item.answer === 'string' ? item.answer.trim() : '';
  if (!answer) { error.value = '当前记录没有可提交的回答内容。'; return; }
  if (!await confirmAction('确认将当前回答作为管理员纠错候选提交到知识学习审核？')) return;
  try {
    await props.api.createKnowledgeCandidate(item.id, answer);
    ElMessage.success('已创建知识候选，请到知识学习审核中处理。');
    await load();
  } catch {
    error.value = '创建知识候选失败，可能已经存在或数据已变化。';
  }
}
onMounted(load);
onMounted(async () => {
  try { groupOptions.value = await props.api.groupOptions(); }
  catch { error.value = '群筛选选项加载失败，请刷新页面重试。'; }
});
</script>

<template>
  <section class="ops-page" aria-labelledby="audit-title">
    <header class="page-header"><div><p class="eyebrow">授权证据视图</p><h1 id="audit-title">会话审计</h1><p>此页面可展示检索来源和回答证据，但会递归过滤密钥、令牌和认证头。</p></div><ElButton @click="() => load()">刷新</ElButton></header>
    <section class="panel audit-filters">
      <label>群
        <ElSelect v-model="groupId" data-testid="audit-group-select" filterable clearable placeholder="全部群">
          <ElOption
            v-for="group in groupOptions"
            :key="group.id"
            :value="group.id"
            :label="`${group.name}${group.workToolGroupRemark ? `（${group.workToolGroupRemark}）` : ''} · ${group.robotName}${group.isEnabled ? '' : ' · 已停用'}`"
          />
        </ElSelect>
      </label>
      <label>开始时间<input v-model="fromLocal" data-testid="audit-from" type="datetime-local"></label>
      <label>结束时间<input v-model="toLocal" data-testid="audit-to" type="datetime-local"></label>
      <ElButton data-testid="apply-audit-filters" type="primary" @click="applyFilters">查询</ElButton>
      <p>时间会转换为 UTC：开始时间包含，结束时间不包含（[开始, 结束)）。</p>
    </section>
    <ElSkeleton v-if="loading" :rows="5" animated aria-label="正在检查审计查询能力" />
    <ElAlert v-else-if="error" :title="error" type="error" :closable="false" show-icon><ElButton @click="() => load()">重试</ElButton></ElAlert>
    <section v-else-if="!capability.available" class="panel"><ElAlert title="后端暂未提供会话审计查询 API" type="info" :closable="false" show-icon><p>{{ capability.message }}</p></ElAlert></section>
    <section v-else class="panel">
      <ElEmpty v-if="!capability.items.length" description="暂无会话审计记录。" />
      <div v-else class="audit-list">
        <article v-for="item in capability.items" :key="String(item.id)" class="audit-card">
          <header>
            <div class="audit-title">
              <strong>{{ item.question }}</strong>
              <ElTag :type="sourceTagType(item.answerSource)" size="small">
                {{ sourceLabel(item.answerSource) }}
              </ElTag>
            </div>
            <time>{{ formatBeijingTime(String(item.createdAtUtc ?? '')) }}</time>
          </header>
          <p>{{ safeEvidenceText(item.answer) }}</p>
          <div class="actions"><ElButton size="small" :disabled="!item.answer || !!item.knowledgeCandidate" @click="createKnowledgeCandidate(item)">创建知识候选</ElButton></div>
          <section v-if="item.answerSource === 'web_search'" class="web-sources">
            <h2>网页来源</h2>
            <ElAlert
              v-if="item.webSearchFailureCode"
              :title="`联网搜索状态：${item.webSearchFailureCode}`"
              type="warning"
              :closable="false"
            />
            <ul v-if="item.webSearchSources.length">
              <li v-for="source in item.webSearchSources" :key="`${source.index}-${source.url}`">
                <a :href="source.url" target="_blank" rel="noopener noreferrer">
                  {{ source.title }}
                </a>
                <span v-if="source.site"> · {{ source.site }}</span>
                <time v-if="source.publishedAt"> · {{ source.publishedAt }}</time>
              </li>
            </ul>
            <p v-else>本次没有可安全展示的网页来源。</p>
          </section>
          <div class="evidence-grid">
            <section><h2>输入摘要</h2><pre>{{ safeEvidence(item.inputSummary) }}</pre></section>
            <section><h2>发送状态</h2><pre>{{ safeEvidence(item.send) }}</pre></section>
            <section v-if="item.knowledgeCandidate"><h2>知识候选</h2><pre>{{ safeEvidence(item.knowledgeCandidate) }}</pre></section>
          </div>
          <h2>检索来源</h2><div class="tag-list"><ElTag v-for="source in (item.sources as string[] ?? [])" :key="source" effect="plain">{{ safeEvidenceText(source) }}</ElTag></div>
          <details><summary>完整证据（已移除秘密字段）</summary><pre>{{ safeEvidence(item.evidence) }}</pre></details>
        </article>
      </div>
      <ElPagination v-if="capability.total > capability.pageSize" :current-page="capability.page" :page-size="capability.pageSize"
        :total="capability.total" layout="prev, pager, next, total" @current-change="load" />
    </section>
  </section>
</template>
