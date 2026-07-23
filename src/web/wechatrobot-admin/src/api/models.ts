import { apiClient } from './http';

export interface ModelConfiguration {
  id: string; name: string; provider: string; configurationType: string; baseUrl: string; model: string;
  timeoutSeconds: number; maxRetries: number; isEnabled: boolean; isDefault: boolean; hasApiKey: boolean; lastFour?: string;
}
export interface ModelApi {
  list(): Promise<ModelConfiguration[]>;
  save(name: string, value: Omit<ModelConfiguration, 'id' | 'hasApiKey' | 'lastFour'> & { apiKey?: string }): Promise<ModelConfiguration>;
  testConnection(name: string): Promise<{ succeeded: boolean }>;
}
export const modelApi: ModelApi = {
  async list() { return (await apiClient.get('/api/admin/model-configurations')).data; },
  async save(name, value) { return (await apiClient.put(`/api/admin/model-configurations/${encodeURIComponent(name)}`, value)).data; },
  async testConnection(name) { return (await apiClient.post(`/api/admin/model-configurations/${encodeURIComponent(name)}/test-connection`)).data; }
};
