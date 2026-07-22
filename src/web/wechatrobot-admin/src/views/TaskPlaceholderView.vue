<script setup lang="ts">defineProps<{ ownerTask: string; title: string }>();</script>
<template><section class="placeholder" :data-task-owner="ownerTask"><h1>{{ title }}</h1><p>此页面由 {{ ownerTask }} 实现；Task 16 将移除此占位页面。</p></section></template>
