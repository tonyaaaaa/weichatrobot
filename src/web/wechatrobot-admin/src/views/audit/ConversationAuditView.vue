<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { ElAlert, ElButton, ElEmpty, ElSkeleton, ElTag } from 'element-plus';
import { auditApi, type AuditApi } from '../../api/audit';
import { formatBeijingTime } from '../../utils/beijingTime';
import { safeEvidence, safeEvidenceText } from '../../utils/evidenceRedaction';

const props = withDefaults(defineProps<{ api?: AuditApi }>(), { api: () => auditApi });
const loading = ref(true); const error = ref(''); const capability = ref<{ available: boolean; message?: string; items: Array<Record<string, unknown>> }>({ available: false, items: [] });
async function load() {
  loading.value = true; error.value = '';
  try { capability.value = await props.api.capability(); } catch { error.value = '审计能力检查失败，请重试。'; }
  finally { loading.value = false; }
}
onMounted(load);
</script>

<template>
  <section class="ops-page" aria-labelledby="audit-title">
    <header class="page-header"><div><p class="eyebrow">授权证据视图</p><h1 id="audit-title">会话审计</h1><p>此页面可展示检索来源和回答证据，但会递归过滤密钥、令牌和认证头。</p></div><ElButton @click="load">刷新</ElButton></header>
    <ElSkeleton v-if="loading" :rows="5" animated aria-label="正在检查审计查询能力" />
    <ElAlert v-else-if="error" :title="error" type="error" :closable="false" show-icon><ElButton @click="load">重试</ElButton></ElAlert>
    <section v-else-if="!capability.available" class="panel"><ElAlert title="后端暂未提供会话审计查询 API" type="info" :closable="false" show-icon><p>{{ capability.message }}</p></ElAlert></section>
    <section v-else class="panel">
      <ElEmpty v-if="!capability.items.length" description="暂无会话审计记录。" />
      <div v-else class="audit-list">
        <article v-for="item in capability.items" :key="String(item.id)" class="audit-card">
          <header><strong>{{ item.question }}</strong><time>{{ formatBeijingTime(String(item.createdAtUtc ?? '')) }}</time></header>
          <p>{{ safeEvidenceText(item.answer) }}</p><h2>检索来源</h2><div class="tag-list"><ElTag v-for="source in (item.sources as string[] ?? [])" :key="source" effect="plain">{{ safeEvidenceText(source) }}</ElTag></div>
          <details><summary>完整证据（已移除秘密字段）</summary><pre>{{ safeEvidence(item.evidence) }}</pre></details>
        </article>
      </div>
    </section>
  </section>
</template>
