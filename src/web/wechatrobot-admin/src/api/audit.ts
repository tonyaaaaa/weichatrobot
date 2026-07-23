import { apiClient } from './http';

export interface AuditApi {
  capability(): Promise<{ available: boolean; message?: string; items: Array<Record<string, unknown>> }>;
}

export const auditApi: AuditApi = {
  async capability() {
    const response = await apiClient.get<{ items: Array<Record<string, unknown>> }>('/api/audit/conversations');
    return { available: true, items: response.data.items };
  }
};
