import { apiClient } from './http';

export interface RobotSettings {
  id: string;
  name: string;
  robotReference: 'configured' | 'missing';
  hasWorkToolRobotId: boolean;
  isEnabled: boolean;
  sendRateLimitPerMinute: number;
  updatedAtUtc: string;
}

export interface RobotMutation {
  name: string;
  isEnabled: boolean;
  sendRateLimitPerMinute: number;
  workToolRobotId?: string;
  enableConfirmationToken?: string;
}

export interface RobotProbe {
  reachable: boolean;
  online: boolean | null;
  messageCallbackEnabled: boolean;
  replyAllEnabled: boolean;
  failureCode?: string | null;
  enableConfirmationToken?: string | null;
  enableConfirmationExpiresAtUtc?: string | null;
}

export interface RobotCallbackStatus {
  messageCallbackConfigured: boolean;
  commandResultCallbackConfigured: boolean;
  replyAll: boolean;
  checkedAtUtc: string;
}

export interface RobotApi {
  list(): Promise<RobotSettings[]>;
  save(id: string, request: RobotMutation): Promise<RobotSettings>;
  probe(id: string): Promise<RobotProbe>;
  configureMessageCallback(id: string, publicBaseUrl: string, replyAll: boolean): Promise<void>;
  configureCommandResultCallback(id: string, publicBaseUrl: string): Promise<void>;
  getCallbacks(id: string): Promise<RobotCallbackStatus>;
}

const path = (id: string) => `/api/admin/worktool/robots/${encodeURIComponent(id)}`;

export const robotApi: RobotApi = {
  async list() {
    return (await apiClient.get<RobotSettings[]>('/api/admin/worktool/robots')).data;
  },
  async save(id, request) {
    return (await apiClient.put<RobotSettings>(path(id), request)).data;
  },
  async probe(id) {
    return (await apiClient.post<RobotProbe>(`${path(id)}/test-connection`)).data;
  },
  async configureMessageCallback(id, publicBaseUrl, replyAll) {
    await apiClient.post(`${path(id)}/message-callback/configure`, { publicBaseUrl, replyAll });
  },
  async configureCommandResultCallback(id, publicBaseUrl) {
    await apiClient.post(`${path(id)}/command-result-callback/configure`, { publicBaseUrl });
  },
  async getCallbacks(id) {
    return (await apiClient.get<RobotCallbackStatus>(`${path(id)}/callbacks`)).data;
  }
};
