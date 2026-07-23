<script setup lang="ts">
import { computed, ref } from 'vue';
import { ElAlert, ElButton, ElProgress } from 'element-plus';
import PublicOssWarning from '../../components/PublicOssWarning.vue';
import { knowledgeApi, type KnowledgeApi, type UploadResult } from '../../api/knowledge';

const props = withDefaults(defineProps<{ api?: Pick<KnowledgeApi, 'upload'> }>(), { api: () => knowledgeApi });
const selectedFile = ref<File>();
const progress = ref(0);
const uploading = ref(false);
const error = ref('');
const result = ref<UploadResult>();
const isLegacyDoc = computed(() => selectedFile.value?.name.toLowerCase().endsWith('.doc') ?? false);

function chooseFile(event: Event) {
  selectedFile.value = (event.target as HTMLInputElement).files?.[0];
  error.value = '';
  result.value = undefined;
  progress.value = 0;
}
function message(errorValue: unknown): string {
  if (errorValue instanceof Error) return errorValue.message;
  return '上传失败，请检查文件和网络后重试。';
}
async function upload() {
  if (!selectedFile.value) { error.value = '请先选择文件。'; return; }
  uploading.value = true;
  error.value = '';
  try {
    result.value = await props.api.upload(selectedFile.value, value => { progress.value = value; });
    progress.value = 100;
  } catch (value) {
    error.value = message(value);
  } finally {
    uploading.value = false;
  }
}
</script>

<template>
  <section class="ops-page" aria-labelledby="documents-title">
    <header class="page-header">
      <div><p class="eyebrow">知识库运营</p><h1 id="documents-title">知识文档</h1><p>上传 Markdown、TXT、PDF 或 DOCX，完成分段审核后再建立索引。</p></div>
    </header>
    <PublicOssWarning />
    <section class="panel" aria-labelledby="upload-title">
      <h2 id="upload-title">上传文档</h2>
      <div class="form-row">
        <label for="knowledge-file">文件（Markdown / TXT / PDF / DOCX）</label>
        <input id="knowledge-file" type="file" accept=".md,.txt,.pdf,.doc,.docx" @change="chooseFile">
        <p class="helper">旧版 DOC 需要先转换为 DOCX；系统不会在后台静默转换格式。</p>
        <ElAlert v-if="isLegacyDoc" title="检测到 DOC 文件，请先用 Word 另存为 DOCX 后再上传。" type="warning" :closable="false" show-icon />
      </div>
      <div v-if="uploading || progress" class="progress-block" aria-live="polite">
        <ElProgress :percentage="progress" :stroke-width="10" :aria-label="`上传进度 ${progress}%`" />
      </div>
      <ElAlert v-if="error" :title="`${error} 请修正后重新上传。`" type="error" :closable="false" show-icon />
      <div v-if="result" class="notice success" aria-live="polite">
        <p>已上传 {{ result.safeFileName }}，当前状态：{{ result.state }}。</p>
        <a class="primary-link" data-testid="open-document-detail" :href="`/knowledge/documents/${result.documentId}/versions/${result.versionId}`">进入分段与索引</a>
      </div>
      <ElButton type="primary" data-testid="upload-document" :loading="uploading" :disabled="isLegacyDoc" @click="upload">
        {{ uploading ? '正在上传…' : '上传文档' }}
      </ElButton>
    </section>
    <section class="panel">
      <h2>文档列表</h2>
      <div class="capability-state">
        <strong>后端暂未提供文档列表查询 API</strong>
        <p>当前可上传文档，并通过上传结果中的文档 ID 和版本 ID 进入分段处理；这里不会显示伪造列表。</p>
      </div>
    </section>
  </section>
</template>
