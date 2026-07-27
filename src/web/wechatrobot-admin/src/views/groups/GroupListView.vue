<script setup lang="ts">
import { onMounted, ref } from 'vue';
import {
  workToolOperationsApi,
  type KnownGroup,
  type WorkToolOperationsApi
} from '../../api/worktool';

const props = withDefaults(
  defineProps<{ api?: Pick<WorkToolOperationsApi, 'listGroups'> }>(),
  { api: () => workToolOperationsApi }
);
const groups = ref<KnownGroup[]>([]);
const loading = ref(true);
const loadError = ref('');

function formatUpdatedAt(value: string) {
  return new Date(value).toLocaleString('zh-CN');
}

async function load() {
  loading.value = true;
  loadError.value = '';
  groups.value = [];
  try {
    groups.value = await props.api.listGroups();
  } catch {
    loadError.value = '群列表加载失败，请稍后重试。';
  } finally {
    loading.value = false;
  }
}

onMounted(load);
</script>

<template>
  <section class="group-list-view" aria-labelledby="group-list-title">
    <header class="group-list-header">
      <div>
        <p class="eyebrow">群管理</p>
        <h1 id="group-list-title">已登记群</h1>
        <p>选择一个群进入配置；内部配置 ID 由系统生成和维护。</p>
      </div>
      <RouterLink class="group-operations-link" :to="{ name: 'group-operations' }">群操作</RouterLink>
    </header>

    <section class="group-list-panel" aria-live="polite">
      <p v-if="loading">正在加载群列表…</p>
      <div v-else-if="loadError" class="group-list-state error-state">
        <p>{{ loadError }}</p>
        <button type="button" @click="load">重新加载</button>
      </div>
      <div v-else-if="groups.length === 0" class="group-list-state">
        <p>暂无已登记群。</p>
        <RouterLink :to="{ name: 'group-operations' }">前往群操作登记</RouterLink>
      </div>
      <div v-else class="group-table-wrap">
        <table>
          <thead>
            <tr>
              <th scope="col">群名称</th>
              <th scope="col">机器人</th>
              <th scope="col">状态</th>
              <th scope="col">最后更新</th>
              <th scope="col"><span class="visually-hidden">操作</span></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="group in groups" :key="group.id" data-testid="group-row">
              <td>
                <strong>{{ group.name }}</strong>
                <small v-if="group.workToolGroupRemark">{{ group.workToolGroupRemark }}</small>
              </td>
              <td>{{ group.robotName }}</td>
              <td><span :class="['status-badge', { disabled: !group.isEnabled }]">{{ group.isEnabled ? '启用' : '停用' }}</span></td>
              <td>{{ formatUpdatedAt(group.updatedAtUtc) }}</td>
              <td>
                <RouterLink
                  data-testid="configure-group"
                  :to="{ name: 'group-configuration', params: { id: group.id } }"
                >配置</RouterLink>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>
  </section>
</template>

<style scoped>
.group-list-view {
  display: grid;
  width: 100%;
  max-width: 1440px;
  margin: 0 auto;
  gap: var(--space-xl);
}
.group-list-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--space-xl);
}
.group-list-header p { margin-bottom: 0; color: var(--color-muted-text); }
.group-operations-link {
  display: inline-flex;
  min-height: 44px;
  align-items: center;
  padding: .55rem .85rem;
  border: 1px solid var(--color-border);
  border-radius: .5rem;
  background: var(--color-surface);
  color: var(--color-accent-strong);
  font-weight: 600;
  text-decoration: none;
}
.group-list-panel {
  min-width: 0;
  padding: var(--space-xl);
  border: 1px solid var(--color-border);
  border-radius: .75rem;
  background: var(--color-surface);
  box-shadow: var(--shadow-sm);
}
.group-table-wrap { overflow-x: auto; }
table { width: 100%; border-collapse: collapse; }
th, td {
  padding: var(--space-md);
  border-bottom: 1px solid var(--color-border);
  text-align: left;
  vertical-align: middle;
}
td:first-child strong, td:first-child small { display: block; }
td:first-child small { margin-top: .2rem; color: var(--color-muted-text); }
td:last-child { text-align: right; }
.status-badge {
  display: inline-flex;
  padding: .2rem .55rem;
  border-radius: 999px;
  background: color-mix(in srgb, var(--color-success) 15%, transparent);
  color: var(--color-success);
  font-weight: 600;
}
.status-badge.disabled {
  background: var(--color-background);
  color: var(--color-muted-text);
}
.group-list-state {
  display: grid;
  justify-items: start;
  gap: var(--space-md);
  padding: var(--space-xl);
  border-radius: .5rem;
  background: var(--color-background);
}
.group-list-state p { margin: 0; }
.error-state { color: var(--color-danger); }
.visually-hidden {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border: 0;
}
@media (max-width: 700px) {
  .group-list-header { flex-direction: column; }
  .group-list-panel { padding: var(--space-lg); }
}
</style>
