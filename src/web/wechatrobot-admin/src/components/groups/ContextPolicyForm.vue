<script setup lang="ts">
import type { ContextOverrides, EffectiveContext } from '../../api/groups';
const props = defineProps<{ configured: ContextOverrides; effective: EffectiveContext }>();
const emit = defineEmits<{ clear: [] }>();
function normalizeNumber(field: 'historyTurns' | 'idleTimeoutMinutes' | 'tokenCap') {
  const value = (props.configured as Record<string, unknown>)[field];
  props.configured[field] = typeof value === 'number' && Number.isFinite(value) ? value : null;
}
</script>

<template>
  <section class="group-panel context-policy" aria-label="上下文策略">
    <h2>上下文策略</h2>
    <p>留空即继承系统默认值；当前有效设置会显示在字段说明中。</p>
    <div class="context-policy-grid">
      <label>上下文范围 <select v-model="configured.senderIsolated"><option :value="null">继承（群共享）</option><option :value="false">群共享</option><option :value="true">按成员隔离</option></select></label>
      <label>历史轮数 <input v-model.number="configured.historyTurns" type="number" min="0" max="100" placeholder="继承（6）" @change="normalizeNumber('historyTurns')"><small>有效值：{{ effective.historyTurns }}</small></label>
      <label>空闲超时（分钟）<input v-model.number="configured.idleTimeoutMinutes" type="number" min="1" max="1440" placeholder="继承（30）" @change="normalizeNumber('idleTimeoutMinutes')"><small>有效值：{{ effective.idleTimeoutMinutes }}</small></label>
      <label>Token 上限<input v-model.number="configured.tokenCap" type="number" min="256" max="100000" placeholder="继承（3000）" @change="normalizeNumber('tokenCap')"><small>有效值：{{ effective.tokenCap }}</small></label>
      <label>摘要 <select v-model="configured.summaryEnabled"><option :value="null">继承系统默认</option><option :value="true">启用</option><option :value="false">关闭</option></select></label>
      <label>机器人历史 <select v-model="configured.includeBotHistory"><option :value="null">继承系统默认</option><option :value="true">纳入</option><option :value="false">不纳入</option></select></label>
    </div>
    <button class="danger-action" type="button" data-testid="clear-context" @click="emit('clear')">清空本群上下文</button>
  </section>
</template>

<style scoped>
.group-panel {
  min-width: 0;
  padding: var(--space-xl);
  border: 1px solid var(--color-border);
  border-radius: .75rem;
  background: var(--color-surface);
  box-shadow: var(--shadow-sm);
}
.context-policy > p {
  margin-bottom: var(--space-lg);
  color: var(--color-muted-text);
}
.context-policy-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: var(--space-lg);
  margin-bottom: var(--space-xl);
}
.context-policy-grid label {
  display: grid;
  align-content: start;
  gap: var(--space-sm);
  margin: 0;
}
.context-policy-grid small {
  color: var(--color-muted-text);
  font-weight: 400;
}
@media (max-width: 600px) {
  .group-panel { padding: var(--space-lg); }
  .context-policy-grid { grid-template-columns: 1fr; }
}
</style>
