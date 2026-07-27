import { apiClient } from './http';

export interface AuditApi {
  capability(request?: AuditQuery): Promise<AuditPage>;
  groupOptions(): Promise<AuditGroupOption[]>;
}

export interface AuditGroupOption {
  id: string;
  name: string;
  workToolGroupRemark?: string | null;
  robotName: string;
  isEnabled: boolean;
}

export interface AuditQuery {
  groupId?: string;
  fromUtc?: string;
  toUtc?: string;
  page?: number;
  pageSize?: number;
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
  async capability(request = {}) {
    const response = await apiClient.get<Omit<AuditPage, 'available'>>('/api/audit/conversations', {
      params: {
        groupId: request.groupId || undefined,
        fromUtc: request.fromUtc || undefined,
        toUtc: request.toUtc || undefined,
        page: request.page ?? 1,
        pageSize: request.pageSize ?? 20
      }
    });
    return { available: true, ...response.data };
  },
  async groupOptions() {
    return (await apiClient.get<AuditGroupOption[]>('/api/audit/group-options')).data;
  }
};
