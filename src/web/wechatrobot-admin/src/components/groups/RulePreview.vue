<script setup lang="ts">
defineProps<{ results: { groupName: string; isMatch: boolean; isExcluded: boolean }[] }>();
</script>

<template>
  <div class="rule-preview" aria-live="polite">
    <p v-if="results.length === 0">输入已知群名称后可在保存前预览。</p>
    <ul v-else><li v-for="result in results" :key="result.groupName">{{ result.groupName }}：{{ result.isExcluded ? '已排除' : result.isMatch ? '将匹配' : '不匹配' }}</li></ul>
  </div>
</template>

<style scoped>
.rule-preview {
  margin-top: var(--space-lg);
  padding: var(--space-lg);
  border-radius: .5rem;
  background: var(--color-background);
}
.rule-preview p,
.rule-preview ul { margin-bottom: 0; }
.rule-preview ul { padding-left: 1.25rem; }
</style>
