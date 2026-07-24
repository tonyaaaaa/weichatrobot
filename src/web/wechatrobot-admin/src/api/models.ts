import { apiClient } from './http';

export type ModelConfigurationType = 'chat' | 'embedding';
export type ModelConnectionStatus = 'Untested' | 'Succeeded' | 'Failed';

export interface ModelConfiguration {
  id: string;
  name: string;
  provider: string;
  configurationType: ModelConfigurationType;
  baseUrl: string;
  model: string;
  timeoutSeconds: number;
  maxRetries: number;
  isEnabled: boolean;
  isDefault: boolean;
  connectionStatus: ModelConnectionStatus;
  lastTestedAtUtc?: string;
  lastTestFailureSummary?: string;
  hasApiKey: boolean;
  lastFour?: string;
  version: number;
}

export interface ModelConfigurationDraft {
  name: string;
  provider: string;
  configurationType: ModelConfigurationType;
  baseUrl: string;
  model: string;
  apiKey?: string;
  timeoutSeconds: number;
  maxRetries: number;
  version?: number;
}

export interface ModelConfigurationApiError {
  code?: string;
  message?: string;
  retrievalAuditCount?: number;
  errors?: Record<string, string[]>;
}

export interface ModelApi {
  list(): Promise<ModelConfiguration[]>;
  create(value: ModelConfigurationDraft): Promise<ModelConfiguration>;
  update(id: string, value: ModelConfigurationDraft): Promise<ModelConfiguration>;
  testConnection(id: string): Promise<ModelConfiguration>;
  setEnabled(id: string, enabled: boolean, version: number): Promise<ModelConfiguration>;
  setDefault(id: string, isDefault: boolean, version: number): Promise<ModelConfiguration>;
  clearApiKey(id: string, version: number): Promise<ModelConfiguration>;
  delete(id: string, version: number): Promise<void>;
}

export const modelApi: ModelApi = {
  async list() {
    return (await apiClient.get<ModelConfiguration[]>('/api/admin/model-configurations')).data;
  },
  async create(value) {
    return (await apiClient.post<ModelConfiguration>('/api/admin/model-configurations', value)).data;
  },
  async update(id, value) {
    return (await apiClient.put<ModelConfiguration>(
      `/api/admin/model-configurations/${encodeURIComponent(id)}`,
      value
    )).data;
  },
  async testConnection(id) {
    return (await apiClient.post<ModelConfiguration>(
      `/api/admin/model-configurations/${encodeURIComponent(id)}/test-connection`
    )).data;
  },
  async setEnabled(id, enabled, version) {
    return (await apiClient.post<ModelConfiguration>(
      `/api/admin/model-configurations/${encodeURIComponent(id)}/enabled`,
      { enabled, version }
    )).data;
  },
  async setDefault(id, isDefault, version) {
    return (await apiClient.post<ModelConfiguration>(
      `/api/admin/model-configurations/${encodeURIComponent(id)}/default`,
      { isDefault, version }
    )).data;
  },
  async clearApiKey(id, version) {
    return (await apiClient.delete<ModelConfiguration>(
      `/api/admin/model-configurations/${encodeURIComponent(id)}/api-key`,
      { params: { version } }
    )).data;
  },
  async delete(id, version) {
    await apiClient.delete(
      `/api/admin/model-configurations/${encodeURIComponent(id)}`,
      { params: { version } }
    );
  }
};
