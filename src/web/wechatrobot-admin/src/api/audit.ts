import { apiClient } from './http';

export interface AuditApi {
  capability(request?: AuditQuery): Promise<AuditPage>;
  groupOptions(): Promise<AuditGroupOption[]>;
  createKnowledgeCandidate(auditId: string, answer: string): Promise<{ id: string; status: string; version: number }>;
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
  items: AuditItem[];
  total: number;
  page: number;
  pageSize: number;
}

export interface AuditWebSource {
  title: string;
  url: string;
  site?: string | null;
  publishedAt?: string | null;
  index: number;
}

export interface AuditItem extends Record<string, unknown> {
  id: string;
  groupProfileId: string;
  modelConfigurationId?: string | null;
  question: string;
  answer?: string | null;
  answerSource: 'knowledge' | 'web_search' | 'model_knowledge' | 'insufficient' | 'clarification' | 'system_failure' | 'none';
  webSearchFailureCode?: string | null;
  webSearchSources: AuditWebSource[];
  sources: string[];
  createdAtUtc: string;
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
  },
  async createKnowledgeCandidate(auditId, answer) {
    return (await apiClient.post<{ id: string; status: string; version: number }>(
      `/api/audit/conversations/${encodeURIComponent(auditId)}/knowledge-candidate`,
      { answer }
    )).data;
  }
};
