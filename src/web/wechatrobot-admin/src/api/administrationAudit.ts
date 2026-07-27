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

export interface AdministrationAuditTargetOption {
  targetType: string;
  targetId: string;
  label: string;
}

export interface AdministrationAuditFilterOptions {
  actors: string[];
  actions: string[];
  targetTypes: string[];
  targets: AdministrationAuditTargetOption[];
}

export interface AdministrationAuditApi {
  list(request: AdministrationAuditQuery): Promise<AdministrationAuditPage>;
  filterOptions(targetType?: string, q?: string): Promise<AdministrationAuditFilterOptions>;
}

export const administrationAuditApi: AdministrationAuditApi = {
  async filterOptions(targetType, q) {
    return (await apiClient.get<AdministrationAuditFilterOptions>(
      '/api/admin/administration-audits/filter-options',
      {
        params: {
          targetType: targetType?.trim() || undefined,
          q: q?.trim() || undefined
        }
      }
    )).data;
  },
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
