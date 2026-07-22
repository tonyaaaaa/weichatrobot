<script setup lang="ts">
defineProps<{ results: { groupName: string; isMatch: boolean; isExcluded: boolean }[] }>();
</script>

<template>
  <section aria-live="polite">
    <h2>规则预览</h2>
    <p v-if="results.length === 0">输入已知群名称后可在保存前预览。</p>
    <ul v-else><li v-for="result in results" :key="result.groupName">{{ result.groupName }}：{{ result.isExcluded ? '已排除' : result.isMatch ? '将匹配' : '不匹配' }}</li></ul>
  </section>
</template>
