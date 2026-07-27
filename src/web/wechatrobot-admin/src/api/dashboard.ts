import { apiClient } from './http';

export interface RobotSummary {
  total: number;
  enabled: number;
  reachable: number;
  online: number;
  messageCallbackConfigured: number;
  commandResultCallbackConfigured: number;
  failedChecks: number;
}

export interface KnowledgeSummary {
  documents: number;
  versions: number;
  pendingCandidates: number;
  failedTasks: number;
}

export interface OperationsSummary {
  durableJobs: Record<string, number>;
  sendCommands: Record<string, number>;
  deadLetters: number;
}

export interface ReadinessComponent {
  name: string;
  status: 'healthy' | 'failed';
  required: boolean;
  detail?: string | null;
}

export interface ReadinessSummary {
  status: 'healthy' | 'degraded' | 'failed';
  components: ReadinessComponent[];
}

export interface DashboardSummary {
  checkedAtUtc: string;
  robots: RobotSummary;
  knowledge: KnowledgeSummary;
  operations: OperationsSummary;
  readiness: ReadinessSummary;
}

export interface DashboardApi {
  getSummary(): Promise<DashboardSummary>;
}

export const dashboardApi: DashboardApi = {
  async getSummary() {
    return (await apiClient.get<DashboardSummary>(
      '/api/admin/dashboard/summary'
    )).data;
  }
};
