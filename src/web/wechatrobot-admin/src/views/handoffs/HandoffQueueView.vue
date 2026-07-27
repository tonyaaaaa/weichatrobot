<script setup lang="ts">
import { onMounted, ref } from 'vue';
import {
  ElAlert, ElButton, ElEmpty, ElOption, ElPagination, ElSelect, ElSkeleton,
  ElTable, ElTableColumn, ElTag
} from 'element-plus';
import {
  handoffApi, type HandoffApi, type HandoffDetail, type HandoffMessage,
  type HandoffAssigneeOption, type HandoffSummary, type HandoffTransition
} from '../../api/handoffs';
import { formatBeijingTime } from '../../utils/beijingTime';
import { safeEvidence } from '../../utils/evidenceRedaction';

const props = withDefaults(defineProps<{ api?: HandoffApi }>(), { api: () => handoffApi });
const loading = ref(true); const busy = ref(false); const error = ref(''); const notice = ref('');
const items = ref<HandoffSummary[]>([]); const total = ref(0); const page = ref(1); const pageSize = 20; const state = ref('');
const detail = ref<HandoffDetail>(); const selectedHandoffId = ref('');
const messages = ref<HandoffMessage[]>([]); const messageTotal = ref(0); const messagePage = ref(1); const detailPageSize = 10;
const transitions = ref<HandoffTransition[]>([]); const transitionTotal = ref(0); const transitionPage = ref(1);
const assignee = ref(''); const finalAnswer = ref('');
const assigneeOptions = ref<HandoffAssigneeOption[]>([]);
async function load() {
  loading.value = true; error.value = '';
  try { const result = await props.api.list(state.value, page.value, pageSize); items.value = result.items; total.value = result.total; }
  catch { error.value = '人工转接队列加载失败。'; } finally { loading.value = false; }
}
async function loadAssignees() {
  try { assigneeOptions.value = await props.api.assignees(); }
  catch { error.value = '客服选项加载失败，请刷新页面重试。'; }
}
function changeState(): void { page.value = 1; void load(); }
function changeQueuePage(value: number): void { page.value = value; void load(); }
async function loadMessages(id = selectedHandoffId.value) {
  if (!id) return;
  const result = await props.api.messages(id, messagePage.value, detailPageSize);
  messages.value = result.items; messageTotal.value = result.total;
}
async function loadTransitions(id = selectedHandoffId.value) {
  if (!id) return;
  const result = await props.api.transitions(id, transitionPage.value, detailPageSize);
  transitions.value = result.items; transitionTotal.value = result.total;
}
async function select(id: string, resetPages = true) {
  busy.value = true; error.value = '';
  selectedHandoffId.value = id;
  if (resetPages) { messagePage.value = 1; transitionPage.value = 1; }
  try {
    const [record] = await Promise.all([props.api.detail(id), loadMessages(id), loadTransitions(id)]);
    detail.value = record; assignee.value = record.assigneeUserId ?? ''; finalAnswer.value = record.finalAnswer ?? '';
    if (record.assigneeUserId && !assigneeOptions.value.some(option => option.id === record.assigneeUserId)) {
      assigneeOptions.value.push({
        id: record.assigneeUserId,
        displayName: '历史客服',
        email: '',
        roles: [],
        isEnabled: false
      });
    }
  } catch { error.value = '转接详情加载失败。'; } finally { busy.value = false; }
}
async function changeMessagePage(next: number) {
  messagePage.value = next;
  try { await loadMessages(); } catch { error.value = '转接消息加载失败。'; }
}
async function changeTransitionPage(next: number) {
  transitionPage.value = next;
  try { await loadTransitions(); } catch { error.value = '状态迁移加载失败。'; }
}
async function run(handoffId: string, action: () => Promise<unknown>, confirmation: string, success: string) {
  if (!window.confirm(confirmation)) return;
  busy.value = true; error.value = '';
  try {
    await action();
    notice.value = success;
    await load();
    await select(handoffId);
  }
  catch { error.value = '状态更新失败，可能存在并发修改，请刷新后重试。'; } finally { busy.value = false; }
}
async function assignCase() {
  const record = detail.value;
  if (!record || !assignee.value) { error.value = '请选择客服。'; return; }
  await run(record.id, () => props.api.assign(record.id, assignee.value, record.version), '确认分配该转接？', '转接已分配。');
}
async function resolveCase() {
  const record = detail.value;
  if (!record || !finalAnswer.value.trim()) { error.value = '解决前必须填写人工最终答案。'; return; }
  await run(record.id, () => props.api.resolve(record.id, finalAnswer.value, record.version), '确认解决并提交人工答案供后续知识审核？', '转接已解决，答案等待知识审核。');
}
async function restoreCase() {
  const record = detail.value;
  if (!record) return;
  await run(record.id, () => props.api.restore(record.id, record.version), '确认恢复该群的 AI 回复？', 'AI 回复已恢复。');
}
onMounted(() => Promise.all([load(), loadAssignees()]));
</script>

<template>
  <section class="ops-page" aria-labelledby="handoffs-title">
    <header class="page-header"><div><p class="eyebrow">人工协同</p><h1 id="handoffs-title">人工转接</h1><p>查看暂停原因、群消息和完整状态迁移，分配后由企业员工处理。</p></div><ElButton @click="load">刷新</ElButton></header>
    <section class="split-layout">
      <div class="panel">
        <div class="toolbar"><label for="handoff-state">状态</label><ElSelect id="handoff-state" v-model="state" aria-label="转接状态" placeholder="全部状态" @change="changeState"><ElOption value="" label="全部" /><ElOption value="WaitingHuman" label="WaitingHuman" /><ElOption value="HumanHandling" label="HumanHandling" /><ElOption value="Resolved" label="Resolved" /><ElOption value="AIActive" label="AIActive" /></ElSelect></div>
        <ElSkeleton v-if="loading" :rows="4" animated aria-label="正在加载转接队列" />
        <ElAlert v-else-if="error && !items.length" :title="error" type="error" :closable="false" show-icon><ElButton @click="load">重试</ElButton></ElAlert>
        <ElEmpty v-else-if="!items.length" description="当前没有人工转接。" />
        <ElTable v-else :data="items" row-key="id" table-layout="auto">
          <ElTableColumn label="状态" width="150"><template #default="{ row }"><ElTag effect="plain">{{ row.state }}</ElTag></template></ElTableColumn>
          <ElTableColumn prop="reasonCode" label="原因" min-width="150" />
          <ElTableColumn label="更新时间" min-width="160"><template #default="{ row }">{{ formatBeijingTime(row.updatedAtUtc) }}</template></ElTableColumn>
          <ElTableColumn label="操作" width="92"><template #default="{ row }"><ElButton :data-testid="`handoff-${row.id}`" @click="select(row.id)">处理</ElButton></template></ElTableColumn>
        </ElTable>
        <div class="pagination"><span>共 {{ total }} 条</span><ElPagination :current-page="page" :page-size="pageSize" :total="total" layout="prev, pager, next" @current-change="changeQueuePage" /></div>
      </div>
      <aside class="panel detail-panel">
        <h2>转接详情</h2><ElSkeleton v-if="busy && !detail" :rows="5" animated /><ElEmpty v-else-if="!detail" description="从左侧选择转接记录。" />
        <template v-else>
          <dl><dt>状态</dt><dd><ElTag effect="plain">{{ detail.state }}</ElTag></dd><dt>原因</dt><dd>{{ detail.reasonCode }}</dd><dt>证据（已移除秘密字段）</dt><dd><pre>{{ safeEvidence(detail.evidenceJson || '无') }}</pre></dd><dt>版本</dt><dd>{{ detail.version }}</dd></dl>
          <label for="assignee-select">客服</label>
          <ElSelect id="assignee-select" v-model="assignee" data-testid="assignee" filterable placeholder="选择客服">
            <ElOption
              v-for="option in assigneeOptions"
              :key="option.id"
              :value="option.id"
              :disabled="!option.isEnabled"
              :label="`${option.displayName}${option.email ? ` · ${option.email}` : ''}${option.roles.length ? ` · ${option.roles.join('/')}` : ' · 已停用'}`"
            />
          </ElSelect>
          <ElButton data-testid="assign-handoff" :disabled="busy" @click="assignCase">分配客服</ElButton>
          <label for="final-answer">人工最终答案</label><textarea id="final-answer" v-model="finalAnswer" data-testid="final-answer" rows="4" />
          <div class="actions"><ElButton data-testid="resolve-handoff" type="primary" :loading="busy" @click="resolveCase">解决并进入知识审核</ElButton><ElButton :disabled="busy" data-testid="restore-handoff" @click="restoreCase">恢复 AI</ElButton></div>
          <h3>消息</h3><ElEmpty v-if="!messages.length" :image-size="48" description="暂无已记录消息。" />
          <ul v-else class="history-list"><li v-for="message in messages" :key="message.id"><strong>{{ message.senderDisplayName }}</strong>：{{ message.text }}<small>{{ message.authenticationKind }} · {{ formatBeijingTime(message.createdAtUtc) }}</small></li></ul>
          <div class="pagination"><span>共 {{ messageTotal }} 条</span><ElButton data-testid="messages-previous" :disabled="messagePage <= 1" @click="changeMessagePage(messagePage - 1)">上一页</ElButton><ElButton data-testid="messages-next" :disabled="messagePage * detailPageSize >= messageTotal" @click="changeMessagePage(messagePage + 1)">下一页</ElButton></div>
          <h3>状态迁移</h3><ElEmpty v-if="!transitions.length" :image-size="48" description="暂无迁移记录。" />
          <ol v-else class="history-list"><li v-for="transition in transitions" :key="transition.id"><strong>#{{ transition.sequence }} {{ transition.fromState }} → {{ transition.toState }}</strong><span>原因：{{ transition.reasonCode }}</span><small>操作者：{{ transition.actorUserId ?? '系统' }} · {{ formatBeijingTime(transition.createdAtUtc) }}</small></li></ol>
          <div class="pagination"><span>共 {{ transitionTotal }} 条</span><ElButton data-testid="transitions-previous" :disabled="transitionPage <= 1" @click="changeTransitionPage(transitionPage - 1)">上一页</ElButton><ElButton data-testid="transitions-next" :disabled="transitionPage * detailPageSize >= transitionTotal" @click="changeTransitionPage(transitionPage + 1)">下一页</ElButton></div>
        </template>
      </aside>
    </section>
    <ElAlert v-if="error && items.length" :title="error" type="error" :closable="false" show-icon /><p aria-live="polite">{{ notice }}</p>
  </section>
</template>
