<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { ElButton, ElEmpty, ElSkeleton, ElTable, ElTableColumn, ElTag } from 'element-plus';
import {
  workToolOperationsApi,
  type KnownGroup,
  type RemoteWorkToolGroup,
  type WorkToolRobotOption,
  type WorkToolOperationsApi
} from '../../api/worktool';

const props = withDefaults(
  defineProps<{ api?: Pick<WorkToolOperationsApi, 'listGroups'> &
    Partial<Pick<WorkToolOperationsApi,
      'listRobots' | 'listRemoteGroups' | 'importRemoteGroups'>> }>(),
  { api: () => workToolOperationsApi }
);
const groups = ref<KnownGroup[]>([]);
const loading = ref(true);
const loadError = ref('');
const robots = ref<WorkToolRobotOption[]>([]);
const selectedRobotId = ref('');
const remoteQuery = ref('');
const remoteGroups = ref<RemoteWorkToolGroup[]>([]);
const selectedRemoteNames = ref<string[]>([]);
const remoteBusy = ref(false);
const remoteError = ref('');

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

async function loadRobots() {
  if (!props.api.listRobots) return;
  robots.value = await props.api.listRobots();
  if (!selectedRobotId.value && robots.value.length > 0)
    selectedRobotId.value = robots.value[0].id;
}

async function loadRemoteGroups() {
  if (!props.api.listRemoteGroups || !selectedRobotId.value) return;
  remoteBusy.value = true;
  remoteError.value = '';
  selectedRemoteNames.value = [];
  try {
    remoteGroups.value = (await props.api.listRemoteGroups(selectedRobotId.value, {
      query: remoteQuery.value.trim() || undefined,
      page: 1,
      pageSize: 50
    })).items;
  } catch {
    remoteGroups.value = [];
    remoteError.value = '远程群读取失败，请检查机器人连接后重试。';
  } finally {
    remoteBusy.value = false;
  }
}

async function importRemoteGroups() {
  if (!props.api.importRemoteGroups || selectedRemoteNames.value.length === 0) return;
  remoteBusy.value = true;
  remoteError.value = '';
  try {
    await props.api.importRemoteGroups(
      selectedRobotId.value,
      selectedRemoteNames.value.map(groupName => ({
        groupName,
        expectedImportState: 'Available' as const
      }))
    );
    await Promise.all([load(), loadRemoteGroups()]);
  } catch {
    remoteError.value = '群导入失败，本地已有群不会被删除或停用。';
  } finally {
    remoteBusy.value = false;
  }
}

onMounted(async () => {
  await Promise.all([load(), loadRobots()]);
});
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

    <section v-if="api.listRemoteGroups" class="group-list-panel import-panel">
      <header>
        <div>
          <h2>从 WorkTool 导入</h2>
          <p>WorkTool 已将该群列表接口标记为将废弃；这里只用于发现并登记群名称，不代表企业微信实时成员目录。</p>
        </div>
      </header>
      <div class="import-toolbar">
        <label>
          <span>机器人</span>
          <select v-model="selectedRobotId" data-testid="remote-robot">
            <option v-for="robot in robots" :key="robot.id" :value="robot.id">
              {{ robot.name }}{{ robot.isEnabled ? '' : '（已停用）' }}
            </option>
          </select>
        </label>
        <label>
          <span>群名称</span>
          <input v-model="remoteQuery" type="search" placeholder="可选，按群名称查询">
        </label>
        <ElButton
          data-testid="load-remote-groups"
          :loading="remoteBusy"
          :disabled="!selectedRobotId"
          @click="loadRemoteGroups"
        >读取远程群</ElButton>
      </div>
      <p v-if="remoteError" class="remote-error">{{ remoteError }}</p>
      <div v-if="remoteGroups.length" class="group-table-wrap">
        <table class="remote-table">
          <thead><tr><th>选择</th><th>群名称</th><th>群主</th><th>成员数</th><th>状态</th></tr></thead>
          <tbody>
            <tr v-for="group in remoteGroups" :key="group.groupName">
              <td><input
                v-model="selectedRemoteNames"
                :data-testid="`select-remote-${group.groupName}`"
                type="checkbox"
                :value="group.groupName"
                :disabled="group.importState !== 'Available'"
              ></td>
              <td>{{ group.groupName }}</td>
              <td>{{ group.masterName || '—' }}</td>
              <td>{{ group.membersCount }}</td>
              <td><ElTag :type="group.importState === 'Available' ? 'success' : group.importState === 'Conflict' ? 'danger' : 'info'">
                {{ group.importState === 'Available' ? '可导入' : group.importState === 'Imported' ? '已登记' : '名称冲突' }}
              </ElTag></td>
            </tr>
          </tbody>
        </table>
      </div>
      <div class="import-actions">
        <ElButton
          data-testid="import-remote-groups"
          type="primary"
          :loading="remoteBusy"
          :disabled="selectedRemoteNames.length === 0"
          @click="importRemoteGroups"
        >导入所选群</ElButton>
      </div>
    </section>

    <section class="group-list-panel" aria-live="polite">
      <ElSkeleton v-if="loading" :rows="5" animated />

      <ElEmpty v-else-if="loadError" :description="loadError">
        <ElButton type="primary" @click="load">重新加载</ElButton>
      </ElEmpty>

      <ElEmpty v-else-if="groups.length === 0" description="暂无已登记群。">
        <RouterLink class="el-button el-button--primary text-link-button" :to="{ name: 'group-operations' }">
          前往群操作登记
        </RouterLink>
      </ElEmpty>

      <div v-else class="group-table-wrap">
        <ElTable :data="groups" style="width: 100%">
          <ElTableColumn label="群名称">
            <template #default="{ row }">
              <div data-testid="group-row" class="group-info">
                <strong>{{ row.name }}</strong>
                <small v-if="row.workToolGroupRemark" class="group-remark">{{ row.workToolGroupRemark }}</small>
              </div>
            </template>
          </ElTableColumn>

          <ElTableColumn prop="robotName" label="机器人" />

          <ElTableColumn label="状态">
            <template #default="{ row }">
              <ElTag :type="row.isEnabled ? 'success' : 'info'" size="small">
                {{ row.isEnabled ? '启用' : '停用' }}
              </ElTag>
            </template>
          </ElTableColumn>

          <ElTableColumn label="最后更新">
            <template #default="{ row }">
              {{ formatUpdatedAt(row.updatedAtUtc) }}
            </template>
          </ElTableColumn>

          <ElTableColumn align="right" width="100">
            <template #default="{ row }">
              <RouterLink
                class="configure-link"
                data-testid="configure-group"
                :to="{ name: 'group-configuration', params: { id: row.id } }"
              >
                配置
              </RouterLink>
            </template>
          </ElTableColumn>
        </ElTable>
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

.group-list-header p {
  margin-bottom: 0;
  color: var(--color-muted-text);
}

.group-operations-link {
  display: inline-flex;
  min-height: 40px;
  align-items: center;
  padding: .5rem 1rem;
  border: 1px solid var(--color-border);
  border-radius: .5rem;
  background: var(--color-surface);
  color: var(--color-accent);
  font-weight: 600;
  text-decoration: none;
  transition: all 180ms ease;
}

.group-operations-link:hover {
  border-color: var(--color-accent);
  background: var(--color-background);
  color: var(--color-accent-strong);
}

.group-list-panel {
  min-width: 0;
  padding: var(--space-xl);
  border: 1px solid var(--color-border);
  border-radius: .75rem;
  background: var(--color-surface);
  box-shadow: var(--shadow-sm);
}
.import-panel { display: grid; gap: var(--space-lg); }
.import-panel header p { margin-bottom: 0; color: var(--color-muted-text); }
.import-toolbar { display: grid; grid-template-columns: minmax(12rem, 1fr) minmax(14rem, 2fr) auto; align-items: end; gap: var(--space-md); }
.import-toolbar label { display: grid; gap: var(--space-xs); }
.import-toolbar input, .import-toolbar select { min-height: 44px; }
.remote-table { width: 100%; border-collapse: collapse; }
.remote-table th, .remote-table td { padding: var(--space-md); border-bottom: 1px solid var(--color-border); text-align: left; }
.import-actions { display: flex; justify-content: flex-end; }
.remote-error { color: var(--color-danger); }

.group-table-wrap {
  overflow-x: auto;
}

.group-info {
  display: flex;
  flex-direction: column;
  gap: var(--space-xs);
}

.group-info strong {
  color: var(--color-foreground);
  font-weight: 600;
}

.group-remark {
  color: var(--color-muted-text);
  font-size: 0.8125rem;
}

.configure-link {
  color: var(--color-accent);
  font-weight: 600;
  text-decoration: none;
  transition: color 180ms ease;
}

.configure-link:hover {
  color: var(--color-accent-strong);
  text-decoration: underline;
}

.text-link-button {
  text-decoration: none;
  font-weight: 600;
}

@media (max-width: 700px) {
  .group-list-header {
    flex-direction: column;
    gap: var(--space-md);
  }
  .group-list-panel {
    padding: var(--space-lg);
  }
  .import-toolbar { grid-template-columns: 1fr; }
}
</style>
