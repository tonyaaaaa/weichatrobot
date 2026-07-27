<script setup lang="ts">
import { computed, ref } from 'vue';
import { RouterLink, RouterView } from 'vue-router';
import { ElButton } from 'element-plus';
import { getVisibleNavigation } from '../router';
import { useAuthStore } from '../stores/auth';
const auth = useAuthStore();
const navigation = computed(() => getVisibleNavigation(auth.user?.roles ?? []));
const navigationOpen = ref(false);
</script>

<template>
  <div class="admin-layout">
    <a class="skip-link" href="#main-content">跳到主要内容</a>
    <header><strong>NewsAgent · AI 群助手</strong><span>{{ auth.user?.displayName }}</span><ElButton class="nav-toggle" data-testid="navigation-toggle" aria-controls="admin-navigation" :aria-expanded="navigationOpen" @click="navigationOpen = !navigationOpen">{{ navigationOpen ? '收起导航' : '展开导航' }}</ElButton></header>
    <nav id="admin-navigation" :class="{ 'is-open': navigationOpen }" aria-label="后台导航"><RouterLink v-for="item in navigation" :key="item.name" :to="{ name: item.name }" @click="navigationOpen = false">{{ item.label }}</RouterLink></nav>
    <main id="main-content" tabindex="-1"><RouterView /></main>
  </div>
</template>

<style scoped>
.admin-layout { display: grid; grid-template-columns: 14rem minmax(0, 1fr); grid-template-rows: auto 1fr; min-height: 100dvh; }
header { grid-column: 1 / -1; display: flex; justify-content: space-between; gap: 1rem; padding: .75rem 1rem; color: var(--color-on-primary); background: var(--color-foreground); }
.nav-toggle { display: none; border-color: var(--color-secondary); color: var(--color-on-primary); background: transparent; }
.nav-toggle:hover:not(.is-disabled) { border-color: var(--color-on-primary); color: var(--color-on-primary); background: var(--color-accent-strong); }
.nav-toggle:focus-visible { outline: 3px solid var(--color-on-primary); outline-offset: 2px; box-shadow: 0 0 0 2px var(--color-accent); }
nav { display: grid; align-content: start; gap: .25rem; padding: .75rem; border-right: 1px solid var(--color-border); background: var(--color-surface); }
nav a { min-height: 44px; padding: .625rem .75rem; border-radius: .375rem; color: var(--color-foreground); text-decoration: none; transition: color 180ms ease, background-color 180ms ease; }
nav a:hover { background: var(--color-muted); }
nav a.router-link-active { color: var(--color-on-primary); background: var(--color-accent); }
main { display: grid; min-width: 0; align-content: start; gap: 1rem; padding: 1rem; }
@media (max-width: 760px) {
  .admin-layout { grid-template-columns: 1fr; }
  header { display: grid; grid-template-columns: 1fr auto; align-items: center; }
  header span { grid-column: 1 / -1; }
  .nav-toggle { display: inline-flex; align-items: center; justify-content: center; }
  nav { display: none; grid-template-columns: repeat(2, minmax(0, 1fr)); overflow: visible; border-right: 0; border-bottom: 1px solid var(--color-border); }
  nav.is-open { display: grid; }
  main { padding: .75rem; }
}
</style>
