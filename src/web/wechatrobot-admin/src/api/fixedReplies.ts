import { apiClient } from './http';

export type FixedReplyScopeType = 'Global' | 'SelectedGroups';
export type FixedReplyGroupEffect = 'Include' | 'Exclude';
export interface FixedReplyGroupRule {
  groupProfileId: string;
  effect: FixedReplyGroupEffect;
}
export interface FixedReplyTemplate {
  id: string;
  name: string;
  intentDescription: string;
  replyText: string;
  scopeType: FixedReplyScopeType;
  priority: number;
  isEnabled: boolean;
  version: number;
  examples: string[];
  groupRules: FixedReplyGroupRule[];
  updatedAtUtc: string;
}
export interface FixedReplyTemplateDraft {
  name: string;
  intentDescription: string;
  replyText: string;
  scopeType: FixedReplyScopeType;
  priority: number;
  isEnabled: boolean;
  examples: string[];
  groupRules: FixedReplyGroupRule[];
}
export interface EffectiveFixedReply {
  id: string;
  version: number;
  name: string;
  intentDescription: string;
  examples: string[];
  priority: number;
  isGroupSpecific: boolean;
}
export interface FixedReplyRoutePreview {
  matched: boolean;
  decision: 'MatchFixedTemplate' | 'ContinueKnowledgeAnswer';
  reasonCode?: string | null;
  templateId?: string;
  templateVersion?: number;
  templateName?: string;
  replyText?: string;
}
export const fixedReplyApi = {
  async list(params: Record<string, unknown> = {}) {
    return (await apiClient.get<FixedReplyTemplate[]>(
      '/api/admin/fixed-reply-templates',
      { params }
    )).data;
  },
  async get(id: string) {
    return (await apiClient.get<FixedReplyTemplate>(
      `/api/admin/fixed-reply-templates/${encodeURIComponent(id)}`
    )).data;
  },
  async create(draft: FixedReplyTemplateDraft) {
    return (await apiClient.post<FixedReplyTemplate>(
      '/api/admin/fixed-reply-templates',
      draft
    )).data;
  },
  async update(id: string, version: number, draft: FixedReplyTemplateDraft) {
    return (await apiClient.put<FixedReplyTemplate>(
      `/api/admin/fixed-reply-templates/${encodeURIComponent(id)}`,
      { expectedVersion: version, ...draft }
    )).data;
  },
  async setEnabled(id: string, version: number, enabled: boolean) {
    return (await apiClient.post<FixedReplyTemplate>(
      `/api/admin/fixed-reply-templates/${encodeURIComponent(id)}/${enabled ? 'enable' : 'disable'}`,
      { expectedVersion: version }
    )).data;
  },
  async remove(id: string, version: number) {
    await apiClient.delete(
      `/api/admin/fixed-reply-templates/${encodeURIComponent(id)}`,
      { params: { expectedVersion: version } }
    );
  },
  async listForGroup(groupId: string) {
    return (await apiClient.get<EffectiveFixedReply[]>(
      `/api/admin/groups/${encodeURIComponent(groupId)}/fixed-reply-templates`
    )).data;
  },
  async includeForGroup(groupId: string, templateId: string, expectedVersion: number) {
    return (await apiClient.post<FixedReplyTemplate>(
      `/api/admin/groups/${encodeURIComponent(groupId)}/fixed-reply-templates/${encodeURIComponent(templateId)}/include`,
      { expectedVersion }
    )).data;
  },
  async removeIncludeForGroup(groupId: string, templateId: string, expectedVersion: number) {
    return (await apiClient.delete<FixedReplyTemplate>(
      `/api/admin/groups/${encodeURIComponent(groupId)}/fixed-reply-templates/${encodeURIComponent(templateId)}/include`,
      { params: { expectedVersion } }
    )).data;
  },
  async excludeForGroup(groupId: string, templateId: string, expectedVersion: number) {
    return (await apiClient.post<FixedReplyTemplate>(
      `/api/admin/groups/${encodeURIComponent(groupId)}/fixed-reply-templates/${encodeURIComponent(templateId)}/exclude`,
      { expectedVersion }
    )).data;
  },
  async removeExcludeForGroup(groupId: string, templateId: string, expectedVersion: number) {
    return (await apiClient.delete<FixedReplyTemplate>(
      `/api/admin/groups/${encodeURIComponent(groupId)}/fixed-reply-templates/${encodeURIComponent(templateId)}/exclude`,
      { params: { expectedVersion } }
    )).data;
  },
  async preview(groupProfileId: string, question: string) {
    return (await apiClient.post<FixedReplyRoutePreview>(
      '/api/admin/fixed-reply-templates/preview',
      { groupProfileId, question }
    )).data;
  }
};
