<script setup lang="ts">
import type { GroupRule, PatternKind } from '../../api/groups';
const props = defineProps<{ includeRules: GroupRule[]; excludeRules: GroupRule[] }>();
const emit = defineEmits<{ add: [direction: 'include' | 'exclude', kind: PatternKind]; remove: [direction: 'include' | 'exclude', index: number] }>();
const labels: Record<PatternKind, string> = { exact: '精确', contains: '包含', regex: '正则' };
const ruleKinds = Object.keys(labels) as PatternKind[];
function add(direction: 'include' | 'exclude', kind: PatternKind) { emit('add', direction, kind); }
</script>

<template>
  <section class="group-panel rule-editor" aria-label="群匹配规则">
    <header class="rule-editor-heading">
      <div>
        <h2>匹配规则</h2>
        <p>先满足任一包含规则，再由任一排除规则优先拒绝。正则表达式有服务端超时保护。</p>
      </div>
    </header>
    <div v-for="direction in (['include', 'exclude'] as const)" :key="direction" class="rule-group">
      <div class="rule-section-heading">
        <h3>{{ direction === 'include' ? '包含（任一匹配）' : '排除（优先级最高）' }}</h3>
        <div class="rule-add-actions" :aria-label="direction === 'include' ? '添加包含规则' : '添加排除规则'">
          <button v-for="kind in ruleKinds" :key="`${direction}-${kind}`" type="button" :aria-label="`添加${labels[kind]}${direction === 'include' ? '包含' : '排除'}规则`" :data-testid="`add-${kind}-${direction}`" @click="add(direction, kind)">添加{{ labels[kind] }}</button>
        </div>
      </div>
      <p v-if="(direction === 'include' ? props.includeRules : props.excludeRules).length === 0">尚未添加规则</p>
      <div v-for="(rule, index) in (direction === 'include' ? props.includeRules : props.excludeRules)" :key="`${direction}-${index}`" class="rule-row">
        <select v-model="rule.patternKind" :aria-label="`${direction}-${index}-类型`"><option value="exact">精确</option><option value="contains">包含</option><option value="regex">正则</option></select>
        <input v-model="rule.pattern" type="text" :aria-label="`${direction}-${index}-模式`" placeholder="群名称或正则表达式" maxlength="1024">
        <label class="rule-case-toggle"><input v-model="rule.ignoreCase" type="checkbox">忽略大小写</label>
        <button class="rule-remove danger-action" type="button" :aria-label="`删除${direction === 'include' ? '包含' : '排除'}规则 ${index + 1}`" @click="emit('remove', direction, index)">删除</button>
      </div>
    </div>
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
.rule-editor-heading p,
.rule-group > p {
  margin-bottom: 0;
  color: var(--color-muted-text);
}
.rule-group {
  display: grid;
  gap: var(--space-md);
  padding-top: var(--space-xl);
  border-top: 1px solid var(--color-border);
}
.rule-group + .rule-group { margin-top: var(--space-xl); }
.rule-section-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-md);
}
.rule-section-heading h3 { margin: 0; }
.rule-add-actions {
  display: flex;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: var(--space-md);
}
.rule-add-actions button {
  min-height: 44px;
  padding: .35rem .65rem;
  color: var(--color-accent-strong);
  font-size: .875rem;
}
.rule-row {
  display: grid;
  grid-template-columns: 7rem minmax(12rem, 1fr) auto auto;
  align-items: center;
  gap: var(--space-md);
  padding: var(--space-md);
  border: 1px solid var(--color-border);
  border-radius: .625rem;
  background: var(--color-background);
}
.rule-case-toggle {
  display: flex;
  align-items: center;
  min-height: 44px;
  gap: var(--space-sm);
  margin: 0;
  white-space: nowrap;
  cursor: pointer;
}
.rule-case-toggle input {
  width: 1.25rem;
  min-height: 1.25rem;
  margin: 0;
}
.rule-remove { white-space: nowrap; }
@media (max-width: 700px) {
  .rule-section-heading { align-items: flex-start; flex-direction: column; }
  .rule-add-actions { justify-content: flex-start; }
  .rule-row { grid-template-columns: minmax(7rem, .65fr) minmax(0, 1fr) auto; }
  .rule-row > input[type="text"] { grid-column: 1 / -1; grid-row: 1; }
}
@media (max-width: 480px) {
  .group-panel { padding: var(--space-lg); }
  .rule-row { grid-template-columns: 1fr auto; }
  .rule-row > input[type="text"] { grid-column: 1 / -1; }
  .rule-row > select { min-width: 0; }
}
</style>
