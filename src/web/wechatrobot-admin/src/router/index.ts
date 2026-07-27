import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router';
import { useAuthStore } from '../stores/auth';
import AdminLayout from '../layouts/AdminLayout.vue';
import DashboardView from '../views/DashboardView.vue';
import LoginView from '../views/LoginView.vue';
import GroupListView from '../views/groups/GroupListView.vue';
import GroupRulesView from '../views/groups/GroupRulesView.vue';
import GroupOperationsView from '../views/groups/GroupOperationsView.vue';

export type Role = 'Admin' | 'KnowledgeOperator' | 'HumanAgent';
export interface NavigationItem { name: string; label: string; roles: Role[]; }

export const navigation: NavigationItem[] = [
  { name: 'dashboard', label: '工作台', roles: ['Admin', 'KnowledgeOperator', 'HumanAgent'] },
  { name: 'knowledge-documents', label: '知识库', roles: ['Admin', 'KnowledgeOperator'] },
  { name: 'knowledge-tags', label: '知识库标签', roles: ['Admin', 'KnowledgeOperator'] },
  { name: 'knowledge-review', label: '知识审核', roles: ['Admin', 'KnowledgeOperator'] },
  { name: 'group-list', label: '群管理', roles: ['Admin'] },
  { name: 'handoffs', label: '人工转接', roles: ['Admin', 'HumanAgent'] },
  { name: 'audit', label: '会话审计', roles: ['Admin', 'KnowledgeOperator'] },
  { name: 'administration-audit', label: '管理审计', roles: ['Admin'] },
  { name: 'model-settings', label: '模型配置', roles: ['Admin'] },
  { name: 'robot-settings', label: '机器人设置', roles: ['Admin'] },
  { name: 'users', label: '用户与角色', roles: ['Admin'] },
  { name: 'system-settings', label: '系统设置', roles: ['Admin'] }
];

export function getVisibleNavigation(roles: string[]): NavigationItem[] {
  return navigation.filter(item => item.roles.some(role => roles.includes(role)));
}

export const routes: RouteRecordRaw[] = [
  { path: '/login', name: 'login', component: LoginView, meta: { public: true } },
  {
    path: '/', component: AdminLayout,
    children: [
      { path: '', name: 'dashboard', component: DashboardView, meta: { roles: ['Admin', 'KnowledgeOperator', 'HumanAgent'] } },
      { path: 'knowledge/documents', name: 'knowledge-documents', component: () => import('../views/knowledge/KnowledgeDocumentsView.vue'), meta: { roles: ['Admin', 'KnowledgeOperator'] } },
      { path: 'knowledge/documents/:documentId', name: 'knowledge-document-management', component: () => import('../views/knowledge/KnowledgeDocumentManagementView.vue'), props: true, meta: { roles: ['Admin', 'KnowledgeOperator'] } },
      { path: 'knowledge/documents/:documentId/versions/:versionId', name: 'knowledge-document-detail', component: () => import('../views/knowledge/DocumentDetailView.vue'), props: true, meta: { roles: ['Admin', 'KnowledgeOperator'] } },
      { path: 'knowledge/tags', name: 'knowledge-tags', component: () => import('../views/knowledge/KnowledgeTagsView.vue'), meta: { roles: ['Admin', 'KnowledgeOperator'] } },
      { path: 'knowledge/review', name: 'knowledge-review', component: () => import('../views/knowledge/KnowledgeReviewView.vue'), meta: { roles: ['Admin', 'KnowledgeOperator'] } },
      { path: 'groups', name: 'group-list', component: GroupListView, meta: { roles: ['Admin'] } },
      { path: 'groups/operations', name: 'group-operations', component: GroupOperationsView, meta: { roles: ['Admin'] } },
      { path: 'groups/:id/configuration', name: 'group-configuration', component: GroupRulesView, props: true, meta: { roles: ['Admin'] } },
      { path: 'handoffs', name: 'handoffs', component: () => import('../views/handoffs/HandoffQueueView.vue'), meta: { roles: ['Admin', 'HumanAgent'] } },
      { path: 'audit', name: 'audit', component: () => import('../views/audit/ConversationAuditView.vue'), meta: { roles: ['Admin', 'KnowledgeOperator'] } },
      { path: 'administration-audits', name: 'administration-audit', component: () => import('../views/audit/AdministrationAuditView.vue'), meta: { roles: ['Admin'] } },
      { path: 'models', name: 'model-settings', component: () => import('../views/models/ModelSettingsView.vue'), meta: { roles: ['Admin'] } },
      { path: 'robots', name: 'robot-settings', component: () => import('../views/settings/RobotSettingsView.vue'), meta: { roles: ['Admin'] } },
      { path: 'users', name: 'users', component: () => import('../views/users/UserRolesView.vue'), meta: { roles: ['Admin'] } },
      { path: 'settings', name: 'system-settings', component: () => import('../views/settings/SystemSettingsView.vue'), meta: { roles: ['Admin'] } }
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
