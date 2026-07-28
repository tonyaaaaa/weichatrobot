import { apiClient } from './http';
import type { WorkToolRobotOption } from './worktool';

export type SendCommandStatus =
  | 'pending'
  | 'retrying'
  | 'leased'
  | 'dispatching'
  | 'executedSucceeded'
  | 'executedPartially'
  | 'executedFailed'
  | 'deliveryUnknown'
  | 'deliveryUnknownResolved'
  | 'resultTimeout'
  | 'blocked'
  | 'deadLetter'
  | 'cancelled'
  | string;

export interface SendCommandItem {
  id: string;
  robotConfigId: string;
  robotName: string;
  groupName: string;
  status: SendCommandStatus;
  attemptCount: number;
  createdAtUtc: string;
  externalDispatchStartedAtUtc?: string | null;
  completedAtUtc?: string | null;
  reason?: string | null;
  version: number;
  messageLength: number;
}

export interface SendCommandPage {
  items: SendCommandItem[];
  total: number;
  page: number;
  pageSize: number;
}

export interface SendCommandQuery {
  robotConfigId?: string;
  group?: string;
  status?: string;
  fromUtc?: string;
  toUtc?: string;
  page: number;
  pageSize: number;
}

export interface SendCommandMutationResult {
  id: string;
  status: string;
  version: number;
}

export interface SendCommandsApi {
  listRobots(): Promise<WorkToolRobotOption[]>;
  list(query: SendCommandQuery): Promise<SendCommandPage>;
  cancel(id: string, expectedVersion: number): Promise<SendCommandMutationResult>;
  acknowledgeUnknown(id: string, expectedVersion: number): Promise<SendCommandMutationResult>;
}

export const sendCommandsApi: SendCommandsApi = {
  async listRobots() {
    return (await apiClient.get<WorkToolRobotOption[]>('/api/admin/worktool/robots')).data;
  },
  async list(query) {
    return (await apiClient.get<SendCommandPage>(
      '/api/admin/operations/send-commands',
      { params: query }
    )).data;
  },
  async cancel(id, expectedVersion) {
    return (await apiClient.post<SendCommandMutationResult>(
      `/api/admin/operations/send-commands/${encodeURIComponent(id)}/cancel`,
      { expectedVersion }
    )).data;
  },
  async acknowledgeUnknown(id, expectedVersion) {
    return (await apiClient.post<SendCommandMutationResult>(
      `/api/admin/operations/send-commands/${encodeURIComponent(id)}/acknowledge-unknown`,
      { expectedVersion }
    )).data;
  }
};
