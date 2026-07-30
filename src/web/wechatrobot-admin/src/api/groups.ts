import { apiClient } from './http';

export type PatternKind = 'exact' | 'contains' | 'regex';
export interface GroupRule { id?: string; pattern: string; patternKind: PatternKind; ignoreCase: boolean; }
export interface ContextOverrides { senderIsolated?: boolean | null; historyTurns?: number | null; idleTimeoutMinutes?: number | null; tokenCap?: number | null; summaryEnabled?: boolean | null; includeBotHistory?: boolean | null; }
export interface EffectiveContext { senderIsolated: boolean; historyTurns: number; idleTimeoutMinutes: number; tokenCap: number; summaryEnabled: boolean; includeBotHistory: boolean; }
export interface AnswerFallbackSettings {
  webSearchEnabled: boolean;
  modelKnowledgeFallbackEnabled: boolean;
  webSearchShowSources: boolean;
  webSearchResultCount: number;
  webSearchRecency: 'NoLimit' | 'OneDay' | 'OneWeek' | 'OneMonth' | 'OneYear';
  webSearchDomainFilter?: string | null;
  webSearchContentSize: 'Medium' | 'High';
  finalNoEvidencePolicy: 'InsufficientEvidence' | 'Clarification';
}
export interface GroupConfiguration {
  id: string; name: string; rules: { include: GroupRule[]; exclude: GroupRule[] }; boundTagIds: string[]; allowedTagIds: string[];
  identity: {
    robotName: string;
    workToolGroupRemark?: string | null;
    registrationSource: string;
    state: GroupLifecycleStatus;
    isEnabled: boolean;
    stateVersion: number;
  };
  availableTags: { id: string; name: string; isGlobalPublic: boolean; isEnabled: boolean; isBound: boolean }[]; tagVisibility: 'any-bound-tag-or-global-public';
  context: { configured: ContextOverrides; effective: EffectiveContext }; clearedContextSessions: number;
  answerFallback: AnswerFallbackSettings;
  defaultChatModel: {
    isConfigured: boolean;
    configurationName?: string | null;
    connectionStatus?: string | null;
    webSearchMode: string;
    canUseWebSearch: boolean;
    unavailableReason: 'none' | 'not_configured' | 'disabled' | 'connection_not_succeeded' | 'not_enabled' | 'unsupported';
  };
  memorySummary: {
    activeGroupMemoryCount: number;
    activeMemberMemoryCount: number;
    pendingCandidateCount: number;
    pendingOrRunningJobCount: number;
  };
  agentRuntime?: {
    intentRuntimeMode: 'Legacy' | 'Shadow' | 'AgentFramework' | 'Paused';
    answerRuntimeMode: 'Legacy' | 'Shadow' | 'AgentFramework';
    templateRoutingRuntimeMode: 'Disabled' | 'Shadow' | 'AgentFramework';
    editable: boolean;
  };
  configurationVersion: number;
}
export interface UpdateGroupConfiguration {
  includeRules: GroupRule[];
  excludeRules: GroupRule[];
  boundTagIds: string[];
  context: ContextOverrides;
  clearContext: boolean;
  expectedConfigurationVersion: number;
  answerFallback?: AnswerFallbackSettings;
}
export interface RulePreview { results: { groupName: string; isMatch: boolean; isExcluded: boolean }[]; }
export type GroupLifecycleStatus = 'enabled' | 'disabled' | 'archived';
export interface GroupLifecycleState {
  id: string;
  state: GroupLifecycleStatus;
  isEnabled: boolean;
  archivedAtUtc?: string | null;
  stateVersion: number;
}
export interface ConversationContextMessagePreview {
  role: string;
  senderDisplayName: string;
  content: string;
  createdAtUtc: string;
}
export interface ConversationContextSession {
  sessionId: string;
  senderDisplayName: string;
  scope: string;
  summary?: string | null;
  clearedAtUtc?: string | null;
  clearedThroughSequence: number;
  lastActivityAtUtc: string;
  version: number;
  messages: ConversationContextMessagePreview[];
  wasIdleReset: boolean;
  wasTokenLimited: boolean;
  contextTokenCount: number;
}
export interface GroupContextPage {
  groupId: string;
  configurationVersion: number;
  items: ConversationContextSession[];
  total: number;
  page: number;
  pageSize: number;
}
export interface GroupApi {
  getConfiguration(groupId: string): Promise<GroupConfiguration>;
  updateConfiguration(groupId: string, request: UpdateGroupConfiguration): Promise<GroupConfiguration>;
  previewRules(request: Pick<UpdateGroupConfiguration, 'includeRules' | 'excludeRules'> & { groupNames: string[] }): Promise<RulePreview>;
  changeState(groupId: string, action: 'disable' | 'enable' | 'archive' | 'restore', expectedStateVersion: number): Promise<GroupLifecycleState>;
  getContext(groupId: string, page: number, pageSize: number): Promise<GroupContextPage>;
  clearContext(groupId: string, expectedConfigurationVersion: number): Promise<{ clearedSessions: number; configurationVersion: number }>;
}

export const groupApi: GroupApi = {
  async getConfiguration(groupId) { return (await apiClient.get<GroupConfiguration>(`/api/groups/${encodeURIComponent(groupId)}/configuration`)).data; },
  async updateConfiguration(groupId, request) { return (await apiClient.put<GroupConfiguration>(`/api/groups/${encodeURIComponent(groupId)}/configuration`, request)).data; },
  async previewRules(request) { return (await apiClient.post<RulePreview>('/api/group-rules/preview', request)).data; },
  async changeState(groupId, action, expectedStateVersion) {
    return (await apiClient.post<GroupLifecycleState>(
      `/api/groups/${encodeURIComponent(groupId)}/${action}`,
      { expectedStateVersion }
    )).data;
  },
  async getContext(groupId, page, pageSize) {
    return (await apiClient.get<GroupContextPage>(
      `/api/groups/${encodeURIComponent(groupId)}/conversation-context`,
      { params: { page, pageSize } }
    )).data;
  },
  async clearContext(groupId, expectedConfigurationVersion) {
    return (await apiClient.post<{ clearedSessions: number; configurationVersion: number }>(
      `/api/groups/${encodeURIComponent(groupId)}/conversation-context/clear`,
      { expectedConfigurationVersion }
    )).data;
  }
};
