import { apiClient } from './http';

export type IntentRuntimeMode = 'Legacy' | 'Shadow' | 'AgentFramework' | 'Paused';
export type IntentDecision = 'Reply' | 'NoReply' | 'Uncertain';
export type IntentCategory =
  | 'DirectedToBot'
  | 'FollowUpToBot'
  | 'HumanConversation'
  | 'SocialChatter'
  | 'Uncertain';

export interface AgentRuntimeStatus {
  intentRuntimeMode: IntentRuntimeMode;
  answerRuntimeMode: 'Legacy' | 'Shadow' | 'AgentFramework';
  privateChatRuntimeMode: 'Disabled' | 'AgentFramework';
  templateRoutingRuntimeMode: 'Disabled' | 'Shadow' | 'AgentFramework';
  intentModelConfigurationId?: string | null;
  intentMinimumConfidence: number;
  intentHistoryMessageCount: number;
  intentHistoryMinutes: number;
}

export interface AgentDiagnosticsItem {
  id: string;
  conversationMessageId: string;
  groupProfileId: string;
  groupName: string;
  senderDisplayName: string;
  decision: IntentDecision;
  category: IntentCategory;
  reasonCode: string;
  confidence: number;
  failureCode?: string | null;
  runtimeMode: IntentRuntimeMode;
  agentVersion: string;
  modelConfigurationId?: string | null;
  modelConfigurationVersion?: number | null;
  latencyMilliseconds: number;
  formalConversationIncluded: boolean;
  decidedAtUtc: string;
}

export interface AgentDiagnosticsPage {
  items: AgentDiagnosticsItem[];
  total: number;
  page: number;
  pageSize: number;
}

export interface AgentDiagnosticsQuery {
  groupId?: string;
  runtimeMode?: IntentRuntimeMode;
  decision?: IntentDecision;
  fromUtc?: string;
  toUtc?: string;
  page?: number;
  pageSize?: number;
}

export interface AgentDiagnosticsApi {
  runtime(): Promise<AgentRuntimeStatus>;
  list(query?: AgentDiagnosticsQuery): Promise<AgentDiagnosticsPage>;
}

export const agentDiagnosticsApi: AgentDiagnosticsApi = {
  async runtime() {
    return (await apiClient.get<AgentRuntimeStatus>(
      '/api/admin/agent-diagnostics/runtime')).data;
  },
  async list(query = {}) {
    return (await apiClient.get<AgentDiagnosticsPage>(
      '/api/admin/agent-diagnostics',
      { params: query })).data;
  }
};
