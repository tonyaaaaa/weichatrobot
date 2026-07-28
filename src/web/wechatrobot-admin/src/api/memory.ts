import { apiClient } from './http';

export interface Page<T> { items: T[]; total: number; page: number; pageSize: number }
export interface MemoryCandidate {
  id: string; scopeType: string; robotConfigId?: string | null; groupProfileId?: string | null;
  subjectKey?: string | null; subjectDisplayName?: string | null; memoryType: string; content: string;
  confidence: number; isExplicit: boolean; observationCount: number; distinctSessionCount: number;
  distinctDayCount: number; hasUnresolvedConflict: boolean; status: string;
  promotedMemoryEntryId?: string | null; knowledgeCandidateId?: string | null;
  version: number; createdAtUtc: string; updatedAtUtc: string;
}
export interface MemoryEntry {
  id: string; scopeType: string; robotConfigId?: string | null; groupProfileId?: string | null;
  subjectKey?: string | null; subjectDisplayName?: string | null; memoryType: string; content: string;
  confidence: number; status: string; supersedesMemoryEntryId?: string | null;
  sourceCandidateId?: string | null; validFromUtc: string; expiresAtUtc?: string | null;
  recallCount: number; lastRecalledAtUtc?: string | null; statusVersion: number;
  version: number; createdAtUtc: string; updatedAtUtc: string;
}
export interface MemoryJob {
  id: string; jobType: string; groupProfileId?: string | null; status: string; attemptCount: number;
  availableAtUtc: string; nextAttemptAtUtc: string; completedAtUtc?: string | null;
  version: number; createdAtUtc: string; updatedAtUtc: string;
}
export interface MemoryQuery {
  groupProfileId?: string; scopeType?: string; memoryType?: string; status?: string;
  page: number; pageSize: number;
}

export interface MemoryApi {
  listCandidates(query: MemoryQuery): Promise<Page<MemoryCandidate>>;
  editCandidate(id: string, content: string, confidence: number, expectedVersion: number): Promise<MemoryCandidate>;
  promoteCandidate(id: string, expectedVersion: number): Promise<MemoryEntry>;
  rejectCandidate(id: string, expectedVersion: number): Promise<MemoryCandidate>;
  reorganizeCandidate(id: string, expectedVersion: number): Promise<void>;
  listEntries(query: MemoryQuery): Promise<Page<MemoryEntry>>;
  forgetEntry(id: string, expectedVersion: number): Promise<MemoryEntry>;
  restoreEntry(id: string, expectedVersion: number): Promise<MemoryEntry>;
  listJobs(query: MemoryQuery): Promise<Page<MemoryJob>>;
  retryJob(id: string, expectedVersion: number): Promise<void>;
}

const params = (query: MemoryQuery) => ({
  groupProfileId: query.groupProfileId || undefined,
  scopeType: query.scopeType || undefined,
  memoryType: query.memoryType || undefined,
  status: query.status || undefined,
  page: query.page,
  pageSize: query.pageSize
});

export const memoryApi: MemoryApi = {
  async listCandidates(query) {
    return (await apiClient.get<Page<MemoryCandidate>>('/api/admin/memory/candidates', { params: params(query) })).data;
  },
  async editCandidate(id, content, confidence, expectedVersion) {
    return (await apiClient.post<MemoryCandidate>(`/api/admin/memory/candidates/${encodeURIComponent(id)}/edit`, {
      content, confidence, expectedVersion
    })).data;
  },
  async promoteCandidate(id, expectedVersion) {
    return (await apiClient.post<MemoryEntry>(`/api/admin/memory/candidates/${encodeURIComponent(id)}/promote`, {
      expectedVersion
    })).data;
  },
  async rejectCandidate(id, expectedVersion) {
    return (await apiClient.post<MemoryCandidate>(`/api/admin/memory/candidates/${encodeURIComponent(id)}/reject`, {
      expectedVersion
    })).data;
  },
  async reorganizeCandidate(id, expectedVersion) {
    await apiClient.post(`/api/admin/memory/candidates/${encodeURIComponent(id)}/reorganize`, {
      expectedVersion
    });
  },
  async listEntries(query) {
    return (await apiClient.get<Page<MemoryEntry>>('/api/admin/memory/entries', { params: params(query) })).data;
  },
  async forgetEntry(id, expectedVersion) {
    return (await apiClient.post<MemoryEntry>(`/api/admin/memory/entries/${encodeURIComponent(id)}/forget`, {
      expectedVersion
    })).data;
  },
  async restoreEntry(id, expectedVersion) {
    return (await apiClient.post<MemoryEntry>(`/api/admin/memory/entries/${encodeURIComponent(id)}/restore`, {
      expectedVersion
    })).data;
  },
  async listJobs(query) {
    return (await apiClient.get<Page<MemoryJob>>('/api/admin/memory/jobs', { params: params(query) })).data;
  },
  async retryJob(id, expectedVersion) {
    await apiClient.post(`/api/admin/memory/jobs/${encodeURIComponent(id)}/retry`, { expectedVersion });
  }
};
