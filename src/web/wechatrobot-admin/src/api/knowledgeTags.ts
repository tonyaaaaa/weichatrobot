import { apiClient } from './http';

export interface KnowledgeTag {
  id: string;
  name: string;
  isEnabled: boolean;
  isGlobalPublic: boolean;
  version: number;
  createdAtUtc: string;
}

export interface KnowledgeTagOption {
  id: string;
  name: string;
  isGlobalPublic: boolean;
}

export interface KnowledgeTagPage {
  items: KnowledgeTag[];
  total: number;
  page: number;
  pageSize: number;
}

export interface KnowledgeTagApi {
  list(params: {
    q?: string;
    state?: 'all' | 'enabled' | 'disabled';
    global?: 'all' | 'global' | 'scoped';
    page: number;
    pageSize: number;
  }): Promise<KnowledgeTagPage>;
  options(): Promise<KnowledgeTagOption[]>;
  create(request: {
    name: string;
    isGlobalPublic: boolean;
  }): Promise<KnowledgeTag>;
  update(id: string, request: {
    name: string;
    isGlobalPublic: boolean;
    expectedVersion: number;
  }): Promise<KnowledgeTag>;
  setEnabled(id: string, request: {
    isEnabled: boolean;
    expectedVersion: number;
  }): Promise<KnowledgeTag>;
  delete(id: string, expectedVersion: number): Promise<void>;
}

export const knowledgeTagApi: KnowledgeTagApi = {
  async list(params) {
    return (await apiClient.get<KnowledgeTagPage>('/api/knowledge/tags', {
      params: {
        query: params.q?.trim() || undefined,
        isEnabled: params.state === 'all' || params.state === undefined
          ? undefined
          : params.state === 'enabled',
        isGlobalPublic: params.global === 'all' || params.global === undefined
          ? undefined
          : params.global === 'global',
        page: params.page,
        pageSize: params.pageSize
      }
    })).data;
  },
  async options() {
    return (await apiClient.get<KnowledgeTagOption[]>('/api/knowledge/tags/options')).data;
  },
  async create(request) {
    return (await apiClient.post<KnowledgeTag>('/api/knowledge/tags', request)).data;
  },
  async update(id, request) {
    return (await apiClient.put<KnowledgeTag>(
      `/api/knowledge/tags/${encodeURIComponent(id)}`,
      request
    )).data;
  },
  async setEnabled(id, request) {
    return (await apiClient.patch<KnowledgeTag>(
      `/api/knowledge/tags/${encodeURIComponent(id)}/enabled`,
      request
    )).data;
  },
  async delete(id, expectedVersion) {
    await apiClient.delete(
      `/api/knowledge/tags/${encodeURIComponent(id)}`,
      { params: { expectedVersion } }
    );
  }
};
