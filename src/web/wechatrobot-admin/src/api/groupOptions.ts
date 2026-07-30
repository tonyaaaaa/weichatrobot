import { apiClient } from './http';

export interface GroupOption {
  id: string;
  name: string;
  workToolGroupRemark?: string | null;
  robotName: string;
  state: 'enabled' | 'disabled' | 'archived';
  isEnabled: boolean;
}

export interface GroupOptionApi {
  list(): Promise<GroupOption[]>;
}

export const groupOptionApi: GroupOptionApi = {
  async list() {
    return (await apiClient.get<GroupOption[]>('/api/group-options')).data;
  }
};
