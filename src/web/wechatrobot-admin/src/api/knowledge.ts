import { apiClient } from './http';

export interface Page<T> { items: T[]; total: number; page: number; pageSize: number }
export interface UploadResult {
  documentId: string; versionId: string; version: number; state: string; safeFileName: string;
  publicUrl: string | null; publicReadWarning: string;
}
export interface KnowledgeDocumentSummary {
  id: string;
  title: string;
  status: string;
  stateVersion: number;
  activeVersionId: string | null;
  versionCount: number;
  latestVersionId: string | null;
  latestVersion: number | null;
  latestVersionStatus: string | null;
  latestFailureReason: string | null;
  canRetryUpload: boolean;
  createdAtUtc: string;
  updatedAtUtc: string;
}
export interface KnowledgeDocumentPage {
  items: KnowledgeDocumentSummary[];
  total: number;
  page: number;
  pageSize: number;
}
export interface KnowledgeDocumentJobSummary {
  id: string;
  jobType: string;
  status: string;
  attemptCount: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}
export interface KnowledgeDocumentIndexJobSummary {
  id: string;
  operation: string;
  status: string;
  attemptCount: number;
  hasFailure: boolean;
  createdAtUtc: string;
  updatedAtUtc: string;
}
export interface KnowledgeDocumentVersionSummary {
  id: string;
  version: number;
  originalFileName: string;
  safeFileName: string;
  contentType: string;
  sizeBytes: number;
  status: string;
  failureReason: string | null;
  isPublished: boolean;
  hasPublicObject: boolean;
  previewRevision: number;
  previewCount: number;
  approvedChunkCount: number;
  ocrPageCount: number;
  ocrFailedPageCount: number;
  uploadAndParseJobs: KnowledgeDocumentJobSummary[];
  indexJobs: KnowledgeDocumentIndexJobSummary[];
  createdAtUtc: string;
  updatedAtUtc: string;
}
export interface KnowledgeDocumentDetail {
  document: KnowledgeDocumentSummary;
  versions: KnowledgeDocumentVersionSummary[];
}
export interface KnowledgeDocumentListRequest {
  query?: string;
  status?: string;
  page: number;
  pageSize: number;
}
export interface PreviewItem { id: string; sequence: number; text: string; pageNumber?: number; status?: string }
export interface PreviewSet { versionId: string; revision: number; items: PreviewItem[] }
export interface ChunkLengths { targetTokens: number; overlapTokens: number; maximumTokens: number }
export type ChunkPolicy =
  | ({ kind: 'smart' } & ChunkLengths)
  | ({ kind: 'separator'; separator: string } & ChunkLengths)
  | ({ kind: 'regex'; regexPattern: string } & ChunkLengths)
  | ({ kind: 'qa'; qaEntries: Array<{ question: string; synonyms: string[]; answer: string }> } & ChunkLengths);
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
  listDocuments(request: KnowledgeDocumentListRequest): Promise<KnowledgeDocumentPage>;
  getDocument(documentId: string): Promise<KnowledgeDocumentDetail>;
  getDocumentVersions(documentId: string): Promise<KnowledgeDocumentVersionSummary[]>;
  retryDocumentUpload(documentId: string, expectedStateVersion: number): Promise<UploadResult>;
  disableDocument(documentId: string, expectedStateVersion: number): Promise<void>;
  requestPhysicalDelete(documentId: string, expectedStateVersion: number): Promise<void>;
  getPreviews(versionId: string): Promise<PreviewSet | PreviewItem[]>;
  generatePreviews(versionId: string, revision: number, policy?: ChunkPolicy): Promise<PreviewSet>;
  editPreview(versionId: string, previewId: string, text: string, revision: number): Promise<PreviewSet>;
  splitPreview(versionId: string, previewId: string, offset: number, revision: number): Promise<PreviewSet>;
  mergePreviews(versionId: string, firstId: string, secondId: string, revision: number): Promise<PreviewSet>;
  deletePreview(versionId: string, previewId: string, revision: number): Promise<PreviewSet>;
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
  async listDocuments(request) {
    return (await apiClient.get<KnowledgeDocumentPage>('/api/knowledge/documents', {
      params: {
        query: request.query || undefined,
        status: request.status || undefined,
        page: request.page,
        pageSize: request.pageSize
      }
    })).data;
  },
  async getDocument(documentId) {
    return (await apiClient.get<KnowledgeDocumentDetail>(
      `/api/knowledge/documents/${encodeURIComponent(documentId)}`
    )).data;
  },
  async getDocumentVersions(documentId) {
    return (await apiClient.get<KnowledgeDocumentVersionSummary[]>(
      `/api/knowledge/documents/${encodeURIComponent(documentId)}/versions`
    )).data;
  },
  async retryDocumentUpload(documentId, expectedStateVersion) {
    return (await apiClient.post<UploadResult>(
      `/api/knowledge/documents/${encodeURIComponent(documentId)}/retry-upload`,
      { expectedStateVersion }
    )).data;
  },
  async disableDocument(documentId, expectedStateVersion) {
    await apiClient.post(
      `/api/knowledge/documents/${encodeURIComponent(documentId)}/disable`,
      { expectedStateVersion }
    );
  },
  async requestPhysicalDelete(documentId, expectedStateVersion) {
    await apiClient.delete(
      `/api/knowledge/documents/${encodeURIComponent(documentId)}/physical`,
      { params: { expectedStateVersion } }
    );
  },
  async getPreviews(versionId) { return (await apiClient.get(`/api/knowledge/versions/${encodeURIComponent(versionId)}/previews`)).data; },
  async generatePreviews(versionId, expectedRevision, policy = {
    kind: 'smart', targetTokens: 800, overlapTokens: 120, maximumTokens: 1000
  }) {
    return (await apiClient.post(`/api/knowledge/versions/${encodeURIComponent(versionId)}/previews/generate`, { expectedRevision, policy })).data;
  },
  async editPreview(versionId, previewId, text, expectedRevision) {
    return (await apiClient.put(`/api/knowledge/versions/${encodeURIComponent(versionId)}/previews/${encodeURIComponent(previewId)}`, { text, expectedRevision })).data;
  },
  async splitPreview(versionId, previewId, offset, expectedRevision) {
    return (await apiClient.post(`/api/knowledge/versions/${encodeURIComponent(versionId)}/previews/${encodeURIComponent(previewId)}/split`, { offset, expectedRevision })).data;
  },
  async mergePreviews(versionId, firstId, secondId, expectedRevision) {
    return (await apiClient.post(`/api/knowledge/versions/${encodeURIComponent(versionId)}/previews/merge`, { firstId, secondId, expectedRevision })).data;
  },
  async deletePreview(versionId, previewId, expectedRevision) {
    return (await apiClient.delete(
      `/api/knowledge/versions/${encodeURIComponent(versionId)}/previews/${encodeURIComponent(previewId)}`,
      { params: { expectedRevision } }
    )).data;
  },
  async approvePreviews(versionId, expectedRevision) {
    return (await apiClient.post(`/api/knowledge/versions/${encodeURIComponent(versionId)}/previews/approve`, { expectedRevision })).data;
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
