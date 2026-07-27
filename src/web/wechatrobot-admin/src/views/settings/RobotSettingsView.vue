<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue';
import { ElAlert, ElButton, ElEmpty, ElSkeleton, ElSwitch, ElTag } from 'element-plus';
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

function credentialSaveRequired(item: RobotSettings) {
  if (credentials[item.id]?.trim()) return '新机器人 ID 尚未保存，请先保存后再测试连接。';
  if (!item.hasWorkToolRobotId) return '请先填写并保存 WorkTool 机器人 ID。';
  return '';
}

async function probe(item: RobotSettings) {
  const blockedReason = credentialSaveRequired(item);
  if (blockedReason) {
    error.value = blockedReason;
    return;
  }
  busy.value = `${item.id}:probe`; error.value = '';
  try { probes[item.id] = await props.api.probe(item.id); }
  catch (exception) {
    const code = (exception as { response?: { data?: { error?: string } } }).response?.data?.error;
    error.value = code === 'worktool-credential-required'
      ? '请先填写并保存 WorkTool 机器人 ID。'
      : 'WorkTool 连接测试失败。';
  }
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
        <label class="field">机器人名称<input v-model="item.name" maxlength="128"></label>
        <label class="field">发送限流<input v-model.number="item.sendRateLimitPerMinute" type="number" min="1" max="60"></label>
        <label class="field">轮换 WorkTool 机器人 ID
          <input
            v-model="credentials[item.id]"
            :data-testid="`credential-${item.id}`"
            type="password"
            autocomplete="new-password"
            placeholder="留空表示不修改"
          >
        </label>
        <div class="setting-row runtime-setting">
          <div class="setting-copy">
            <strong>机器人运行状态</strong>
            <p>停用后不会用于消息发送和群操作，配置仍会保留。</p>
          </div>
          <ElSwitch
            v-model="item.isEnabled"
            :data-testid="`enabled-${item.id}`"
            inline-prompt
            active-text="启用"
            inactive-text="停用"
          />
        </div>
        <div v-if="credentialSaveRequired(item)" class="credential-hint" role="status">
          {{ credentialSaveRequired(item) }}
        </div>
        <div class="actions primary-actions">
          <ElButton
            :data-testid="`probe-${item.id}`"
            :disabled="Boolean(credentialSaveRequired(item))"
            :loading="busy === `${item.id}:probe`"
            @click="probe(item)"
          >测试连接</ElButton>
          <ElButton type="primary" :loading="busy === `${item.id}:save`" @click="save(item)">保存设置</ElButton>
        </div>
        <div v-if="probes[item.id]" class="status-row">
          <ElTag :type="probes[item.id].reachable ? 'success' : 'danger'">{{ probes[item.id].reachable ? '可达' : '不可达' }}</ElTag>
          <ElTag type="info">在线状态：WorkTool 官方未提供可靠结果</ElTag>
        </div>
        <div class="callback-box">
          <div class="callback-heading">
            <div class="setting-copy">
              <strong>回调配置</strong>
              <p>配置 WorkTool 向当前系统推送消息和指令结果。</p>
            </div>
            <ElButton
              :disabled="Boolean(credentialSaveRequired(item))"
              :loading="busy === `${item.id}:callbacks`"
              @click="queryCallbacks(item)"
            >查询回调状态</ElButton>
          </div>
          <label class="field">公网地址<input v-model="publicBaseUrl" type="url"></label>
          <div class="setting-row">
            <div class="setting-copy"><strong>回复全部消息</strong><p>开启后 WorkTool 会将所有收到的消息推送到回调地址。</p></div>
            <ElSwitch v-model="replyAll" active-text="开启" inactive-text="关闭" />
          </div>
          <div class="actions">
            <ElButton
              :data-testid="`message-callback-${item.id}`"
              :disabled="Boolean(credentialSaveRequired(item))"
              @click="configureMessage(item)"
            >配置消息回调</ElButton>
            <ElButton
              :data-testid="`result-callback-${item.id}`"
              :disabled="Boolean(credentialSaveRequired(item))"
              @click="configureResult(item)"
            >配置指令结果回调</ElButton>
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
.ops-page,.cards,.panel{display:grid;gap:var(--space-lg)}.ops-page{max-width:1440px;margin:auto}.page-header,.panel>header,.callback-heading{display:flex;justify-content:space-between;align-items:center;gap:var(--space-md);flex-wrap:wrap}.actions,.status-row{display:flex;align-items:center;gap:var(--space-md);flex-wrap:wrap}.cards{grid-template-columns:repeat(auto-fit,minmax(360px,1fr))}.panel{padding:var(--space-xl);border:1px solid var(--color-border);border-radius:.75rem;background:var(--color-surface)}.field{display:grid;gap:var(--space-xs)}.setting-row{display:flex;align-items:center;justify-content:space-between;gap:var(--space-lg);min-height:44px;padding:var(--space-md);border:1px solid var(--color-border);border-radius:.6rem}.setting-copy{display:grid;gap:var(--space-xs)}.setting-copy p{margin:0;color:var(--color-muted-text);line-height:1.5}.credential-hint{padding:var(--space-sm) var(--space-md);border-radius:.5rem;background:var(--color-background);color:var(--color-muted-text)}.primary-actions{justify-content:flex-end}.callback-box{display:grid;gap:var(--space-md);padding:var(--space-lg);border-radius:.6rem;background:var(--color-background)}small{color:var(--color-muted-text)}.create-panel{position:fixed;z-index:50;inset:15% 25%;box-shadow:var(--shadow-lg)}
@media (max-width:640px){.setting-row{align-items:flex-start}.primary-actions{justify-content:stretch}.primary-actions :deep(.el-button){flex:1}.create-panel{inset:5%}}
</style>
