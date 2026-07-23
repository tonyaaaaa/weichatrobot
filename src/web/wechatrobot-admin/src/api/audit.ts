export interface AuditApi {
  capability(): Promise<{ available: boolean; message?: string; items: Array<Record<string, unknown>> }>;
}

// Task 15 persists evidence, but no authorized conversation-audit read endpoint exists yet.
// Keep this capability explicit instead of presenting mock records as backend data.
export const auditApi: AuditApi = {
  async capability() {
    return { available: false, message: '后端暂未提供会话审计查询 API；当前页面不会伪造审计数据。', items: [] };
  }
};
