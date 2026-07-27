<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue';
import { ElAlert, ElButton, ElEmpty, ElSkeleton, ElTag } from 'element-plus';
import {
  robotApi,
  type RobotApi,
  type RobotCallbackStatus,
  type RobotProbe,
  type RobotSettings
} from '../../api/robots';
import { formatBeijingTime } from '../../utils/beijingTime';

const props = withDefaults(defineProps<{ api?: RobotApi }>(), { api: () => robotApi });
const items = ref<RobotSettings[]>([]);
const loading = ref(true);
const busy = ref('');
const error = ref('');
const notice = ref('');
const publicBaseUrl = ref(window.location.origin);
const replyAll = ref(true);
const credentials = reactive<Record<string, string>>({});
const probes = reactive<Record<string, RobotProbe>>({});
const callbacks = reactive<Record<string, RobotCallbackStatus>>({});
const createOpen = ref(false);
const createName = ref('');
const createCredential = ref('');

async function load() {
  loading.value = true; error.value = '';
  try { items.value = await props.api.list(); }
  catch { error.value = '机器人设置加载失败。'; }
  finally { loading.value = false; }
}

async function save(item: RobotSettings) {
  busy.value = `${item.id}:save`; error.value = '';
  try {
    const saved = await props.api.save(item.id, {
      name: item.name,
      isEnabled: item.isEnabled,
      sendRateLimitPerMinute: item.sendRateLimitPerMinute,
      workToolRobotId: credentials[item.id]?.trim() || undefined,
      enableConfirmationToken: probes[item.id]?.enableConfirmationToken || undefined
    });
    items.value = items.value.map(value => value.id === saved.id ? saved : value);
    credentials[item.id] = '';
    notice.value = '机器人设置已保存。';
  } catch (exception) {
    const code = (exception as { response?: { data?: { error?: string } } }).response?.data?.error;
    error.value = code === 'robot-probe-required'
      ? '启用前必须先完成当前配置的连接测试。'
      : code === 'robot-disable-before-credential-rotation'
        ? '轮换机器人标识前必须先停用机器人。'
        : '机器人设置保存失败，请检查名称、标识和限流范围。';
  } finally { busy.value = ''; }
}

async function createRobot() {
  if (!createName.value.trim() || !createCredential.value.trim()) {
    error.value = '请输入机器人名称和 WorkTool 机器人 ID。'; return;
  }
  const id = crypto.randomUUID();
  busy.value = 'create';
  try {
    await props.api.save(id, {
      name: createName.value.trim(), workToolRobotId: createCredential.value.trim(),
      isEnabled: false, sendRateLimitPerMinute: 50
    });
    createOpen.value = false; createName.value = ''; createCredential.value = '';
    await load(); notice.value = '机器人已创建为停用状态，请测试连接后启用。';
  } catch { error.value = '机器人创建失败。'; }
  finally { busy.value = ''; }
}

async function probe(item: RobotSettings) {
  busy.value = `${item.id}:probe`; error.value = '';
  try { probes[item.id] = await props.api.probe(item.id); }
  catch { error.value = 'WorkTool 连接测试失败。'; }
  finally { busy.value = ''; }
}

async function queryCallbacks(item: RobotSettings) {
  busy.value = `${item.id}:callbacks`;
  try { callbacks[item.id] = await props.api.getCallbacks(item.id); }
  catch { error.value = '回调状态查询失败。'; }
  finally { busy.value = ''; }
}

async function configureMessage(item: RobotSettings) {
  busy.value = `${item.id}:message`;
  try {
    await props.api.configureMessageCallback(item.id, publicBaseUrl.value, replyAll.value);
    await queryCallbacks(item); notice.value = '消息回调已配置。';
  } catch { error.value = '消息回调配置失败。'; }
  finally { busy.value = ''; }
}

async function configureResult(item: RobotSettings) {
  busy.value = `${item.id}:result`;
  try {
    await props.api.configureCommandResultCallback(item.id, publicBaseUrl.value);
    await queryCallbacks(item); notice.value = '指令结果回调已配置。';
  } catch { error.value = '指令结果回调配置失败。'; }
  finally { busy.value = ''; }
}

onMounted(load);
</script>

<template>
  <section class="ops-page">
    <header class="page-header">
      <div><p class="eyebrow">WorkTool 接入</p><h1>机器人设置</h1>
        <p>机器人标识只允许写入和轮换，页面永不读取明文。</p></div>
      <ElButton data-testid="create-robot" type="primary" @click="createOpen = true">新增机器人</ElButton>
    </header>
    <ElAlert v-if="error" :title="error" type="error" :closable="false" />
    <ElAlert v-if="notice" :title="notice" type="success" :closable="false" />
    <ElSkeleton v-if="loading" :rows="5" animated />
    <ElEmpty v-else-if="!items.length" description="暂无机器人配置。" />
    <section v-else class="cards">
      <article v-for="item in items" :key="item.id" class="panel">
        <header><div><h2>{{ item.name }}</h2><small>{{ formatBeijingTime(item.updatedAtUtc) }}</small></div>
          <div><ElTag>{{ item.hasWorkToolRobotId ? '标识已配置' : '标识缺失' }}</ElTag>
            <ElTag :type="item.isEnabled ? 'success' : 'info'">{{ item.isEnabled ? '已启用' : '已停用' }}</ElTag></div></header>
        <label>机器人名称<input v-model="item.name" maxlength="128"></label>
        <label>发送限流<input v-model.number="item.sendRateLimitPerMinute" type="number" min="1" max="60"></label>
        <label>轮换 WorkTool 机器人 ID
          <input v-model="credentials[item.id]" type="password" autocomplete="new-password" placeholder="留空表示不修改">
        </label>
        <label><input v-model="item.isEnabled" type="checkbox"> 启用机器人（启用前需测试连接）</label>
        <div class="actions">
          <ElButton :data-testid="`probe-${item.id}`" @click="probe(item)">测试连接</ElButton>
          <ElButton type="primary" @click="save(item)">保存</ElButton>
          <ElButton @click="queryCallbacks(item)">查询回调状态</ElButton>
        </div>
        <div v-if="probes[item.id]" class="status-row">
          <ElTag :type="probes[item.id].reachable ? 'success' : 'danger'">{{ probes[item.id].reachable ? '可达' : '不可达' }}</ElTag>
          <ElTag :type="probes[item.id].online === true ? 'success' : 'info'">
            {{ probes[item.id].online === true ? '在线' : probes[item.id].online === false ? '离线' : '在线状态未知' }}
          </ElTag>
        </div>
        <div class="callback-box">
          <label>公网地址<input v-model="publicBaseUrl" type="url"></label>
          <label><input v-model="replyAll" type="checkbox"> 回复全部消息</label>
          <div class="actions">
            <ElButton :data-testid="`message-callback-${item.id}`" @click="configureMessage(item)">配置消息回调</ElButton>
            <ElButton :data-testid="`result-callback-${item.id}`" @click="configureResult(item)">配置指令结果回调</ElButton>
          </div>
          <p v-if="callbacks[item.id]">
            消息回调：{{ callbacks[item.id].messageCallbackConfigured ? '已配置' : '未配置' }}；
            结果回调：{{ callbacks[item.id].commandResultCallbackConfigured ? '已配置' : '未配置' }}；
            检查时间：{{ formatBeijingTime(callbacks[item.id].checkedAtUtc) }}
          </p>
        </div>
      </article>
    </section>
    <section v-if="createOpen" class="panel create-panel">
      <h2>新增机器人</h2>
      <label>名称<input v-model="createName"></label>
      <label>WorkTool 机器人 ID<input v-model="createCredential" type="password" autocomplete="new-password"></label>
      <div class="actions"><ElButton @click="createOpen = false">取消</ElButton><ElButton type="primary" @click="createRobot">创建为停用状态</ElButton></div>
    </section>
  </section>
</template>

<style scoped>
.ops-page,.cards,.panel{display:grid;gap:var(--space-lg)}.ops-page{max-width:1440px;margin:auto}.page-header,.panel>header,.actions,.status-row{display:flex;justify-content:space-between;align-items:center;gap:var(--space-md);flex-wrap:wrap}.cards{grid-template-columns:repeat(auto-fit,minmax(360px,1fr))}.panel{padding:var(--space-xl);border:1px solid var(--color-border);border-radius:.75rem;background:var(--color-surface)}label{display:grid;gap:var(--space-xs)}.callback-box{display:grid;gap:var(--space-md);padding:var(--space-lg);border-radius:.6rem;background:var(--color-background)}small{color:var(--color-muted-text)}.create-panel{position:fixed;z-index:50;inset:15% 25%;box-shadow:var(--shadow-lg)}
</style>
