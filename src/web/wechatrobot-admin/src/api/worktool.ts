import { apiClient } from './http';

export interface KnownGroup {
  id: string;
  robotConfigId: string;
  robotName: string;
  name: string;
  workToolGroupRemark?: string;
  isEnabled: boolean;
  archivedAtUtc?: string | null;
  state: 'enabled' | 'disabled' | 'archived';
  stateVersion: number;
  configurationVersion: number;
  updatedAtUtc: string;
}
export interface WorkToolRobotOption {
  id: string;
  name: string;
  isEnabled: boolean;
}
export interface RemoteWorkToolGroup {
  groupName: string;
  masterName?: string;
  membersCount: number;
  groupAnnouncement?: string;
  importState: 'Available' | 'Imported' | 'Conflict';
}
export interface RemoteWorkToolGroupPage {
  pageNumber: number;
  pageSize: number;
  totalPages: number;
  total: number;
  items: RemoteWorkToolGroup[];
}
export interface GroupImportResult {
  groupName: string;
  status: 'Imported' | 'Conflict';
  groupProfileId?: string;
  errorCode?: string;
}
export interface GroupOperation { robotConfigId: string; kind: 'Create' | 'AddMembers' | 'RemoveMembers' | 'Rename' | 'UpdateAnnouncement'; groupIdentifier: string; memberDisplayNames: string[]; value?: string; }
export type WorkToolOperationStatus =
  | 'queued'
  | 'dispatching'
  | 'dispatchFailed'
  | 'rejected'
  | 'accepted'
  | 'executedSucceeded'
  | 'executedPartially'
  | 'executedFailed'
  | 'deliveryUnknown'
  | 'resultTimeout';
export interface WorkToolOperationAudit {
  id: string;
  operation: string;
  workToolCommandNumber: number;
  status: WorkToolOperationStatus | string;
  result?: string;
  createdAtUtc: string;
}
export interface WorkToolOperationsApi {
  listRobots(): Promise<WorkToolRobotOption[]>;
  listGroups(status?: 'current' | 'enabled' | 'disabled' | 'archived' | 'all'): Promise<KnownGroup[]>;
  listRemoteGroups(robotId: string, params: {
    query?: string;
    page: number;
    pageSize: number;
  }): Promise<RemoteWorkToolGroupPage>;
  importRemoteGroups(robotId: string, groups: {
    groupName: string;
    expectedImportState: 'Available';
  }[]): Promise<GroupImportResult[]>;
  registerExistingGroup(request: { robotConfigId: string; name: string; workToolGroupRemark?: string; manualInvitationCompleted: boolean }): Promise<KnownGroup>;
  preview(operation: GroupOperation): Promise<{ sanitizedRequest: string; confirmationToken: string; expiresAtUtc: string }>;
  execute(operation: GroupOperation, confirmationToken: string): Promise<{ succeeded: boolean; message: string }>;
  listOperations(): Promise<WorkToolOperationAudit[]>;
  getAuditScope(): Promise<{ scope: string }>;
}
export const workToolOperationsApi: WorkToolOperationsApi = {
  async listRobots() { return (await apiClient.get<WorkToolRobotOption[]>('/api/admin/worktool/robots')).data; },
  async listGroups(status = 'current') {
    return (await apiClient.get<KnownGroup[]>('/api/admin/worktool/groups', { params: { status } })).data;
  },
  async listRemoteGroups(robotId, params) {
    return (await apiClient.get<RemoteWorkToolGroupPage>(
      `/api/admin/worktool/robots/${encodeURIComponent(robotId)}/groups`,
      { params }
    )).data;
  },
  async importRemoteGroups(robotId, groups) {
    return (await apiClient.post<GroupImportResult[]>(
      `/api/admin/worktool/robots/${encodeURIComponent(robotId)}/groups/import`,
      { groups }
    )).data;
  },
  async registerExistingGroup(request) { return (await apiClient.post<KnownGroup>('/api/admin/worktool/groups/register', request)).data; },
  async preview(operation) { return (await apiClient.post<{ sanitizedRequest: string; confirmationToken: string; expiresAtUtc: string }>('/api/admin/worktool/group-operations/preview', operation)).data; },
  async execute(operation, confirmationToken) { return (await apiClient.post<{ succeeded: boolean; message: string }>('/api/admin/worktool/group-operations/execute', { operation, confirmationToken })).data; },
  async listOperations() { return (await apiClient.get<WorkToolOperationAudit[]>('/api/admin/worktool/group-operations')).data; },
  async getAuditScope() { return (await apiClient.get<{ scope: string }>('/api/admin/worktool/group-operations/audit-scope')).data; }
};
