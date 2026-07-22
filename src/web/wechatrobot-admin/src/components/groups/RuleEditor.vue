<script setup lang="ts">
import type { GroupRule, PatternKind } from '../../api/groups';
const props = defineProps<{ includeRules: GroupRule[]; excludeRules: GroupRule[] }>();
const emit = defineEmits<{ add: [direction: 'include' | 'exclude', kind: PatternKind]; remove: [direction: 'include' | 'exclude', index: number] }>();
const labels: Record<PatternKind, string> = { exact: '精确', contains: '包含', regex: '正则' };
function add(direction: 'include' | 'exclude', kind: PatternKind) { emit('add', direction, kind); }
</script>

<template>
  <section aria-label="群匹配规则">
    <h2>匹配规则</h2>
    <p>先满足任一包含规则，再由任一排除规则优先拒绝。正则表达式有服务端超时保护。</p>
    <div class="rule-actions">
      <button v-for="kind in (Object.keys(labels) as PatternKind[])" :key="`include-${kind}`" type="button" :data-testid="`add-${kind}-include`" @click="add('include', kind)">添加{{ labels[kind] }}包含</button>
      <button v-for="kind in (Object.keys(labels) as PatternKind[])" :key="`exclude-${kind}`" type="button" :data-testid="`add-${kind}-exclude`" @click="add('exclude', kind)">添加{{ labels[kind] }}排除</button>
    </div>
    <div v-for="direction in (['include', 'exclude'] as const)" :key="direction" class="rule-list">
      <h3>{{ direction === 'include' ? '包含（任一匹配）' : '排除（优先级最高）' }}</h3>
      <p v-if="(direction === 'include' ? props.includeRules : props.excludeRules).length === 0">尚未添加规则</p>
      <div v-for="(rule, index) in (direction === 'include' ? props.includeRules : props.excludeRules)" :key="`${direction}-${index}`" class="rule-row">
        <select v-model="rule.patternKind" :aria-label="`${direction}-${index}-类型`"><option value="exact">精确</option><option value="contains">包含</option><option value="regex">正则</option></select>
        <input v-model="rule.pattern" :aria-label="`${direction}-${index}-模式`" placeholder="群名称或正则表达式" maxlength="1024">
        <label><input v-model="rule.ignoreCase" type="checkbox">忽略大小写</label>
        <button type="button" @click="emit('remove', direction, index)">删除</button>
      </div>
    </div>
  </section>
</template>
