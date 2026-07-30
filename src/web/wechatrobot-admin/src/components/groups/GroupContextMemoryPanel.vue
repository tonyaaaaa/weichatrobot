<script setup lang="ts">
import { ElButton, ElTag } from 'element-plus';
import type { ContextOverrides, EffectiveContext, GroupConfiguration } from '../../api/groups';
import ContextPolicyForm from './ContextPolicyForm.vue';

defineProps<{
  configured: ContextOverrides;
  effective: EffectiveContext;
  memorySummary: GroupConfiguration['memorySummary'];
  groupId: string;
}>();
const emit = defineEmits<{
  'update:configured': [value: ContextOverrides];
  'clear-context': [];
}>();
</script>

<template>
  <div class="context-memory-stack">
    <ContextPolicyForm
      :configured="configured"
      :effective="effective"
      @update:configured="emit('update:configured', $event)"
    />

    <section class="panel">
      <header>
        <div>
          <h2>长期记忆摘要</h2>
          <p>以下为本群真实数据。成员记忆按 WorkTool 观察到的昵称作用域汇总，不代表稳定用户身份。</p>
        </div>
        <ElTag effect="plain">只读</ElTag>
      </header>
      <div class="memory-grid">
        <div data-testid="memory-group-count"><strong>{{ memorySummary.activeGroupMemoryCount }}</strong><span>群级有效记忆</span></div>
        <div data-testid="memory-member-count"><strong>{{ memorySummary.activeMemberMemoryCount }}</strong><span>成员昵称作用域记忆</span></div>
        <div data-testid="memory-candidate-count"><strong>{{ memorySummary.pendingCandidateCount }}</strong><span>待整理候选</span></div>
        <div data-testid="memory-job-count"><strong>{{ memorySummary.pendingOrRunningJobCount }}</strong><span>等待或正在整理</span></div>
      </div>
      <div class="link-actions">
        <RouterLink :to="{ name: 'group-context', params: { id: groupId } }">查看当前对话上下文</RouterLink>
        <RouterLink :to="{ name: 'memory-center', query: { groupId } }">打开本群记忆中心</RouterLink>
      </div>
    </section>

    <section class="panel danger-zone">
      <div>
        <h2>清空短期上下文</h2>
        <p>只重置短期摘要和上下文选择位置；不会删除历史消息、审计记录或长期记忆。</p>
      </div>
      <ElButton type="danger" plain data-testid="clear-context" @click="emit('clear-context')">清空短期上下文</ElButton>
    </section>
  </div>
</template>

<style scoped>
.context-memory-stack { display: grid; gap: var(--space-lg); }
.panel {
  min-width: 0;
  padding: var(--space-xl);
  border: 1px solid var(--color-border);
  border-radius: .9rem;
  background: var(--color-surface);
}
header, .danger-zone { display: flex; align-items: flex-start; justify-content: space-between; gap: var(--space-xl); }
h2, p { margin: 0; }
header p, .danger-zone p { color: var(--color-muted-text); }
.memory-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: var(--space-md);
  margin-top: var(--space-xl);
}
.memory-grid > div { display: grid; gap: .25rem; padding: var(--space-lg); border-radius: .75rem; background: var(--color-background); }
.memory-grid strong { color: var(--color-primary); font-size: 1.65rem; }
.memory-grid span { color: var(--color-muted-text); }
.link-actions { display: flex; flex-wrap: wrap; gap: var(--space-md); margin-top: var(--space-xl); }
.link-actions a { min-height: 2.75rem; display: inline-flex; align-items: center; padding: 0 var(--space-lg); border: 1px solid var(--color-border); border-radius: .65rem; }
.danger-zone { border-color: color-mix(in srgb, var(--color-danger) 35%, var(--color-border)); }
@media (max-width: 760px) {
  .memory-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .danger-zone { display: grid; }
}
@media (max-width: 430px) { .memory-grid { grid-template-columns: 1fr; } }
</style>
