import { apiClient } from './http';

export type PrivateKnowledgeIngestStatus =
  | 'Received'
  | 'Extracting'
  | 'Comparing'
  | 'Staged'
  | 'Indexing'
  | 'Activated'
  | 'Retryable'
  | 'Failed';

export interface PrivateKnowledgeIngestBatch {
  id: string;
  robotConfigId: string;
  sourceConversationMessageId: string;
  roomType: number;
  sourceActorDisplayName: string;
  status: PrivateKnowledgeIngestStatus;
  totalCount: number;
  newCount: number;
  duplicateCount: number;
  supplementCount: number;
  correctionCount: number;
  failureCode?: string | null;
  version: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface PrivateKnowledgeIngestApi {
  list(params?: { status?: string; skip?: number; take?: number }): Promise<PrivateKnowledgeIngestBatch[]>;
  get(id: string): Promise<PrivateKnowledgeIngestBatch>;
  retry(id: string, expectedVersion: number): Promise<PrivateKnowledgeIngestBatch>;
}

export const privateKnowledgeIngestApi: PrivateKnowledgeIngestApi = {
  async list(params = {}) {
    return (await apiClient.get<PrivateKnowledgeIngestBatch[]>(
      '/api/admin/private-knowledge-ingests',
      { params }
    )).data;
  },
  async get(id: string) {
    return (await apiClient.get<PrivateKnowledgeIngestBatch>(
      `/api/admin/private-knowledge-ingests/${encodeURIComponent(id)}`
    )).data;
  },
  async retry(id: string, expectedVersion: number) {
    return (await apiClient.post<PrivateKnowledgeIngestBatch>(
      `/api/admin/private-knowledge-ingests/${encodeURIComponent(id)}/retry`,
      { expectedVersion }
    )).data;
  }
};
