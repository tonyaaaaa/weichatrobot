import { apiClient } from './http';

export interface Page<T> { items: T[]; total: number; page: number; pageSize: number }
export interface UploadResult {
  documentId: string; versionId: string; version: number; state: string; safeFileName: string;
  publicUrl: string; publicReadWarning: string;
}
export interface PreviewItem { id: string; sequence: number; text: string; pageNumber?: number; status?: string }
export interface PreviewSet { versionId: string; revision: number; items: PreviewItem[] }
export interface IndexJobStatus {
  id: string;
  versionId: string;
  operation: string;
  status: string;
  attemptCount: number;
  failureReason?: string | null;
}
export interface IndexStatus {
  documentId: string;
  activeVersionId?: string | null;
  documentStatus: string;
  collectionName?: string | null;
  approvedChunkCount: number;
  activePointCount?: number | null;
  consistency: string;
  driftDetails: string[];
  jobs: IndexJobStatus[];
}
export interface CandidateSummary { id: string; question: string; status: string; version: number; updatedAtUtc: string }
export interface CandidateDetail extends CandidateSummary { answer: string; evidenceJson: string }

export interface KnowledgeApi {
  upload(file: File, onProgress: (value: number) => void): Promise<UploadResult>;
  getPreviews(versionId: string): Promise<PreviewSet | PreviewItem[]>;
  generatePreviews(versionId: string, revision: number): Promise<PreviewSet>;
  editPreview(versionId: string, previewId: string, text: string, revision: number): Promise<PreviewSet>;
  splitPreview(versionId: string, previewId: string, offset: number, revision: number): Promise<PreviewSet>;
  mergePreviews(versionId: string, firstId: string, secondId: string, revision: number): Promise<PreviewSet>;
  approvePreviews(versionId: string, revision: number): Promise<PreviewItem[]>;
  getIndexStatus(documentId: string): Promise<IndexStatus>;
  queueIndex(documentId: string, versionId: string, tagIds: string[], reindex?: boolean): Promise<{ jobId: string }>;
  retryIndex(jobId: string): Promise<void>;
}

export const knowledgeApi: KnowledgeApi = {
  async upload(file, onProgress) {
    const form = new FormData();
    form.append('file', file);
    const response = await apiClient.post<UploadResult>('/api/knowledge/documents', form, {
      onUploadProgress: event => onProgress(event.total ? Math.round(event.loaded * 100 / event.total) : 0)
    });
    return response.data;
  },
  async getPreviews(versionId) { return (await apiClient.get(`/api/knowledge/versions/${versionId}/previews`)).data; },
  async generatePreviews(versionId, expectedRevision) {
    return (await apiClient.post(`/api/knowledge/versions/${versionId}/previews/generate`, { expectedRevision, policy: null })).data;
  },
  async editPreview(versionId, previewId, text, expectedRevision) {
    return (await apiClient.put(`/api/knowledge/versions/${versionId}/previews/${previewId}`, { text, expectedRevision })).data;
  },
  async splitPreview(versionId, previewId, offset, expectedRevision) {
    return (await apiClient.post(`/api/knowledge/versions/${versionId}/previews/${previewId}/split`, { offset, expectedRevision })).data;
  },
  async mergePreviews(versionId, firstId, secondId, expectedRevision) {
    return (await apiClient.post(`/api/knowledge/versions/${versionId}/previews/merge`, { firstId, secondId, expectedRevision })).data;
  },
  async approvePreviews(versionId, expectedRevision) {
    return (await apiClient.post(`/api/knowledge/versions/${versionId}/previews/approve`, { expectedRevision })).data;
  },
  async getIndexStatus(documentId) {
    return (await apiClient.get(`/api/knowledge/documents/${documentId}/index-status`, { params: { checkConsistency: true } })).data;
  },
  async queueIndex(documentId, versionId, tagIds, reindex = false) {
    return (await apiClient.post(`/api/knowledge/documents/${documentId}/versions/${versionId}/${reindex ? 'reindex' : 'index'}`, { tagIds })).data;
  },
  async retryIndex(jobId) { await apiClient.post(`/api/knowledge/index-jobs/${jobId}/retry`); }
};

export interface KnowledgeReviewApi {
  listCandidates(status: string, page: number, pageSize: number): Promise<Page<CandidateSummary>>;
  getCandidate(id: string): Promise<CandidateDetail>;
  reviewCandidate(id: string, request: { decision: string; tagIds: string[]; revisedAnswer?: string; idempotencyKey: string; expectedVersion: number }): Promise<{ status: string }>;
}
export const knowledgeReviewApi: KnowledgeReviewApi = {
  async listCandidates(status, page, pageSize) {
    return (await apiClient.get('/api/knowledge/candidates/', { params: { status: status || undefined, page, pageSize } })).data;
  },
  async getCandidate(id) { return (await apiClient.get(`/api/knowledge/candidates/${id}`)).data; },
  async reviewCandidate(id, request) { return (await apiClient.post(`/api/knowledge/candidates/${id}/reviews`, request)).data; }
};
