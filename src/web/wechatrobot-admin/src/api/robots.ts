import { apiClient } from './http';

export interface RobotSettings {
  id: string;
  name: string;
  isEnabled: boolean;
  sendRateLimitPerMinute: number;
  updatedAtUtc: string;
}

export const robotApi = {
  async list(): Promise<RobotSettings[]> {
    return (await apiClient.get<RobotSettings[]>('/api/admin/robots/')).data;
  },
  async save(item: RobotSettings): Promise<RobotSettings> {
    return (await apiClient.put<RobotSettings>(`/api/admin/robots/${item.id}`, {
      name: item.name,
      isEnabled: item.isEnabled,
      sendRateLimitPerMinute: item.sendRateLimitPerMinute
    })).data;
  }
};
