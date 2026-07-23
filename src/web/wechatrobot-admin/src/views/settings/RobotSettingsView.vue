<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { ElAlert, ElButton, ElEmpty, ElSkeleton, ElTag } from 'element-plus';
import { robotApi, type RobotSettings } from '../../api/robots';

const items = ref<RobotSettings[]>([]);
const loading = ref(true);
const busy = ref('');
const error = ref('');
const notice = ref('');

async function load() {
  loading.value = true; error.value = '';
  try { items.value = await robotApi.list(); }
  catch { error.value = '机器人设置加载失败。'; }
  finally { loading.value = false; }
}
async function save(item: RobotSettings) {
  busy.value = item.id; error.value = '';
  try {
    const saved = await robotApi.save(item);
    items.value = items.value.map(value => value.id === saved.id ? saved : value);
    notice.value = '机器人设置已保存。';
  } catch { error.value = '机器人设置保存失败，请检查名称和限流范围。'; }
  finally { busy.value = ''; }
}
onMounted(load);
</script>

<template>
  <section class="ops-page" aria-labelledby="robot-settings-title">
    <header class="page-header"><div><p class="eyebrow">机器人运行配置</p><h1 id="robot-settings-title">机器人设置</h1><p>管理启用状态和安全发送限流；页面不会返回 WorkTool 机器人标识或回调密钥。</p></div></header>
    <ElSkeleton v-if="loading" :rows="4" animated />
    <ElAlert v-else-if="error && !items.length" :title="error" type="error" :closable="false" />
    <ElEmpty v-else-if="!items.length" description="暂无机器人配置。" />
    <section v-else class="panel">
      <article v-for="item in items" :key="item.id" class="model-card">
        <header><h2>{{ item.name }}</h2><ElTag effect="plain">{{ item.isEnabled ? '已启用' : '已停用' }}</ElTag></header>
        <label :for="`robot-name-${item.id}`">机器人名称</label>
        <input :id="`robot-name-${item.id}`" v-model="item.name" aria-label="机器人名称" maxlength="128">
        <label :for="`robot-rate-${item.id}`">发送限流</label>
        <input :id="`robot-rate-${item.id}`" v-model.number="item.sendRateLimitPerMinute" aria-label="发送限流" type="number" min="1" max="60">
        <label><input v-model="item.isEnabled" type="checkbox"> 启用机器人</label>
        <ElButton type="primary" :data-testid="`save-robot-${item.id}`" :loading="busy === item.id" @click="save(item)">保存机器人设置</ElButton>
      </article>
    </section>
    <ElAlert v-if="error && items.length" :title="error" type="error" :closable="false" />
    <p aria-live="polite">{{ notice }}</p>
  </section>
</template>
