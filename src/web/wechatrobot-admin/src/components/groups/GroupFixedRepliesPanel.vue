<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import {
  ElAlert,
  ElButton,
  ElDialog,
  ElEmpty,
  ElInput,
  ElMessage,
  ElSkeleton,
  ElTag
} from 'element-plus';
import { useRouter } from 'vue-router';
import {
  fixedReplyApi,
  type EffectiveFixedReply,
  type FixedReplyRoutePreview,
  type FixedReplyTemplate,
  type FixedReplyTemplateDraft
} from '../../api/fixedReplies';
import { confirmAction } from '../../utils/dialogs';
import FixedReplyTemplateDialog from '../../views/fixed-replies/FixedReplyTemplateDialog.vue';

const props = defineProps<{ groupId: string; groupName?: string }>();
const router = useRouter();
const loading = ref(true);
const error = ref('');
const templates = ref<FixedReplyTemplate[]>([]);
const effective = ref<EffectiveFixedReply[]>([]);
const dialogOpen = ref(false);
const editing = ref<FixedReplyTemplate>();
const saving = ref(false);
const previewOpen = ref(false);
const previewQuestion = ref('');
const previewing = ref(false);
const previewResult = ref<FixedReplyRoutePreview>();
const effectiveIds = computed(() => new Set(effective.value.map(item => item.id)));
const items = computed(() => templates.value);
const enabledCount = computed(() =>
  templates.value.filter(item => item.isEnabled && isEffective(item)).length);

async function load(): Promise<void> {
  loading.value = true; error.value = '';
  try {
    [templates.value, effective.value] = await Promise.all([
      fixedReplyApi.list({}),
      fixedReplyApi.listForGroup(props.groupId)
    ]);
  }
  catch { error.value = '当前群固定回复模板加载失败。'; }
  finally { loading.value = false; }
}
function isEffective(item: FixedReplyTemplate): boolean {
  return effectiveIds.value.has(item.id);
}

async function change(item: FixedReplyTemplate): Promise<void> {
  try {
    if (item.scopeType === 'Global') {
      if (isEffective(item)) {
        await fixedReplyApi.excludeForGroup(props.groupId, item.id, item.version);
      } else {
        await fixedReplyApi.removeExcludeForGroup(props.groupId, item.id, item.version);
      }
    } else if (isEffective(item)) {
      await fixedReplyApi.removeIncludeForGroup(props.groupId, item.id, item.version);
    } else {
      await fixedReplyApi.includeForGroup(props.groupId, item.id, item.version);
    }
    ElMessage.success('本群固定回复配置已更新');
    await load();
  } catch {
    ElMessage.error('更新失败，配置可能已被其他管理员修改，请刷新后重试。');
  }
}

function openCreate(): void {
  editing.value = undefined;
  dialogOpen.value = true;
}

async function openEdit(item: FixedReplyTemplate): Promise<void> {
  if ((item.scopeType === 'Global' || item.groupRules.length > 1)
      && !await confirmAction(
        `编辑“${item.name}”可能影响其他群，确认继续吗？`,
        { title: '编辑共享模板' })) {
    return;
  }
  editing.value = item;
  dialogOpen.value = true;
}

async function save(draft: FixedReplyTemplateDraft): Promise<void> {
  saving.value = true;
  try {
    if (editing.value) {
      await fixedReplyApi.update(
        editing.value.id,
        editing.value.version,
        draft);
    } else {
      await fixedReplyApi.create(draft);
    }
    dialogOpen.value = false;
    ElMessage.success('固定回复模板已保存');
    await load();
  } catch {
    ElMessage.error('保存失败，模板可能已被其他管理员修改，请刷新后重试。');
  } finally {
    saving.value = false;
  }
}

async function toggleEnabled(item: FixedReplyTemplate): Promise<void> {
  const action = item.isEnabled ? '停用' : '启用';
  if (!await confirmAction(
    `确认${action}“${item.name}”？此操作会影响模板绑定的所有群。`,
    { title: `${action}固定回复模板`, danger: item.isEnabled })) {
    return;
  }
  try {
    await fixedReplyApi.setEnabled(
      item.id,
      item.version,
      !item.isEnabled);
    ElMessage.success(`模板已${action}`);
    await load();
  } catch {
    ElMessage.error(`${action}失败，模板可能已被其他管理员修改。`);
  }
}

function openPreview(): void {
  previewQuestion.value = '';
  previewResult.value = undefined;
  previewOpen.value = true;
}

async function preview(): Promise<void> {
  const question = previewQuestion.value.trim();
  if (!question) {
    ElMessage.warning('请输入测试问题');
    return;
  }
  previewing.value = true;
  previewResult.value = undefined;
  try {
    previewResult.value = await fixedReplyApi.preview(
      props.groupId,
      question);
  } catch {
    ElMessage.error('测试匹配失败，请稍后重试。');
  } finally {
    previewing.value = false;
  }
}

function manage(): void {
  void router.push({
    name: 'fixed-replies',
    query: {
      groupId: props.groupId,
      ...(props.groupName ? { groupName: props.groupName } : {})
    }
  });
}
watch(() => props.groupId, load);
onMounted(load);
</script>
<template>
  <section class="fixed-panel">
    <header>
      <div>
        <h2>固定回复模板</h2>
        <p>明确匹配时优先原样回复；不确定时继续下面的知识库流程。</p>
      </div>
      <div class="header-actions">
        <ElButton
          data-testid="test-group-fixed-reply"
          @click="openPreview"
        >
          测试匹配
        </ElButton>
        <ElButton
          data-testid="manage-all-fixed-replies"
          @click="manage"
        >
          管理全部模板
        </ElButton>
        <ElButton
          type="primary"
          data-testid="create-group-fixed-reply"
          @click="openCreate"
        >
          新建当前群模板
        </ElButton>
      </div>
    </header>
    <div v-if="!loading && !error" class="template-summary">
      <span>当前群生效</span>
      <strong>{{ enabledCount }}</strong>
      <span>个模板</span>
    </div>
    <ElSkeleton v-if="loading" :rows="2" animated />
    <ElAlert v-else-if="error" :title="error" type="error" :closable="false"><ElButton @click="load">重试</ElButton></ElAlert>
    <ElEmpty v-else-if="!items.length" description="暂无固定回复模板，可为当前群新建一个" :image-size="70" />
    <div v-else class="template-list">
      <article v-for="item in items" :key="item.id">
        <div class="template-copy">
          <div>
            <strong>{{ item.name }}</strong>
            <ElTag v-if="!item.isEnabled" type="info" effect="plain">已停用</ElTag>
          </div>
          <p>{{ item.intentDescription }}</p>
        </div>
        <div class="template-actions">
          <ElTag
            :type="item.isEnabled && isEffective(item)
              ? (item.scopeType === 'SelectedGroups' ? 'primary' : 'success')
              : 'info'"
            effect="plain"
          >
            {{ !item.isEnabled
              ? '当前停用'
              : isEffective(item)
                ? (item.scopeType === 'SelectedGroups' ? '本群专属' : '全局模板 · 当前生效')
                : item.scopeType === 'Global' ? '全局模板 · 本群已排除' : '未在本群启用' }}
          </ElTag>
          <ElButton
            link
            type="primary"
            :data-testid="`edit-${item.id}`"
            @click="openEdit(item)"
          >
            编辑
          </ElButton>
          <ElButton
            link
            :type="item.isEnabled ? 'warning' : 'success'"
            :data-testid="`${item.isEnabled ? 'disable' : 'enable'}-${item.id}`"
            @click="toggleEnabled(item)"
          >
            {{ item.isEnabled ? '停用' : '启用' }}
          </ElButton>
          <ElButton
            v-if="item.scopeType === 'Global'"
            :data-testid="`${isEffective(item) ? 'exclude' : 'remove-exclude'}-${item.id}`"
            @click="change(item)"
          >
            {{ isEffective(item) ? '本群停用' : '取消排除' }}
          </ElButton>
          <ElButton
            v-else
            :data-testid="`${isEffective(item) ? 'remove-include' : 'include'}-${item.id}`"
            @click="change(item)"
          >
            {{ isEffective(item) ? '从本群移除' : '在本群启用' }}
          </ElButton>
        </div>
      </article>
    </div>
    <FixedReplyTemplateDialog
      v-model="dialogOpen"
      :template="editing"
      :initial-group-id="editing ? undefined : groupId"
      :saving="saving"
      @save="save"
    />
    <ElDialog
      v-model="previewOpen"
      :title="`测试当前群固定回复${groupName ? ` · ${groupName}` : ''}`"
      width="min(92vw, 640px)"
      destroy-on-close
    >
      <div class="preview-form">
        <label>
          成员问题
          <ElInput
            v-model="previewQuestion"
            data-testid="group-fixed-reply-question"
            type="textarea"
            :rows="4"
            maxlength="2000"
            show-word-limit
            placeholder="例如：签证还有多久出？"
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
        <ElButton
          type="primary"
          data-testid="run-group-fixed-reply-test"
          :loading="previewing"
          @click="preview"
        >
          运行真实匹配
        </ElButton>
      </template>
    </ElDialog>
  </section>
</template>
<style scoped>
.fixed-panel { display: grid; gap: 1rem; padding: var(--space-xl); border: 1px solid var(--color-border); border-radius: .9rem; background: var(--color-surface); }
header, article { display: flex; align-items: flex-start; justify-content: space-between; gap: 1rem; }
h2, p { margin: 0; } p { color: var(--color-muted-text); }
.header-actions, .template-actions { display: flex; flex-wrap: wrap; align-items: center; gap: .5rem; }
.template-summary { display: flex; align-items: baseline; gap: .35rem; color: var(--color-muted-text); }
.template-summary strong { color: var(--color-text); font-size: 1.25rem; }
.template-list { display: grid; gap: .75rem; }
article { padding: .9rem; border-radius: .6rem; background: var(--el-fill-color-light); }
.template-copy { display: grid; gap: .35rem; }
.template-copy > div { display: flex; flex-wrap: wrap; align-items: center; gap: .5rem; }
.preview-form, .preview-form label { display: grid; gap: 1rem; }
.preview-form label { font-weight: 600; }
@media (max-width: 600px) { header, article { flex-direction: column; } }
</style>
