import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { KnowledgeDocumentDetail } from '../../api/knowledge';
import { useAuthStore } from '../../stores/auth';
import KnowledgeDocumentManagementView from './KnowledgeDocumentManagementView.vue';

const documentId = '11111111-1111-1111-1111-111111111111';
const newestVersionId = '22222222-2222-2222-2222-222222222222';

function detail(): KnowledgeDocumentDetail {
  return {
    document: {
      id: documentId,
      title: '产品手册.pdf',
      status: 'failed',
      stateVersion: 4,
      activeVersionId: null,
      versionCount: 2,
      latestVersionId: newestVersionId,
      latestVersion: 2,
      latestVersionStatus: 'failed',
      latestFailureReason: 'Object storage upload failed; retry is available.',
      canRetryUpload: true,
      isDeleteRequested: false,
      canRetryPhysicalDelete: false,
      sourceKind: 'DocumentUpload',
      sourceActorDisplayName: '系统管理员',
      tags: [],
      createdAtUtc: '2026-07-24T00:00:00Z',
      updatedAtUtc: '2026-07-25T00:00:00Z'
    },
    versions: [
      {
        id: newestVersionId,
        version: 2,
        originalFileName: '产品手册.pdf',
        safeFileName: 'source.pdf',
        contentType: 'application/pdf',
        sizeBytes: 2048,
        status: 'failed',
        failureReason: 'Object storage upload failed; retry is available.',
        isPublished: false,
        hasPublicObject: false,
        previewRevision: 0,
        previewCount: 0,
        approvedChunkCount: 0,
        ocrPageCount: 0,
        ocrFailedPageCount: 0,
        sourceKind: 'DocumentUpload',
        sourceActorDisplayName: '系统管理员',
        tags: [],
        uploadAndParseJobs: [
          {
            id: 'job-1',
            jobType: 'UploadKnowledgeDocument',
            status: 'retrying',
            attemptCount: 2,
            createdAtUtc: '2026-07-25T00:00:00Z',
            updatedAtUtc: '2026-07-25T00:01:00Z'
          }
        ],
        indexJobs: [],
        createdAtUtc: '2026-07-25T00:00:00Z',
        updatedAtUtc: '2026-07-25T00:01:00Z'
      },
      {
        id: '33333333-3333-3333-3333-333333333333',
        version: 1,
        originalFileName: '旧版.pdf',
        safeFileName: 'source.pdf',
        contentType: 'application/pdf',
        sizeBytes: 1024,
        status: 'disabled',
        failureReason: null,
        isPublished: false,
        hasPublicObject: true,
        previewRevision: 3,
        previewCount: 4,
        approvedChunkCount: 3,
        ocrPageCount: 2,
        ocrFailedPageCount: 1,
        sourceKind: 'DocumentUpload',
        sourceActorDisplayName: '系统管理员',
        tags: [],
        uploadAndParseJobs: [],
        indexJobs: [
          {
            id: 'index-1',
            operation: 'index',
            status: 'failed',
            attemptCount: 1,
            hasFailure: true,
            createdAtUtc: '2026-07-24T00:00:00Z',
            updatedAtUtc: '2026-07-24T00:01:00Z'
          }
        ],
        createdAtUtc: '2026-07-24T00:00:00Z',
        updatedAtUtc: '2026-07-24T00:01:00Z'
      }
    ]
  };
}

function createApi() {
  return {
    upload: vi.fn().mockResolvedValue({
      documentId,
      versionId: '44444444-4444-4444-4444-444444444444',
      version: 3,
      state: 'uploaded',
      safeFileName: 'source.pdf',
      publicUrl: null,
      publicReadWarning: ''
    }),
    getDocument: vi.fn().mockResolvedValue(detail()),
    retryDocumentUpload: vi.fn().mockResolvedValue({}),
    disableDocument: vi.fn().mockResolvedValue(undefined),
    requestPhysicalDelete: vi.fn().mockResolvedValue(undefined)
  };
}

function authenticate(role: 'Admin' | 'KnowledgeOperator'): ReturnType<typeof createPinia> {
  const pinia = createPinia();
  setActivePinia(pinia);
  const auth = useAuthStore();
  auth.accessToken = 'token';
  auth.user = {
    id: 'user-1',
    email: 'operator@example.test',
    displayName: 'Operator',
    roles: [role]
  };
  return pinia;
}

describe('KnowledgeDocumentManagementView', () => {
  beforeEach(() => vi.clearAllMocks());

  it('renders persisted version evidence and links every version to chunks', async () => {
    const api = createApi();
    const wrapper = mount(KnowledgeDocumentManagementView, {
      props: { documentId, api },
      global: { plugins: [authenticate('KnowledgeOperator')] }
    });
    await flushPromises();

    expect(api.getDocument).toHaveBeenCalledWith(documentId);
    expect(wrapper.text()).toContain('产品手册.pdf');
    expect(wrapper.text()).toContain('OCR 2 页（失败 1 页）');
    expect(wrapper.text()).toContain('UploadKnowledgeDocument · retrying · 尝试 2 次');
    expect(wrapper.get(`[data-testid="open-version-${newestVersionId}"]`).attributes('href'))
      .toBe(`/knowledge/documents/${documentId}/versions/${newestVersionId}`);
    expect(wrapper.find('[data-testid="request-physical-delete"]').exists()).toBe(false);
    expect(wrapper.html()).not.toMatch(/objectKey|payloadJson|stagedContent|publicUrl/i);
  });

  it('refreshes persisted truth after retry and disable', async () => {
    const api = createApi();
    const confirmAction = vi.fn().mockResolvedValue(true);
    const wrapper = mount(KnowledgeDocumentManagementView, {
      props: { documentId, api, confirmAction },
      global: { plugins: [authenticate('KnowledgeOperator')] }
    });
    await flushPromises();

    await wrapper.get('[data-testid="retry-document-upload"]').trigger('click');
    await flushPromises();
    expect(api.retryDocumentUpload).toHaveBeenCalledWith(documentId, 4);

    await wrapper.get('[data-testid="disable-document"]').trigger('click');
    await flushPromises();
    expect(api.disableDocument).toHaveBeenCalledWith(documentId, 4);
    expect(api.getDocument).toHaveBeenCalledTimes(3);
  });

  it('uploads a selected file as a new version of the current document', async () => {
    const api = createApi();
    const wrapper = mount(KnowledgeDocumentManagementView, {
      props: { documentId, api },
      global: { plugins: [authenticate('KnowledgeOperator')] }
    });
    await flushPromises();

    const file = new File(['new version'], '产品手册-v3.pdf', {
      type: 'application/pdf'
    });
    const input = wrapper.get('[data-testid="new-version-file"]');
    Object.defineProperty(input.element, 'files', {
      configurable: true,
      value: [file]
    });
    await input.trigger('change');
    await wrapper.get('[data-testid="upload-new-version"]').trigger('click');
    await flushPromises();

    expect(api.upload).toHaveBeenCalledWith(file, expect.any(Function), documentId);
    expect(wrapper.text()).toContain('新版本 v3 已提交处理');
    expect(api.getDocument).toHaveBeenCalledTimes(2);
  });

  it('does not offer new-version upload after a document is disabled', async () => {
    const api = createApi();
    api.getDocument.mockResolvedValue({
      ...detail(),
      document: {
        ...detail().document,
        status: 'disabled'
      }
    });
    const wrapper = mount(KnowledgeDocumentManagementView, {
      props: { documentId, api },
      global: { plugins: [authenticate('KnowledgeOperator')] }
    });
    await flushPromises();

    expect(wrapper.find('[data-testid="upload-new-version"]').exists()).toBe(false);
    expect(wrapper.text()).toContain('当前文档状态不允许上传新版本');
  });

  it.each([
    ['PrivateChatDirect', '私聊直接入库'],
    ['ConversationReview', '消息审核入库'],
    ['AdministrationRevision', '管理员修订']
  ])(
    'shows %s source evidence without offering file-version upload',
    async (sourceKind, sourceText) => {
      const api = createApi();
      const automatic = detail();
      automatic.document = {
        ...automatic.document,
        status: 'active',
        sourceKind,
        sourceActorDisplayName: '张伟',
        tags: [{ id: 'tag-1', name: '加拿大签证' }]
      };
      automatic.versions = [{
        ...automatic.versions[0],
        status: 'active',
        sourceKind,
        sourceActorDisplayName: '张伟',
        changeKind: 'Correction',
        tags: [{ id: 'tag-1', name: '加拿大签证' }]
      }];
      api.getDocument.mockResolvedValue(automatic);

      const wrapper = mount(KnowledgeDocumentManagementView, {
        props: { documentId, api },
        global: { plugins: [authenticate('KnowledgeOperator')] }
      });
      await flushPromises();

      expect(wrapper.text()).toContain(sourceText);
      expect(wrapper.text()).toContain('张伟');
      expect(wrapper.text()).toContain('纠正');
      expect(wrapper.text()).toContain('加拿大签证');
      expect(wrapper.find('[data-testid="upload-new-version"]').exists()).toBe(false);
      expect(wrapper.find('[data-testid="new-version-file"]').exists()).toBe(false);
      expect(wrapper.find('[data-testid="disable-document"]').exists()).toBe(true);
    });

  it('shows the exact asynchronous physical-delete confirmation only to Admin', async () => {
    const api = createApi();
    const confirmAction = vi.fn().mockResolvedValue(true);
    const wrapper = mount(KnowledgeDocumentManagementView, {
      props: { documentId, api, confirmAction },
      global: { plugins: [authenticate('Admin')] }
    });
    await flushPromises();

    await wrapper.get('[data-testid="request-physical-delete"]').trigger('click');
    await flushPromises();

    expect(confirmAction).toHaveBeenCalledWith(
      '这会停用文档并提交异步物理清理，期间不可上传新版本。确认继续？');
    expect(api.requestPhysicalDelete).toHaveBeenCalledWith(documentId, 4);
    expect(wrapper.text()).toContain('删除请求已受理，等待后台清理');
    expect(wrapper.text()).not.toContain('已物理删除');
    expect(api.getDocument).toHaveBeenCalledTimes(2);
  });

  it('shows a pending cleanup state without offering the initial delete action again', async () => {
    const api = createApi();
    api.getDocument.mockResolvedValue({
      ...detail(),
      document: {
        ...detail().document,
        status: 'disabled',
        isDeleteRequested: true,
        canRetryPhysicalDelete: false
      }
    });
    const wrapper = mount(KnowledgeDocumentManagementView, {
      props: { documentId, api },
      global: { plugins: [authenticate('Admin')] }
    });
    await flushPromises();

    expect(wrapper.get('[data-testid="document-state"]').text())
      .toContain('等待物理清理');
    expect(wrapper.find('[data-testid="request-physical-delete"]').exists())
      .toBe(false);
  });

  it('replaces stale status from a concurrency response', async () => {
    const api = createApi();
    api.disableDocument.mockRejectedValue({
      response: {
        data: {
          error: 'document-concurrency-conflict',
          current: { id: documentId, status: 'active', stateVersion: 5 }
        }
      }
    });
    const wrapper = mount(KnowledgeDocumentManagementView, {
      props: {
        documentId,
        api,
        confirmAction: vi.fn().mockResolvedValue(true)
      },
      global: { plugins: [authenticate('KnowledgeOperator')] }
    });
    await flushPromises();

    await wrapper.get('[data-testid="disable-document"]').trigger('click');
    await flushPromises();

    expect(wrapper.get('[data-testid="document-state"]').text()).toContain('active');
    expect(wrapper.text()).toContain('已被其他操作员修改');
  });
});
