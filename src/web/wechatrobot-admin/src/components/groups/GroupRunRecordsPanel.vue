<script setup lang="ts">
defineProps<{ groupId: string; groupName: string }>();
</script>

<template>
  <section class="records-panel">
    <header>
      <h2>本群运行记录</h2>
      <p>以下入口会按当前群自动筛选真实记录，不展示没有后端依据的统计数字。</p>
    </header>
    <div class="record-grid">
      <RouterLink data-testid="record-entry" :to="{ name: 'group-context', params: { id: groupId } }">
        <strong>当前对话上下文</strong><span>查看实际进入模型的短期消息窗口和成员显示名</span><b>查看上下文 →</b>
      </RouterLink>
      <RouterLink data-testid="record-entry" :to="{ name: 'audit', query: { groupId } }">
        <strong>会话审计</strong><span>查看回答来源、知识证据和稳定失败原因</span><b>查看审计 →</b>
      </RouterLink>
      <RouterLink data-testid="record-entry" :to="{ name: 'memory-center', query: { groupId } }">
        <strong>记忆中心</strong><span>查看待整理候选、长期记忆与后台任务</span><b>查看记忆 →</b>
      </RouterLink>
      <RouterLink data-testid="record-entry" :to="{ name: 'send-queue', query: { group: groupName } }">
        <strong>发送队列</strong><span>发送队列当前按群名称筛选，不代表稳定群 ID 关联</span><b>查看发送状态 →</b>
      </RouterLink>
    </div>
  </section>
</template>

<style scoped>
.records-panel { padding: var(--space-xl); border: 1px solid var(--color-border); border-radius: .9rem; background: var(--color-surface); }
header h2, header p { margin: 0; }
header p { color: var(--color-muted-text); }
.record-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--space-lg); margin-top: var(--space-xl); }
.record-grid a { display: grid; min-width: 0; min-height: 9rem; gap: var(--space-sm); padding: var(--space-xl); border: 1px solid var(--color-border); border-radius: .8rem; color: inherit; text-decoration: none; transition: border-color .18s ease, box-shadow .18s ease; }
.record-grid a:hover, .record-grid a:focus-visible { border-color: var(--color-primary); box-shadow: var(--shadow-sm); }
.record-grid span { color: var(--color-muted-text); }
.record-grid b { align-self: end; color: var(--color-primary); }
@media (max-width: 700px) { .record-grid { grid-template-columns: 1fr; } }
</style>
