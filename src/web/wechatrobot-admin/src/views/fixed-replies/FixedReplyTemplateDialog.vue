<script setup lang="ts">
import { reactive, ref, watch } from 'vue';
import {
  ElAlert, ElButton, ElDialog, ElForm, ElFormItem, ElInput, ElInputNumber,
  ElOption, ElSelect, ElSwitch
} from 'element-plus';
import { groupOptionApi, type GroupOption } from '../../api/groupOptions';
import type {
  FixedReplyTemplate,
  FixedReplyTemplateDraft
} from '../../api/fixedReplies';

const props = defineProps<{
  modelValue: boolean;
  template?: FixedReplyTemplate;
  saving?: boolean;
  initialGroupId?: string;
}>();
const emit = defineEmits<{
  'update:modelValue': [value: boolean];
  save: [draft: FixedReplyTemplateDraft];
}>();
const groups = ref<GroupOption[]>([]);
const groupLoadError = ref('');
const errors = ref<string[]>([]);
const selectedGroupIds = ref<string[]>([]);
const form = reactive<FixedReplyTemplateDraft>({
  name: '',
  intentDescription: '',
  replyText: '',
  scopeType: 'Global',
  priority: 0,
  isEnabled: true,
  examples: [''],
  groupRules: []
});

function reset(): void {
  Object.assign(form, props.template ? {
    name: props.template.name,
    intentDescription: props.template.intentDescription,
    replyText: props.template.replyText,
    scopeType: props.template.scopeType,
    priority: props.template.priority,
    isEnabled: props.template.isEnabled,
    examples: [...props.template.examples],
    groupRules: props.template.groupRules.map(rule => ({ ...rule }))
  } : {
    name: '',
    intentDescription: '',
    replyText: '',
    scopeType: props.initialGroupId ? 'SelectedGroups' : 'Global',
    priority: 0,
    isEnabled: true,
    examples: [''],
    groupRules: props.initialGroupId
      ? [{ groupProfileId: props.initialGroupId, effect: 'Include' }]
      : []
  });
  selectedGroupIds.value = form.groupRules.map(rule => rule.groupProfileId);
  errors.value = [];
}
async function loadGroups(): Promise<void> {
  groupLoadError.value = '';
  try {
    groups.value = await groupOptionApi.list();
  } catch {
    groups.value = [];
    groupLoadError.value = '群列表加载失败，请关闭弹框后重试。';
  }
}
function changeScope(): void {
  selectedGroupIds.value = [];
  form.groupRules = [];
}
function changeSelectedGroups(ids: string[]): void {
  selectedGroupIds.value = ids;
  form.groupRules = ids.map(groupProfileId => ({
    groupProfileId,
    effect: form.scopeType === 'Global' ? 'Exclude' : 'Include'
  }));
}
function submit(): void {
  const examples = form.examples.map(value => value.trim()).filter(Boolean);
  errors.value = [];
  if (!form.name.trim()) errors.value.push('请输入模板名称');
  if (!form.intentDescription.trim()) errors.value.push('请输入意图说明');
  if (!form.replyText.trim()) errors.value.push('请输入固定回复正文');
  if (!examples.length) errors.value.push('至少填写一条示例问法');
  if (form.scopeType === 'SelectedGroups' && !form.groupRules.length) {
    errors.value.push('指定群模板至少选择一个群');
  }
  if (errors.value.length) return;
  emit('save', { ...form, examples, groupRules: [...form.groupRules] });
}
watch(() => props.modelValue, value => {
  if (!value) return;
  reset();
  void loadGroups();
}, { immediate: true });
</script>

<template>
  <ElDialog
    :model-value="modelValue"
    class="fixed-reply-template-dialog"
    :title="template ? '编辑固定回复模板' : '新增固定回复模板'"
    width="min(720px, calc(100vw - 32px))"
    align-center
    destroy-on-close
    @update:model-value="emit('update:modelValue', $event)"
  >
    <ElAlert
      v-if="groupLoadError"
      :title="groupLoadError"
      type="error"
      :closable="false"
      show-icon
    />
    <ElForm label-position="top">
      <ElFormItem label="模板名称"><ElInput v-model="form.name" maxlength="128" show-word-limit /></ElFormItem>
      <ElFormItem label="意图说明"><ElInput v-model="form.intentDescription" type="textarea" :rows="3" /></ElFormItem>
      <ElFormItem label="固定回复正文"><ElInput v-model="form.replyText" type="textarea" :rows="5" /></ElFormItem>
      <ElFormItem label="示例问法">
        <div class="examples">
          <div v-for="(_, index) in form.examples" :key="index" class="example-row">
            <ElInput v-model="form.examples[index]" placeholder="例如：签证还有多久出来？" />
            <ElButton v-if="form.examples.length > 1" @click="form.examples.splice(index, 1)">删除</ElButton>
          </div>
          <ElButton @click="form.examples.push('')">添加示例</ElButton>
        </div>
      </ElFormItem>
      <div class="two-columns">
        <ElFormItem label="生效范围">
          <ElSelect v-model="form.scopeType" @change="changeScope">
            <ElOption label="全局模板" value="Global" />
            <ElOption label="指定群模板" value="SelectedGroups" />
          </ElSelect>
        </ElFormItem>
        <ElFormItem label="优先级"><ElInputNumber v-model="form.priority" :min="-1000" :max="1000" /></ElFormItem>
      </div>
      <ElFormItem :label="form.scopeType === 'Global' ? '排除群（可选）' : '生效群'">
        <ElSelect
          :model-value="selectedGroupIds"
          multiple
          filterable
          clearable
          class="full-width"
          placeholder="按群名称选择"
          @update:model-value="changeSelectedGroups"
        >
          <ElOption v-for="group in groups" :key="group.id" :label="group.name" :value="group.id" />
        </ElSelect>
      </ElFormItem>
      <ElFormItem label="启用模板"><ElSwitch v-model="form.isEnabled" /></ElFormItem>
      <ul v-if="errors.length" class="errors"><li v-for="error in errors" :key="error">{{ error }}</li></ul>
    </ElForm>
    <template #footer>
      <ElButton @click="emit('update:modelValue', false)">取消</ElButton>
      <ElButton type="primary" :loading="saving" @click="submit">保存</ElButton>
    </template>
  </ElDialog>
</template>

<style scoped>
.examples { display: grid; width: 100%; gap: .75rem; }
.example-row { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: .5rem; }
.two-columns { display: grid; grid-template-columns: 1fr 1fr; gap: 1rem; }
.full-width { width: 100%; }
.errors { margin: 0; color: var(--el-color-danger); }
:global(.fixed-reply-template-dialog) {
  display: flex;
  max-height: calc(100vh - 32px);
  flex-direction: column;
  overflow: hidden;
}
:global(.fixed-reply-template-dialog .el-dialog__body) {
  min-height: 0;
  overflow-y: auto;
}
@media (max-width: 600px) {
  .two-columns, .example-row { grid-template-columns: 1fr; }
}
</style>
