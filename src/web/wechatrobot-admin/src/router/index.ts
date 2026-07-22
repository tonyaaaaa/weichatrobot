import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router';
import { useAuthStore } from '../stores/auth';
import AdminLayout from '../layouts/AdminLayout.vue';
import DashboardView from '../views/DashboardView.vue';
import LoginView from '../views/LoginView.vue';
import TaskPlaceholderView from '../views/TaskPlaceholderView.vue';

export type Role = 'Admin' | 'KnowledgeOperator' | 'HumanAgent';
export interface NavigationItem { name: string; label: string; roles: Role[]; }

export const navigation: NavigationItem[] = [
  { name: 'dashboard', label: '工作台', roles: ['Admin', 'KnowledgeOperator', 'HumanAgent'] },
  { name: 'knowledge-documents', label: '知识库', roles: ['Admin', 'KnowledgeOperator'] },
  { name: 'knowledge-tags', label: '知识库标签', roles: ['Admin', 'KnowledgeOperator'] },
  { name: 'group-rules', label: '群管理', roles: ['Admin'] },
  { name: 'handoffs', label: '人工转接', roles: ['Admin', 'HumanAgent'] },
  { name: 'audit', label: '会话审计', roles: ['Admin', 'KnowledgeOperator'] },
  { name: 'model-settings', label: '模型配置', roles: ['Admin'] },
  { name: 'users', label: '用户与角色', roles: ['Admin'] },
  { name: 'system-settings', label: '系统设置', roles: ['Admin'] }
];

export function getVisibleNavigation(roles: string[]): NavigationItem[] {
  return navigation.filter(item => item.roles.some(role => roles.includes(role)));
}

function placeholder(name: string, path: string, label: string, roles: Role[], ownerTask: string): RouteRecordRaw {
  return { name, path, component: TaskPlaceholderView, props: { title: label, ownerTask }, meta: { roles } };
}

const routes: RouteRecordRaw[] = [
  { path: '/login', name: 'login', component: LoginView, meta: { public: true } },
  {
    path: '/', component: AdminLayout,
    children: [
      { path: '', name: 'dashboard', component: DashboardView, meta: { roles: ['Admin', 'KnowledgeOperator', 'HumanAgent'] } },
      placeholder('knowledge-documents', 'knowledge/documents', '知识库', ['Admin', 'KnowledgeOperator'], 'Task 16'),
      placeholder('knowledge-tags', 'knowledge/tags', '知识库标签', ['Admin', 'KnowledgeOperator'], 'Task 16'),
      placeholder('group-rules', 'groups', '群管理', ['Admin'], 'Task 8'),
      placeholder('handoffs', 'handoffs', '人工转接', ['Admin', 'HumanAgent'], 'Task 16'),
      placeholder('audit', 'audit', '会话审计', ['Admin', 'KnowledgeOperator'], 'Task 16'),
      placeholder('model-settings', 'models', '模型配置', ['Admin'], 'Task 16'),
      placeholder('users', 'users', '用户与角色', ['Admin'], 'Task 16'),
      placeholder('system-settings', 'settings', '系统设置', ['Admin'], 'Task 16')
    ]
  }
];

export const router = createRouter({ history: createWebHistory(), routes });
router.beforeEach(to => {
  if (to.meta.public) return true;
  const auth = useAuthStore();
  if (!auth.isAuthenticated) return { name: 'login' };
  const roles = to.meta.roles as Role[] | undefined;
  return !roles || roles.some(role => auth.user?.roles.includes(role)) ? true : { name: 'dashboard' };
});
