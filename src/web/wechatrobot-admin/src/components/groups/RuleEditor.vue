<script setup lang="ts">
import { ElButton, ElInput, ElOption, ElSelect, ElSwitch } from 'element-plus';
import type { GroupRule, PatternKind } from '../../api/groups';

const props = defineProps<{ includeRules: GroupRule[]; excludeRules: GroupRule[] }>();
const emit = defineEmits<{
  add: [payload: [direction: 'include' | 'exclude', kind: PatternKind]];
  remove: [payload: [direction: 'include' | 'exclude', index: number]];
}>();
const kinds: { value: PatternKind; label: string }[] = [
  { value: 'exact', label: '精确' },
  { value: 'contains', label: '包含' },
  { value: 'regex', label: '正则' }
];
</script>

<template>
  <section class="rule-editor" aria-label="群匹配规则">
    <div v-for="direction in (['include', 'exclude'] as const)" :key="direction" class="rule-group">
      <header>
        <div>
          <h3>{{ direction === 'include' ? '包含（任一匹配）' : '排除（优先级最高）' }}</h3>
          <p>{{ direction === 'include' ? '满足任意一条后允许进入自动回答。' : '命中任意一条时拒绝进入自动回答。' }}</p>
        </div>
        <div class="add-actions">
          <ElButton
            v-for="kind in kinds"
            :key="kind.value"
            :data-testid="`add-${kind.value}-${direction}`"
            @click="emit('add', [direction, kind.value])"
          >
            添加{{ kind.label }}
          </ElButton>
        </div>
      </header>
      <p v-if="(direction === 'include' ? props.includeRules : props.excludeRules).length === 0" class="empty">尚未添加规则</p>
      <div
        v-for="(rule, index) in (direction === 'include' ? props.includeRules : props.excludeRules)"
        :key="rule.id ?? `${direction}-${index}`"
        class="rule-row"
      >
        <ElSelect v-model="rule.patternKind" :aria-label="`${direction}-${index}-类型`">
          <ElOption v-for="kind in kinds" :key="kind.value" :label="kind.label" :value="kind.value" />
        </ElSelect>
        <ElInput v-model="rule.pattern" :aria-label="`${direction}-${index}-模式`" placeholder="群名称或正则表达式" maxlength="1024" />
        <label class="case-toggle">忽略大小写 <ElSwitch v-model="rule.ignoreCase" /></label>
        <ElButton type="danger" plain :aria-label="`删除${direction === 'include' ? '包含' : '排除'}规则 ${index + 1}`" @click="emit('remove', [direction, index])">删除</ElButton>
      </div>
    </div>
  </section>
</template>

<style scoped>
.rule-editor { display: grid; gap: var(--space-xl); }
.rule-group { display: grid; gap: var(--space-lg); padding: var(--space-lg) 0; }
.rule-group + .rule-group { border-top: 1px solid var(--color-border); }
header { display: flex; justify-content: space-between; gap: var(--space-xl); }
h3, p { margin: 0; }
header p, .empty { color: var(--color-muted-text); }
.add-actions { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: var(--space-sm); }
.rule-row { display: grid; grid-template-columns: 8rem minmax(12rem, 1fr) auto auto; align-items: center; gap: var(--space-md); }
.case-toggle { display: inline-flex; align-items: center; gap: var(--space-sm); white-space: nowrap; }
@media (max-width: 800px) {
  header { display: grid; }
  .add-actions { justify-content: flex-start; }
  .rule-row { grid-template-columns: 1fr; }
}
</style>
