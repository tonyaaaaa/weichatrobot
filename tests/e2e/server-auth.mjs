export const users = {
  'admin@e2e.local': { password: 'Safe-E2E-Admin-1!', roles: ['Admin'], displayName: 'E2E 管理员' },
  'knowledge@e2e.local': { password: 'Safe-E2E-Knowledge-1!', roles: ['KnowledgeOperator'], displayName: 'E2E 知识运营' },
  'human@e2e.local': { password: 'Safe-E2E-Human-1!', roles: ['HumanAgent'], displayName: 'E2E 人工客服' }
};

export function userFromAuthorization(authorization) {
  const token = authorization?.replace(/^Bearer /, '');
  const email = token?.replace(/^e2e-token:/, '');
  return email ? users[email] : undefined;
}

export function userFromRequest(request) {
  return userFromAuthorization(request.headers.authorization);
}

export function requiredRoles(pathname) {
  if (pathname.startsWith('/api/admin/') || pathname.startsWith('/api/groups/') || pathname === '/api/group-rules/preview') return ['Admin'];
  if (pathname.startsWith('/api/knowledge/') || pathname.startsWith('/api/audit/')) return ['Admin', 'KnowledgeOperator'];
  if (pathname.startsWith('/api/handoffs/')) return ['Admin', 'HumanAgent'];
  return [];
}
