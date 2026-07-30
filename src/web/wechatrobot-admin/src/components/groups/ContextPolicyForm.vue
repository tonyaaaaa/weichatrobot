<script setup lang="ts">
import { ElInputNumber, ElOption, ElSelect } from 'element-plus';
import type { ContextOverrides, EffectiveContext } from '../../api/groups';

const props = defineProps<{ configured: ContextOverrides; effective: EffectiveContext }>();
const emit = defineEmits<{ 'update:configured': [value: ContextOverrides] }>();

function update(patch: Partial<ContextOverrides>): void {
  emit('update:configured', { ...props.configured, ...patch });
}

function nullableBoolean(value: string | boolean): boolean | null {
  return value === 'inherit' ? null : value === true;
}
</script>

<template>
  <section class="context-policy" aria-label="上下文策略">
    <header>
      <h2>短期上下文策略</h2>
      <p>留空表示继承系统默认值；字段下方显示当前实际生效值。</p>
    </header>
    <div class="context-section">
      <h3>会话边界</h3>
      <div class="context-grid">
        <label>上下文范围
          <ElSelect :model-value="configured.senderIsolated ?? 'inherit'" @update:model-value="update({ senderIsolated: nullableBoolean($event) })">
            <ElOption label="继承系统默认" value="inherit" />
            <ElOption label="群共享" :value="false" />
            <ElOption label="按观察到的成员昵称隔离" :value="true" />
          </ElSelect>
          <small>有效值：{{ effective.senderIsolated ? '按成员昵称隔离' : '群共享' }}</small>
        </label>
        <label>空闲超时（分钟）
          <ElInputNumber :model-value="configured.idleTimeoutMinutes ?? undefined" :min="1" :max="1440" controls-position="right" @update:model-value="update({ idleTimeoutMinutes: $event ?? null })" />
          <small>有效值：{{ effective.idleTimeoutMinutes }}</small>
        </label>
      </div>
    </div>
    <div class="context-section">
      <h3>输入控制</h3>
      <div class="context-grid">
        <label>历史轮数
          <ElInputNumber :model-value="configured.historyTurns ?? undefined" :min="0" :max="100" controls-position="right" @update:model-value="update({ historyTurns: $event ?? null })" />
          <small>有效值：{{ effective.historyTurns }}</small>
        </label>
        <label>Token 上限
          <ElInputNumber :model-value="configured.tokenCap ?? undefined" :min="256" :max="100000" controls-position="right" @update:model-value="update({ tokenCap: $event ?? null })" />
          <small>有效值：{{ effective.tokenCap }}</small>
        </label>
        <label>摘要
          <ElSelect :model-value="configured.summaryEnabled ?? 'inherit'" @update:model-value="update({ summaryEnabled: nullableBoolean($event) })">
            <ElOption label="继承系统默认" value="inherit" />
            <ElOption label="启用" :value="true" />
            <ElOption label="关闭" :value="false" />
          </ElSelect>
          <small>有效值：{{ effective.summaryEnabled ? '启用' : '关闭' }}</small>
        </label>
        <label>机器人历史
          <ElSelect :model-value="configured.includeBotHistory ?? 'inherit'" @update:model-value="update({ includeBotHistory: nullableBoolean($event) })">
            <ElOption label="继承系统默认" value="inherit" />
            <ElOption label="纳入" :value="true" />
            <ElOption label="不纳入" :value="false" />
          </ElSelect>
          <small>有效值：{{ effective.includeBotHistory ? '纳入' : '不纳入' }}</small>
        </label>
      </div>
    </div>
  </section>
</template>

<style scoped>
.context-policy { display: grid; gap: var(--space-xl); padding: var(--space-xl); border: 1px solid var(--color-border); border-radius: .9rem; background: var(--color-surface); }
header h2, header p, h3 { margin: 0; }
header p, small { color: var(--color-muted-text); }
.context-section { display: grid; gap: var(--space-md); }
.context-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--space-lg); }
label { display: grid; min-width: 0; gap: var(--space-sm); font-weight: 600; }
label :deep(.el-input-number), label :deep(.el-select) { width: 100%; }
small { font-weight: 400; }
@media (max-width: 650px) { .context-grid { grid-template-columns: 1fr; } }
</style>
