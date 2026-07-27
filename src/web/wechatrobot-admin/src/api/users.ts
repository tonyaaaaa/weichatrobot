import { apiClient } from './http';

export type SystemRole = 'Admin' | 'KnowledgeOperator' | 'HumanAgent';
export type UserStateFilter = 'all' | 'enabled' | 'disabled';

export interface ManagedUser {
  id: string;
  email: string;
  displayName: string;
  isEnabled: boolean;
  workToolDisplayName?: string | null;
  roles: SystemRole[];
}

export interface ManagedUserPage {
  items: ManagedUser[];
  total: number;
  page: number;
  pageSize: number;
}

export interface CreateManagedUserRequest {
  email: string;
  displayName: string;
  temporaryPassword: string;
  roles: SystemRole[];
}

export interface UserAdministrationApi {
  list(params: {
    q?: string;
    state?: UserStateFilter;
    page: number;
    pageSize: number;
  }): Promise<ManagedUserPage>;
  roles(): Promise<SystemRole[]>;
  create(request: CreateManagedUserRequest): Promise<ManagedUser>;
  setEnabled(id: string, isEnabled: boolean): Promise<ManagedUser>;
  setRoles(id: string, roles: SystemRole[]): Promise<ManagedUser>;
  setWorkToolDisplayName(id: string, displayName: string): Promise<ManagedUser>;
  clearWorkToolDisplayName(id: string): Promise<ManagedUser>;
}

export const userAdministrationApi: UserAdministrationApi = {
  async list(params) {
    return (await apiClient.get<ManagedUserPage>('/api/admin/users', {
      params: {
        q: params.q?.trim() || undefined,
        state: params.state ?? 'all',
        page: params.page,
        pageSize: params.pageSize
      }
    })).data;
  },
  async roles() {
    return (await apiClient.get<SystemRole[]>('/api/admin/users/roles')).data;
  },
  async create(request) {
    return (await apiClient.post<ManagedUser>('/api/admin/users', request)).data;
  },
  async setEnabled(id, isEnabled) {
    return (await apiClient.put<ManagedUser>(
      `/api/admin/users/${encodeURIComponent(id)}/enabled`,
      { isEnabled }
    )).data;
  },
  async setRoles(id, roles) {
    return (await apiClient.put<ManagedUser>(
      `/api/admin/users/${encodeURIComponent(id)}/roles`,
      { roles }
    )).data;
  },
  async setWorkToolDisplayName(id, displayName) {
    return (await apiClient.put<ManagedUser>(
      `/api/admin/users/${encodeURIComponent(id)}/worktool-display-name`,
      { displayName }
    )).data;
  },
  async clearWorkToolDisplayName(id) {
    return (await apiClient.delete<ManagedUser>(
      `/api/admin/users/${encodeURIComponent(id)}/worktool-display-name`
    )).data;
  }
};
