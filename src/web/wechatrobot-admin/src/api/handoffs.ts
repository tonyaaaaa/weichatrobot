import { apiClient } from './http';
import type { Page } from './knowledge';

export interface HandoffSummary {
  id: string; state: string; reasonCode: string; version: number; updatedAtUtc: string;
  assigneeUserId?: string; groupProfileId?: string;
}
export interface HandoffDetail extends HandoffSummary { evidenceJson?: string; finalAnswer?: string }
export interface HandoffRecord { id: string; state: string; assigneeUserId?: string | null; version: number }
export interface KnowledgeCandidateRecord {
  id: string; handoffCaseId: string; question: string; answer: string; status: string; version: number;
}
export interface HandoffMessage {
  id: string; externalMessageId?: string | null; senderDisplayName: string; authenticatedUserId?: string | null;
  authenticationKind: string; text: string; createdAtUtc: string;
}
export interface HandoffTransition {
  id: string; actorUserId?: string | null; sequence: number; fromState: string; toState: string;
  reasonCode: string; createdAtUtc: string;
}
export interface HandoffApi {
  list(state: string, page: number, pageSize: number): Promise<Page<HandoffSummary>>;
  detail(id: string): Promise<HandoffDetail>;
  messages(id: string, page: number, pageSize: number): Promise<Page<HandoffMessage>>;
  transitions(id: string, page: number, pageSize: number): Promise<Page<HandoffTransition>>;
  assign(id: string, assigneeUserId: string, expectedVersion: number): Promise<HandoffRecord>;
  resolve(id: string, finalAnswer: string, expectedVersion: number): Promise<KnowledgeCandidateRecord>;
  restore(id: string, expectedVersion: number): Promise<HandoffRecord>;
}
export const handoffApi: HandoffApi = {
  async list(state, page, pageSize) { return (await apiClient.get('/api/handoffs/', { params: { state: state || undefined, page, pageSize } })).data; },
  async detail(id) { return (await apiClient.get(`/api/handoffs/${id}`)).data; },
  async messages(id, page, pageSize) { return (await apiClient.get(`/api/handoffs/${id}/messages`, { params: { page, pageSize } })).data; },
  async transitions(id, page, pageSize) { return (await apiClient.get(`/api/handoffs/${id}/transitions`, { params: { page, pageSize } })).data; },
  async assign(id, assigneeUserId, expectedVersion) { return (await apiClient.post(`/api/handoffs/${id}/assign`, { assigneeUserId, expectedVersion })).data; },
  async resolve(id, finalAnswer, expectedVersion) { return (await apiClient.post(`/api/handoffs/${id}/resolve`, { finalAnswer, expectedVersion })).data; },
  async restore(id, expectedVersion) { return (await apiClient.post(`/api/handoffs/${id}/restore-ai`, { expectedVersion })).data; }
};
