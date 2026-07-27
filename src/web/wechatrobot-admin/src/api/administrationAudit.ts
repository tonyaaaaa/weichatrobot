import { apiClient } from './http';

export interface AdministrationAuditItem {
  id: string;
  actor: string;
  action: string;
  targetType: string;
  targetId: string;
  detail: unknown;
  createdAtUtc: string;
}

export interface AdministrationAuditPage {
  items: AdministrationAuditItem[];
  total: number;
  page: number;
  pageSize: number;
}

export interface AdministrationAuditQuery {
  actor?: string;
  action?: string;
  targetType?: string;
  targetId?: string;
  fromUtc?: string;
  toUtc?: string;
  page: number;
  pageSize: number;
}

export interface AdministrationAuditApi {
  list(request: AdministrationAuditQuery): Promise<AdministrationAuditPage>;
}

export const administrationAuditApi: AdministrationAuditApi = {
  async list(request) {
    return (await apiClient.get<AdministrationAuditPage>(
      '/api/admin/administration-audits',
      {
        params: {
          actor: request.actor?.trim() || undefined,
          action: request.action?.trim() || undefined,
          targetType: request.targetType?.trim() || undefined,
          targetId: request.targetId?.trim() || undefined,
          fromUtc: request.fromUtc || undefined,
          toUtc: request.toUtc || undefined,
          page: request.page,
          pageSize: request.pageSize
        }
      }
    )).data;
  }
};
