<script setup lang="ts">
import { onMounted, ref } from 'vue';
import {
  ElAlert, ElButton, ElEmpty, ElForm, ElFormItem, ElInput, ElSkeleton, ElTag
} from 'element-plus';
import { modelApi, type ModelApi, type ModelConfiguration } from '../../api/models';

const props = withDefaults(defineProps<{ api?: ModelApi }>(), { api: () => modelApi });
const loading = ref(true); const busyName = ref(''); const error = ref(''); const notice = ref('');
const items = ref<ModelConfiguration[]>([]);
const apiKeys = ref<Record<string, string>>({});
async function load() {
  loading.value = true; error.value = '';
  try { items.value = await props.api.list(); } catch { error.value = '模型配置加载失败，请检查管理员权限和后端服务。'; }
  finally { loading.value = false; }
}
function masked(item: ModelConfiguration): string { return item.hasApiKey ? `••••${item.lastFour ?? '已保存'}` : '未配置'; }
async function test(name: string) {
  busyName.value = name; error.value = '';
  try { const result = await props.api.testConnection(name); notice.value = result.succeeded ? `${name} 连接测试成功。` : `${name} 连接测试未通过。`; }
  catch { error.value = `${name} 连接测试失败，请检查地址、模型名和密钥。`; } finally { busyName.value = ''; }
}
async function save(item: ModelConfiguration) {
  busyName.value = item.name; error.value = '';
  try {
    const updated = await props.api.save(item.name, {
      name: item.name, provider: item.provider, configurationType: item.configurationType, baseUrl: item.baseUrl,
      model: item.model, timeoutSeconds: item.timeoutSeconds, maxRetries: item.maxRetries, isEnabled: item.isEnabled,
      isDefault: item.isDefault, apiKey: apiKeys.value[item.name] || undefined
    });
    const index = items.value.findIndex(value => value.id === item.id);
    if (index >= 0) items.value[index] = updated;
    apiKeys.value[item.name] = '';
    notice.value = `${item.name} 已保存；浏览器未保留明文密钥。`;
  } catch { error.value = `${item.name} 保存失败，请检查字段后重试。`; } finally { busyName.value = ''; }
}
onMounted(load);
</script>

<template>
  <section class="ops-page" aria-labelledby="models-title">
    <header class="page-header"><div><p class="eyebrow">系统配置</p><h1 id="models-title">模型配置</h1><p>支持 OpenAI 兼容聊天与向量接口；服务端只返回密钥存在状态和末四位。</p></div><ElButton @click="load">刷新</ElButton></header>
    <ElSkeleton v-if="loading" :rows="6" animated aria-label="正在加载模型配置" /><ElAlert v-else-if="error && !items.length" :title="error" type="error" :closable="false" show-icon><ElButton @click="load">重试</ElButton></ElAlert>
    <section v-else class="panel">
      <ElEmpty v-if="!items.length" description="暂无模型配置。后端当前只提供按名称保存接口，新增表单将在明确名称规则后开放。" />
      <div v-else class="model-grid">
        <article v-for="item in items" :key="item.id" class="model-card">
          <header><div><h2>{{ item.name }}</h2><p>{{ item.configurationType }} · {{ item.provider }}</p></div><ElTag :type="item.isEnabled ? 'success' : 'info'" effect="plain">{{ item.isEnabled ? '已启用' : '已停用' }}</ElTag></header>
          <ElForm label-position="top">
          <ElFormItem label="接口地址"><ElInput :id="`base-url-${item.name}`" v-model="item.baseUrl" type="url" /></ElFormItem>
          <ElFormItem label="模型名称"><ElInput :id="`model-${item.name}`" v-model="item.model" /></ElFormItem>
          <ElFormItem label="新 API 密钥"><ElInput :id="`api-key-${item.name}`" v-model="apiKeys[item.name]" type="password" autocomplete="new-password" show-password /></ElFormItem>
          <p class="helper">当前密钥：<span class="mono">{{ masked(item) }}</span>。留空保存时服务端保留原密钥；密钥不会返回浏览器。</p>
          <div class="form-grid"><label>超时（秒）<input v-model.number="item.timeoutSeconds" type="number" min="1" max="300"></label><label>最大重试<input v-model.number="item.maxRetries" type="number" min="0" max="5"></label></div>
          <div class="check-row"><label><input v-model="item.isEnabled" type="checkbox"> 启用</label><label><input v-model="item.isDefault" type="checkbox"> 默认配置</label></div>
          <div class="actions"><ElButton :data-testid="`save-${item.name}`" type="primary" :loading="busyName === item.name" @click="save(item)">{{ busyName === item.name ? '保存中…' : '保存配置' }}</ElButton><ElButton :data-testid="`test-${item.name}`" :disabled="busyName === item.name" @click="test(item.name)">{{ busyName === item.name ? '测试中…' : '测试连接' }}</ElButton></div>
          </ElForm>
        </article>
      </div>
    </section>
    <ElAlert v-if="error && items.length" :title="error" type="error" :closable="false" show-icon /><p aria-live="polite">{{ notice }}</p>
  </section>
</template>
