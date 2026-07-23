import { apiClient } from './http';

export interface AuditApi {
  capability(page?: number, pageSize?: number): Promise<AuditPage>;
}

export interface AuditPage {
  available: boolean;
  message?: string;
  items: Array<Record<string, unknown>>;
  total: number;
  page: number;
  pageSize: number;
}

export const auditApi: AuditApi = {
  async capability(page = 1, pageSize = 20) {
    const response = await apiClient.get<Omit<AuditPage, 'available'>>('/api/audit/conversations', { params: { page, pageSize } });
    return { available: true, ...response.data };
  }
};
