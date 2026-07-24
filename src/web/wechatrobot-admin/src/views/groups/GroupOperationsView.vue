<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue';
import { workToolOperationsApi, type GroupOperation, type KnownGroup, type WorkToolOperationAudit, type WorkToolOperationsApi } from '../../api/worktool';

const props = withDefaults(defineProps<{ api?: WorkToolOperationsApi }>(), { api: () => workToolOperationsApi });
const notice = ref(''); const confirmationToken = ref(''); const audit = ref<WorkToolOperationAudit[]>([]);
const knownGroups = ref<KnownGroup[]>([]);
const registration = reactive({ robotConfigId: '', name: '', workToolGroupRemark: '', manualInvitationCompleted: false });
const operation = reactive<GroupOperation>({ robotConfigId: '', kind: 'Create', groupIdentifier: '', memberDisplayNames: [], value: '' });
async function register() {
  if (!registration.manualInvitationCompleted) { notice.value = '请先由人工在企业微信中邀请机器人入群，然后确认已完成邀请。'; return; }
  await props.api.registerExistingGroup({ ...registration }); notice.value = '已有群已登记。'; await loadKnownGroups();
}
async function preview() { const result = await props.api.preview({ ...operation, memberDisplayNames: operation.memberDisplayNames }); confirmationToken.value = result.confirmationToken; notice.value = `已生成确认令牌，${new Date(result.expiresAtUtc).toLocaleString()} 前确认执行。`; }
async function execute() { if (!confirmationToken.value) { notice.value = '请先预览，再确认执行。'; return; } const result = await props.api.execute({ ...operation, memberDisplayNames: operation.memberDisplayNames }, confirmationToken.value); notice.value = result.message; confirmationToken.value = ''; await loadAudit(); }
async function loadAudit() { audit.value = await props.api.listOperations(); }
async function loadKnownGroups() { knownGroups.value = await props.api.listGroups(); }
function selectKnownGroup(group: KnownGroup) { operation.robotConfigId = group.robotConfigId; operation.groupIdentifier = group.workToolGroupRemark || group.name; notice.value = `已选择已登记群：${group.name}。`; }
function statusCopy(status: string) {
  const copies: Record<string, string> = {
    queued: '等待派发',
    dispatching: '正在提交给 WorkTool',
    dispatchFailed: '派发失败，WorkTool 未接受',
    rejected: 'WorkTool 已拒绝',
    accepted: 'WorkTool 已接受，等待机器人执行结果',
    executedSucceeded: '机器人执行成功',
    executedPartially: '机器人部分执行成功',
    executedFailed: '机器人执行失败',
    deliveryUnknown: '投递结果未知',
    resultTimeout: '等待执行结果超时',
    Previewed: '已预览，等待确认',
    Rejected: '请求已拒绝'
  };
  return copies[status] || status;
}
onMounted(() => Promise.all([loadAudit(), loadKnownGroups()]));
</script>

<template>
  <section class="group-operations-view">
    <h1>群操作</h1>
    <section><h2>登记已有群</h2><p>第一步必须由人工在企业微信中邀请机器人入群；系统不能替代该邀请。</p>
      <label>机器人配置 ID <input v-model="registration.robotConfigId" aria-label="已有群机器人配置 ID"></label><label>群名称 <input v-model="registration.name"></label><label>WorkTool 群备注（可选）<input v-model="registration.workToolGroupRemark"></label>
      <label><input v-model="registration.manualInvitationCompleted" data-testid="manual-invitation-completed" type="checkbox"> 我已由人工在企业微信中完成机器人入群邀请</label><button data-testid="register-existing-group" type="button" @click="register">登记已有群</button>
    </section>
    <section><h2>已登记群</h2><p>选择后自动带入 WorkTool 群备注（如有）或群名称，以及机器人配置。</p><p v-if="!knownGroups.length">暂无已登记群。</p><ul v-else><li v-for="group in knownGroups" :key="group.id"><button type="button" :data-testid="`select-known-group-${group.id}`" @click="selectKnownGroup(group)">{{ group.name }}<template v-if="group.workToolGroupRemark">（{{ group.workToolGroupRemark }}）</template></button></li></ul></section>
    <section><h2>新建或调整群</h2><p>先预览，再使用两分钟内有效、且与当前内容绑定的确认令牌执行。</p>
      <label>机器人配置 ID <input v-model="operation.robotConfigId" data-testid="operation-robot-config-id"></label><label>操作 <select v-model="operation.kind"><option value="Create">新建外部群</option><option value="AddMembers">添加成员</option><option value="RemoveMembers">移除成员</option><option value="Rename">改群名</option><option value="UpdateAnnouncement">更新群公告</option></select></label>
      <label>群名称（如已设置群备注，请填备注名）<input v-model="operation.groupIdentifier" data-testid="operation-group-name"></label><label>成员显示名（每行一个）<textarea :value="operation.memberDisplayNames.join('\n')" @input="operation.memberDisplayNames = ($event.target as HTMLTextAreaElement).value.split('\n').map(item => item.trim()).filter(Boolean)" /></label><p>成员显示名由 WorkTool 按名称执行，不是稳定 ID；重名或改名可能导致操作目标不唯一，请在预览时复核。</p><label>新值 <textarea v-model="operation.value" /></label>
      <button type="button" @click="preview">预览操作</button><button type="button" :disabled="!confirmationToken" @click="execute">确认执行</button>
    </section>
    <p aria-live="polite">{{ notice }}</p><section><h2>命令状态</h2><p>WorkTool 接受命令不代表机器人已经执行成功；最终结果以命令结果回调为准。审计仅覆盖会改变群状态的 WorkTool 206/207 指令；连接测试是非变更健康检查，不作为群命令审计记录。</p><button type="button" @click="loadAudit">刷新</button><ul><li v-for="item in audit" :key="`${item.createdAtUtc}-${item.operation}`">{{ item.createdAtUtc }} · 指令 {{ item.workToolCommandNumber }} · {{ item.operation }} · {{ statusCopy(item.status) }} {{ item.result }}</li></ul></section>
  </section>
</template>
