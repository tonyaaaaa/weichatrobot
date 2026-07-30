<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { ElAlert, ElButton, ElDialog, ElEmpty, ElInput, ElMessage, ElTable, ElTableColumn, ElTag } from 'element-plus';
import { useRoute } from 'vue-router';
import { fixedReplyApi, type FixedReplyRoutePreview, type FixedReplyTemplate, type FixedReplyTemplateDraft } from '../../api/fixedReplies';
import { groupOptionApi } from '../../api/groupOptions';
import GroupProfileSelect from '../../components/groups/GroupProfileSelect.vue';
import { confirmAction } from '../../utils/dialogs';
import FixedReplyTemplateDialog from './FixedReplyTemplateDialog.vue';

const route = useRoute();
const groupIdFilter = computed(() =>
  typeof route.query.groupId === 'string' ? route.query.groupId : '');
const groupNameFilter = computed(() =>
  typeof route.query.groupName === 'string'
    ? route.query.groupName
    : '当前群');
const items = ref<FixedReplyTemplate[]>([]);
const loading = ref(true);
const error = ref('');
const search = ref('');
const dialogOpen = ref(false);
const editing = ref<FixedReplyTemplate>();
const saving = ref(false);
const previewOpen = ref(false);
const previewGroupId = ref(groupIdFilter.value);
const previewQuestion = ref('');
const previewing = ref(false);
const previewResult = ref<FixedReplyRoutePreview>();
async function load(): Promise<void> {
  loading.value = true; error.value = '';
  try {
    items.value = await fixedReplyApi.list({
      search: search.value || undefined,
      groupProfileId: groupIdFilter.value || undefined
    });
  }
  catch { error.value = '固定回复模板加载失败，请重试。'; }
  finally { loading.value = false; }
}
function openCreate(): void { editing.value = undefined; dialogOpen.value = true; }
function openEdit(item: FixedReplyTemplate): void { editing.value = item; dialogOpen.value = true; }
function asTemplate(value: unknown): FixedReplyTemplate { return value as FixedReplyTemplate; }
async function save(draft: FixedReplyTemplateDraft): Promise<void> {
  saving.value = true;
  try {
    const saved = editing.value
      ? await fixedReplyApi.update(editing.value.id, editing.value.version, draft)
      : await fixedReplyApi.create(draft);
    const index = items.value.findIndex(item => item.id === saved.id);
    if (index < 0) items.value.unshift(saved); else items.value.splice(index, 1, saved);
    dialogOpen.value = false; ElMessage.success('固定回复模板已保存');
  } catch { ElMessage.error('保存失败，配置可能已被其他管理员修改，请刷新后重试。'); }
  finally { saving.value = false; }
}
async function remove(item: FixedReplyTemplate): Promise<void> {
  if (!await confirmAction(`确认删除“${item.name}”？历史审计仍会保留。`, { title: '删除固定回复模板', danger: true })) return;
  await fixedReplyApi.remove(item.id, item.version); await load();
}
async function previewRoute(): Promise<void> {
  if (!previewGroupId.value || !previewQuestion.value.trim()) {
    ElMessage.warning('请选择群并输入测试问题。');
    return;
  }
  previewing.value = true;
  previewResult.value = undefined;
  try {
    previewResult.value = await fixedReplyApi.preview(
      previewGroupId.value,
      previewQuestion.value.trim());
  } catch {
    ElMessage.error('测试匹配失败，请检查模型 Agent 能力和服务状态。');
  } finally {
    previewing.value = false;
  }
}
onMounted(load);
</script>

<template>
  <section class="ops-page">
    <header class="page-header">
      <div><p class="eyebrow">知识与回答</p><h1>固定回复模板</h1><p>明确命中模板意图时原样回复；不确定时继续知识库问答。</p></div>
      <div class="header-actions">
        <ElButton data-testid="preview-fixed-reply" @click="previewOpen = true">测试匹配</ElButton>
        <ElButton type="primary" data-testid="create-fixed-reply" @click="openCreate">新增模板</ElButton>
      </div>
    </header>
    <ElAlert
      v-if="groupIdFilter"
      type="info"
      :closable="false"
      show-icon
      :title="`当前群筛选：${groupNameFilter}`"
      description="正在查看与当前群有关的全局模板和指定群模板。"
    />
    <div class="toolbar"><ElInput v-model="search" clearable placeholder="搜索名称或意图" @keyup.enter="load" /><ElButton @click="load">查询</ElButton></div>
    <ElAlert v-if="error" :title="error" type="error" :closable="false" show-icon />
    <ElEmpty v-else-if="!loading && !items.length" description="暂无固定回复模板" />
    <ElTable v-else v-loading="loading" :data="items">
      <ElTableColumn prop="name" label="模板名称" min-width="150" />
      <ElTableColumn prop="intentDescription" label="匹配意图" min-width="240" show-overflow-tooltip />
      <ElTableColumn label="范围" width="120"><template #default="{ row }">{{ row.scopeType === 'Global' ? '全局' : '指定群' }}</template></ElTableColumn>
      <ElTableColumn prop="priority" label="优先级" width="90" />
      <ElTableColumn label="状态" width="90"><template #default="{ row }"><ElTag :type="row.isEnabled ? 'success' : 'info'">{{ row.isEnabled ? '启用' : '停用' }}</ElTag></template></ElTableColumn>
      <ElTableColumn label="操作" width="170" fixed="right"><template #default="{ row }"><ElButton link type="primary" @click="openEdit(asTemplate(row))">编辑</ElButton><ElButton link type="danger" @click="remove(asTemplate(row))">删除</ElButton></template></ElTableColumn>
    </ElTable>
    <FixedReplyTemplateDialog
      v-model="dialogOpen"
      :template="editing"
      :initial-group-id="editing ? undefined : groupIdFilter || undefined"
      :saving="saving"
      @save="save"
    />
    <ElDialog v-model="previewOpen" title="测试固定回复匹配" width="min(92vw, 680px)">
      <div class="preview-form">
        <label>测试群
          <GroupProfileSelect
            v-model="previewGroupId"
            :api="groupOptionApi"
            placeholder="请选择测试群"
          />
        </label>
        <label>成员问题
          <ElInput
            v-model="previewQuestion"
            type="textarea"
            :rows="4"
            maxlength="2000"
            show-word-limit
            placeholder="例如：我的签证还有多久出来？"
          />
        </label>
        <ElAlert
          v-if="previewResult"
          :type="previewResult.matched ? 'success' : 'info'"
          :closable="false"
          :title="previewResult.matched
            ? `命中模板：${previewResult.templateName}`
            : '未命中固定模板，将继续知识库问答'"
        >
          <p v-if="previewResult.matched">{{ previewResult.replyText }}</p>
          <code v-else>{{ previewResult.reasonCode || 'no_exact_match' }}</code>
        </ElAlert>
      </div>
      <template #footer>
        <ElButton @click="previewOpen = false">关闭</ElButton>
        <ElButton type="primary" :loading="previewing" @click="previewRoute">运行真实匹配</ElButton>
      </template>
    </ElDialog>
  </section>
</template>
<style scoped>
.toolbar { display: grid; grid-template-columns: minmax(220px, 420px) auto; gap: .75rem; margin-bottom: 1rem; }
.header-actions { display: flex; flex-wrap: wrap; gap: .5rem; }
.preview-form { display: grid; gap: 1rem; }
.preview-form label { display: grid; gap: .4rem; font-weight: 600; }
@media (max-width: 600px) { .toolbar { grid-template-columns: 1fr; } }
</style>
