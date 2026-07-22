import { apiClient } from './http';

export interface KnownGroup { id: string; robotConfigId: string; externalGroupId: string; name: string; }
export interface GroupOperation { robotConfigId: string; kind: 'Create' | 'AddMembers' | 'RemoveMembers' | 'Rename' | 'UpdateAnnouncement'; groupIdentifier: string; memberIds: string[]; value?: string; }
export interface WorkToolOperationsApi {
  listGroups(): Promise<KnownGroup[]>;
  registerExistingGroup(request: { robotConfigId: string; externalGroupId: string; name: string; manualInvitationCompleted: boolean }): Promise<KnownGroup>;
  preview(operation: GroupOperation): Promise<{ sanitizedRequest: string; confirmationToken: string; expiresAtUtc: string }>;
  execute(operation: GroupOperation, confirmationToken: string): Promise<{ succeeded: boolean; message: string }>;
  listOperations(): Promise<{ id: string; operation: string; workToolCommandNumber: number; status: string; result?: string; createdAtUtc: string }[]>;
}
export const workToolOperationsApi: WorkToolOperationsApi = {
  async listGroups() { return (await apiClient.get<KnownGroup[]>('/api/admin/worktool/groups')).data; },
  async registerExistingGroup(request) { return (await apiClient.post<KnownGroup>('/api/admin/worktool/groups/register', request)).data; },
  async preview(operation) { return (await apiClient.post<{ sanitizedRequest: string; confirmationToken: string; expiresAtUtc: string }>('/api/admin/worktool/group-operations/preview', operation)).data; },
  async execute(operation, confirmationToken) { return (await apiClient.post<{ succeeded: boolean; message: string }>('/api/admin/worktool/group-operations/execute', { operation, confirmationToken })).data; },
  async listOperations() { return (await apiClient.get<{ id: string; operation: string; workToolCommandNumber: number; status: string; result?: string; createdAtUtc: string }[]>('/api/admin/worktool/group-operations')).data; }
};
