<script setup lang="ts">
import { computed } from 'vue';
import { RouterLink, RouterView } from 'vue-router';
import PublicOssWarning from '../components/PublicOssWarning.vue';
import { getVisibleNavigation } from '../router';
import { useAuthStore } from '../stores/auth';
const auth = useAuthStore();
const navigation = computed(() => getVisibleNavigation(auth.user?.roles ?? []));
</script>

<template>
  <div class="admin-layout">
    <header><strong>NewsAgent</strong><span>{{ auth.user?.displayName }}</span></header>
    <nav aria-label="后台导航"><RouterLink v-for="item in navigation" :key="item.name" :to="{ name: item.name }">{{ item.label }}</RouterLink></nav>
    <main><PublicOssWarning /><RouterView /></main>
  </div>
</template>

<style scoped>
.admin-layout { display: grid; grid-template-columns: 14rem 1fr; grid-template-rows: auto 1fr; min-height: 100vh; }
header { grid-column: 1 / -1; display: flex; justify-content: space-between; padding: 1rem 1.5rem; color: #fff; background: #172554; }
nav { display: grid; align-content: start; gap: .25rem; padding: 1rem; background: #eff6ff; }nav a { padding: .625rem .75rem; border-radius: .375rem; color: #1e3a8a; text-decoration: none; }nav a.router-link-active { color: #fff; background: #2563eb; }
main { display: grid; align-content: start; gap: 1.5rem; padding: 1.5rem; }@media (max-width: 760px) { .admin-layout { grid-template-columns: 1fr; } nav { grid-auto-flow: column; grid-auto-columns: max-content; overflow-x: auto; } }
</style>
