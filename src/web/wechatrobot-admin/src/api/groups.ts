import { apiClient } from './http';

export type PatternKind = 'exact' | 'contains' | 'regex';
export interface GroupRule { id?: string; pattern: string; patternKind: PatternKind; ignoreCase: boolean; }
export interface ContextOverrides { senderIsolated?: boolean | null; historyTurns?: number | null; idleTimeoutMinutes?: number | null; tokenCap?: number | null; summaryEnabled?: boolean | null; includeBotHistory?: boolean | null; }
export interface EffectiveContext { senderIsolated: boolean; historyTurns: number; idleTimeoutMinutes: number; tokenCap: number; summaryEnabled: boolean; includeBotHistory: boolean; }
export interface GroupConfiguration {
  id: string; name: string; rules: { include: GroupRule[]; exclude: GroupRule[] }; boundTagIds: string[]; allowedTagIds: string[];
  availableTags: { id: string; name: string; isGlobalPublic: boolean; isEnabled: boolean; isBound: boolean }[]; tagVisibility: 'any-bound-tag-or-global-public';
  context: { configured: ContextOverrides; effective: EffectiveContext }; clearedContextSessions: number;
  configurationVersion: number;
}
export interface UpdateGroupConfiguration {
  includeRules: GroupRule[];
  excludeRules: GroupRule[];
  boundTagIds: string[];
  context: ContextOverrides;
  clearContext: boolean;
  expectedConfigurationVersion: number;
}
export interface RulePreview { results: { groupName: string; isMatch: boolean; isExcluded: boolean }[]; }
export interface GroupApi {
  getConfiguration(groupId: string): Promise<GroupConfiguration>;
  updateConfiguration(groupId: string, request: UpdateGroupConfiguration): Promise<GroupConfiguration>;
  previewRules(request: Pick<UpdateGroupConfiguration, 'includeRules' | 'excludeRules'> & { groupNames: string[] }): Promise<RulePreview>;
}

export const groupApi: GroupApi = {
  async getConfiguration(groupId) { return (await apiClient.get<GroupConfiguration>(`/api/groups/${encodeURIComponent(groupId)}/configuration`)).data; },
  async updateConfiguration(groupId, request) { return (await apiClient.put<GroupConfiguration>(`/api/groups/${encodeURIComponent(groupId)}/configuration`, request)).data; },
  async previewRules(request) { return (await apiClient.post<RulePreview>('/api/group-rules/preview', request)).data; }
};
