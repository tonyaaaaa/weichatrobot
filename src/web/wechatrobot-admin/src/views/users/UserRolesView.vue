<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import { ElAlert, ElButton, ElEmpty, ElSkeleton, ElTag } from 'element-plus';
import {
  userAdministrationApi,
  type ManagedUser,
  type ManagedUserPage,
  type SystemRole,
  type UserAdministrationApi,
  type UserStateFilter
} from '../../api/users';
import { confirmAction as defaultConfirmAction } from '../../utils/dialogs';

const props = withDefaults(defineProps<{
  api?: UserAdministrationApi;
  confirmAction?: (message: string) => boolean | Promise<boolean>;
}>(), {
  api: () => userAdministrationApi,
  confirmAction: defaultConfirmAction
});

const filters = reactive<{
  q: string;
  state: UserStateFilter;
  page: number;
  pageSize: number;
}>({ q: '', state: 'all', page: 1, pageSize: 20 });
const page = ref<ManagedUserPage>({ items: [], total: 0, page: 1, pageSize: 20 });
const roleOptions = ref<SystemRole[]>([]);
const loading = ref(true);
const busy = ref('');
const error = ref('');
const notice = ref('');
const createOpen = ref(false);
const createEmail = ref('');
const createDisplayName = ref('');
const createPassword = ref('');
const createRoles = ref<SystemRole[]>([]);
const roleUser = ref<ManagedUser>();
const selectedRoles = ref<SystemRole[]>([]);
const totalPages = computed(() => Math.max(1, Math.ceil(page.value.total / page.value.pageSize)));

async function load(): Promise<void> {
  loading.value = true;
  error.value = '';
  try {
    const [users, roles] = await Promise.all([
      props.api.list({ ...filters }),
      roleOptions.value.length ? Promise.resolve(roleOptions.value) : props.api.roles()
    ]);
    page.value = users;
    roleOptions.value = roles;
  } catch {
    error.value = '用户列表加载失败，请检查管理员权限和后端服务。';
  } finally {
    loading.value = false;
  }
}

async function applyFilters(): Promise<void> {
  filters.page = 1;
  await load();
}

async function goToPage(value: number): Promise<void> {
  if (value < 1 || value > totalPages.value || value === filters.page) return;
  filters.page = value;
  await load();
}

function openCreate(): void {
  createEmail.value = '';
  createDisplayName.value = '';
  createPassword.value = '';
  createRoles.value = [];
  createOpen.value = true;
  error.value = '';
}

async function createUser(): Promise<void> {
  if (!createEmail.value.trim() || !createDisplayName.value.trim() || !createPassword.value) {
    error.value = '请输入邮箱、显示名称和临时密码。';
    return;
  }
  busy.value = 'create';
  error.value = '';
  try {
    await props.api.create({
      email: createEmail.value.trim(),
      displayName: createDisplayName.value.trim(),
      temporaryPassword: createPassword.value,
      roles: ordered(createRoles.value)
    });
    createPassword.value = '';
    createOpen.value = false;
    notice.value = '用户已创建。请通过安全渠道把临时密码交给本人。';
    await load();
  } catch (exception) {
    handleError(exception, '用户创建失败，请检查邮箱、密码策略和角色。');
  } finally {
    busy.value = '';
  }
}

async function toggle(user: ManagedUser): Promise<void> {
  const verb = user.isEnabled ? '停用' : '启用';
  const suffix = user.isEnabled ? '该账号现有登录令牌将失效。' : '该账号将可以重新登录。';
  if (!await props.confirmAction(`确认${verb}“${user.displayName}”？${suffix}`)) return;
  busy.value = `${user.id}:enabled`;
  error.value = '';
  try {
    await props.api.setEnabled(user.id, !user.isEnabled);
    notice.value = `${user.displayName} 已${verb}。`;
    await load();
  } catch (exception) {
    handleError(exception, `${user.displayName} 状态更新失败。`);
  } finally {
    busy.value = '';
  }
}

function openRoles(user: ManagedUser): void {
  roleUser.value = user;
  selectedRoles.value = [...user.roles];
  error.value = '';
}

async function saveRoles(): Promise<void> {
  if (!roleUser.value) return;
  busy.value = `${roleUser.value.id}:roles`;
  error.value = '';
  try {
    await props.api.setRoles(roleUser.value.id, ordered(selectedRoles.value));
    notice.value = `${roleUser.value.displayName} 的角色已更新。现有登录令牌已失效。`;
    roleUser.value = undefined;
    await load();
  } catch (exception) {
    handleError(exception, '角色更新失败。');
  } finally {
    busy.value = '';
  }
}


function handleError(exception: unknown, fallback: string): void {
  const data = (exception as {
    response?: { data?: { error?: string; errors?: string[] } };
  }).response?.data;
  switch (data?.error) {
    case 'last-enabled-admin':
      error.value = '系统必须保留至少一个已启用的管理员。';
      break;
    case 'unknown-role':
      error.value = '提交了系统不支持的角色，请刷新后重试。';
      break;
    case 'identity-validation':
      error.value = `账号创建不符合身份规则${data.errors?.length ? `：${data.errors.join('、')}` : '。'}`;
      break;
    default:
      error.value = fallback;
  }
}

function ordered(roles: SystemRole[]): SystemRole[] {
  return roleOptions.value.filter(role => roles.includes(role));
}

onMounted(load);
</script>

<template>
  <section class="ops-page" aria-labelledby="users-title">
    <header class="page-header">
      <div>
        <p class="eyebrow">身份与权限</p>
        <h1 id="users-title">用户与角色</h1>
        <p>管理后台登录账号、启停状态和系统权限。</p>
      </div>
      <div class="header-actions">
        <ElButton :loading="loading" @click="load">刷新</ElButton>
        <ElButton data-testid="create-user" type="primary" @click="openCreate">新增用户</ElButton>
      </div>
    </header>

    <ElAlert v-if="error" :title="error" type="error" :closable="false" show-icon />
    <ElAlert v-if="notice" :title="notice" type="success" :closable="false" show-icon />

    <section class="panel">
      <div class="filter-bar">
        <label>
          <span>用户</span>
          <input
            v-model="filters.q"
            data-testid="user-query-filter"
            type="search"
            placeholder="邮箱或显示名称"
            @keyup.enter="applyFilters"
          >
        </label>
        <label>
          <span>状态</span>
          <select
            v-model="filters.state"
            data-testid="user-state-filter"
            @change="applyFilters"
          >
            <option value="all">全部</option>
            <option value="enabled">已启用</option>
            <option value="disabled">已停用</option>
          </select>
        </label>
        <ElButton @click="applyFilters">查询</ElButton>
      </div>

      <ElSkeleton v-if="loading" :rows="6" animated />
      <ElEmpty v-else-if="page.items.length === 0" description="暂无符合条件的用户。" />
      <div v-else class="table-scroll">
        <table>
          <thead>
            <tr><th>用户</th><th>角色</th><th>状态</th><th>操作</th></tr>
          </thead>
          <tbody>
            <tr v-for="user in page.items" :key="user.id">
              <td><strong>{{ user.displayName }}</strong><small>{{ user.email }}</small></td>
              <td>
                <div class="role-tags">
                  <ElTag v-for="role in user.roles" :key="role" effect="plain">{{ role }}</ElTag>
                  <span v-if="user.roles.length === 0">未分配角色</span>
                </div>
              </td>
              <td><ElTag :type="user.isEnabled ? 'success' : 'info'">{{ user.isEnabled ? '已启用' : '已停用' }}</ElTag></td>
              <td>
                <div class="row-actions">
                  <ElButton :data-testid="`edit-roles-${user.id}`" @click="openRoles(user)">编辑角色</ElButton>
                  <ElButton
                    :data-testid="`toggle-user-${user.id}`"
                    :loading="busy === `${user.id}:enabled`"
                    @click="toggle(user)"
                  >{{ user.isEnabled ? '停用' : '启用' }}</ElButton>
                </div>
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <footer class="pagination-bar">
        <span>共 {{ page.total }} 条 · 第 {{ filters.page }} / {{ totalPages }} 页</span>
        <div>
          <ElButton data-testid="previous-page" :disabled="filters.page <= 1" @click="goToPage(filters.page - 1)">上一页</ElButton>
          <ElButton data-testid="next-page" :disabled="filters.page >= totalPages" @click="goToPage(filters.page + 1)">下一页</ElButton>
        </div>
      </footer>
    </section>

    <div v-if="createOpen" class="dialog-backdrop">
      <section class="dialog" role="dialog" aria-modal="true">
        <header><h2>新增后台用户</h2><button type="button" aria-label="关闭" @click="createOpen = false">×</button></header>
        <label><span>邮箱</span><input v-model="createEmail" data-testid="user-email" type="email" autocomplete="off"></label>
        <label><span>显示名称</span><input v-model="createDisplayName" data-testid="user-display-name" autocomplete="off"></label>
        <label><span>临时密码</span><input v-model="createPassword" data-testid="user-temporary-password" type="password" autocomplete="new-password"></label>
        <fieldset><legend>初始角色</legend>
          <label v-for="role in roleOptions" :key="role" class="checkbox-line">
            <input v-model="createRoles" :data-testid="`create-role-${role}`" type="checkbox" :value="role">
            <span>{{ role }}</span>
          </label>
        </fieldset>
        <footer><ElButton @click="createOpen = false">取消</ElButton><ElButton data-testid="save-user" type="primary" :loading="busy === 'create'" @click="createUser">创建</ElButton></footer>
      </section>
    </div>

    <div v-if="roleUser" class="dialog-backdrop">
      <section class="dialog" role="dialog" aria-modal="true">
        <header><h2>编辑 {{ roleUser.displayName }} 的角色</h2><button type="button" aria-label="关闭" @click="roleUser = undefined">×</button></header>
        <fieldset><legend>系统角色</legend>
          <label v-for="role in roleOptions" :key="role" class="checkbox-line">
            <input v-model="selectedRoles" :data-testid="`role-${role}`" type="checkbox" :value="role">
            <span>{{ role }}</span>
          </label>
        </fieldset>
        <ElAlert title="移除最后一个已启用管理员的 Admin 角色会被后端拒绝。" type="warning" :closable="false" />
        <footer><ElButton @click="roleUser = undefined">取消</ElButton><ElButton data-testid="save-roles" type="primary" :loading="busy === `${roleUser.id}:roles`" @click="saveRoles">保存角色</ElButton></footer>
      </section>
    </div>

  </section>
</template>

<style scoped>
.ops-page { display: grid; width: 100%; max-width: 1440px; margin: 0 auto; gap: var(--space-xl); }
.page-header, .header-actions, .row-actions, .pagination-bar, .pagination-bar > div, .role-tags, .dialog > header, .dialog > footer { display: flex; align-items: center; justify-content: space-between; gap: var(--space-sm); }
.page-header { align-items: flex-start; }
.page-header p, small { color: var(--color-muted-text); }
.panel, .dialog { padding: var(--space-xl); border: 1px solid var(--color-border); border-radius: .75rem; background: var(--color-surface); box-shadow: var(--shadow-sm); }
.filter-bar { display: grid; grid-template-columns: minmax(14rem, 1fr) minmax(10rem, auto) auto; align-items: end; gap: var(--space-md); margin-bottom: var(--space-lg); }
.filter-bar label, .dialog > label { display: grid; gap: var(--space-xs); }
.filter-bar input, .filter-bar select, .dialog input:not([type='checkbox']) { min-height: 44px; }
.table-scroll { overflow-x: auto; }
table { width: 100%; border-collapse: collapse; }
th, td { padding: var(--space-md); border-bottom: 1px solid var(--color-border); text-align: left; vertical-align: middle; }
td small { display: block; margin-top: .25rem; }
.role-tags, .row-actions { flex-wrap: wrap; justify-content: flex-start; }
.pagination-bar { margin-top: var(--space-lg); color: var(--color-muted-text); }
.dialog-backdrop { position: fixed; z-index: 100; inset: 0; display: grid; place-items: center; padding: var(--space-xl); background: rgb(15 23 42 / 45%); }
.dialog { display: grid; width: min(36rem, 100%); gap: var(--space-lg); }
.dialog h2 { margin: 0; }
.dialog header button { border: 0; background: transparent; font-size: 1.5rem; cursor: pointer; }
.dialog fieldset { display: grid; gap: var(--space-sm); padding: var(--space-md); border: 1px solid var(--color-border); border-radius: .5rem; }
.checkbox-line { display: flex; align-items: center; gap: var(--space-sm); }
@media (max-width: 720px) { .page-header, .pagination-bar { align-items: stretch; flex-direction: column; } .filter-bar { grid-template-columns: 1fr; } }
</style>
