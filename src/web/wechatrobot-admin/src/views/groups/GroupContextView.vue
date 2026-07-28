<script setup lang="ts">
import { onMounted, ref } from 'vue';
import {
  ElAlert,
  ElButton,
  ElEmpty,
  ElPagination,
  ElSkeleton,
  ElTag
} from 'element-plus';
import {
  groupApi,
  type GroupApi,
  type GroupContextPage
} from '../../api/groups';
import { formatBeijingTime } from '../../utils/beijingTime';
import { confirmAction } from '../../utils/dialogs';

const props = withDefaults(
  defineProps<{ id: string; api?: Pick<GroupApi, 'getContext' | 'clearContext'> }>(),
  { api: () => groupApi }
);
const context = ref<GroupContextPage>({
  groupId: props.id,
  configurationVersion: 0,
  items: [],
  total: 0,
  page: 1,
  pageSize: 20
});
const loading = ref(true);
const clearing = ref(false);
const error = ref('');
const notice = ref('');

async function load(page = context.value.page) {
  loading.value = true;
  error.value = '';
  try {
    context.value = await props.api.getContext(props.id, page, context.value.pageSize);
  } catch (exception) {
    const status = (exception as { response?: { status?: number } }).response?.status;
    error.value = status === 404 ? '群不存在。' : '短期上下文加载失败，请稍后重试。';
  } finally {
    loading.value = false;
  }
}

async function clearContext() {
  if (!await confirmAction(
    '确认清空这个群的全部短期上下文？历史消息和审计记录会保留，但后续回答不会继续携带清空前的对话。',
    {
      title: '清空短期上下文',
      confirmButtonText: '确认清空',
      danger: true
    }
  )) return;
  clearing.value = true;
  error.value = '';
  notice.value = '';
  try {
    const result = await props.api.clearContext(
      props.id,
      context.value.configurationVersion
    );
    notice.value = `已清空 ${result.clearedSessions} 个会话的短期上下文，历史消息仍保留。`;
    await load(1);
  } catch (exception) {
    const status = (exception as { response?: { status?: number } }).response?.status;
    error.value = status === 409
      ? '群配置已被其他管理员修改，请刷新后重试。'
      : '短期上下文清空失败，请稍后重试。';
  } finally {
    clearing.value = false;
  }
}

onMounted(() => load(1));
</script>

<template>
  <section class="ops-page" aria-labelledby="group-context-title">
    <header class="page-header">
      <div>
        <p class="eyebrow">群管理 / 短期上下文</p>
        <h1 id="group-context-title">当前对话上下文</h1>
        <p>这里只展示当前会送入模型的短期对话窗口，不包含长期记忆，也不会调用模型重新生成摘要。</p>
      </div>
      <div class="header-actions">
        <RouterLink class="el-button text-link-button" :to="{ name: 'group-list' }">返回群列表</RouterLink>
        <ElButton
          data-testid="clear-group-context"
          type="danger"
          plain
          :loading="clearing"
          :disabled="loading"
          @click="clearContext"
        >清空短期上下文</ElButton>
      </div>
    </header>

    <ElAlert v-if="notice" :title="notice" type="success" :closable="false" show-icon />
    <ElAlert v-if="error" :title="error" type="error" :closable="false" show-icon>
      <ElButton @click="() => load()">重新加载</ElButton>
    </ElAlert>
    <ElSkeleton v-if="loading" :rows="6" animated />
    <section v-else-if="!error" class="panel">
      <ElEmpty v-if="context.items.length === 0" description="当前群没有可用的短期上下文。" />
      <div v-else class="context-list">
        <article v-for="session in context.items" :key="session.sessionId" class="context-card">
          <header>
            <div>
              <h2>{{ session.senderDisplayName }}</h2>
              <p>{{ session.scope }} · 最后活动 {{ formatBeijingTime(session.lastActivityAtUtc) }}</p>
            </div>
            <div class="context-tags">
              <ElTag v-if="session.wasIdleReset" type="info">已因空闲重置</ElTag>
              <ElTag v-if="session.wasTokenLimited" type="warning">已按长度裁剪</ElTag>
              <ElTag effect="plain">约 {{ session.contextTokenCount }} tokens</ElTag>
            </div>
          </header>
          <section v-if="session.summary" class="context-summary">
            <h3>较早对话摘要</h3>
            <p>{{ session.summary }}</p>
          </section>
          <div v-if="session.messages.length" class="message-list">
            <article
              v-for="(message, index) in session.messages"
              :key="`${session.sessionId}-${index}`"
              :class="['message-preview', message.role === 'assistant' ? 'assistant' : 'user']"
            >
              <strong>{{ message.role === 'assistant' ? '机器人' : '成员' }}</strong>
              <p>{{ message.content }}</p>
              <time>{{ formatBeijingTime(message.createdAtUtc) }}</time>
            </article>
          </div>
          <ElEmpty v-else description="当前窗口没有消息。" :image-size="56" />
          <footer v-if="session.clearedAtUtc">
            最近清空：{{ formatBeijingTime(session.clearedAtUtc) }}，水位 {{ session.clearedThroughSequence }}
          </footer>
        </article>
      </div>
      <ElPagination
        v-if="context.total > context.pageSize"
        :current-page="context.page"
        :page-size="context.pageSize"
        :total="context.total"
        layout="prev, pager, next, total"
        @current-change="load"
      />
    </section>
  </section>
</template>

<style scoped>
.header-actions,
.context-tags {
  display: flex;
  align-items: center;
  gap: var(--space-sm);
}
.text-link-button { text-decoration: none; }
.context-list { display: grid; gap: var(--space-lg); }
.context-card {
  display: grid;
  gap: var(--space-md);
  padding: var(--space-lg);
  border: 1px solid var(--color-border);
  border-radius: .75rem;
}
.context-card > header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--space-md);
}
.context-card h2,
.context-card h3,
.context-card p { margin: 0; }
.context-card header p,
.context-card footer,
.message-preview time { color: var(--color-muted-text); }
.context-summary {
  padding: var(--space-md);
  border-radius: .5rem;
  background: var(--color-background);
}
.message-list { display: grid; gap: var(--space-sm); }
.message-preview {
  display: grid;
  gap: var(--space-xs);
  max-width: 85%;
  padding: var(--space-md);
  border-radius: .75rem;
  background: var(--color-background);
}
.message-preview.assistant {
  justify-self: end;
  background: var(--color-accent-soft);
}
@media (max-width: 700px) {
  .page-header,
  .context-card > header { flex-direction: column; }
  .header-actions { flex-wrap: wrap; }
  .message-preview { max-width: 100%; }
}
</style>
