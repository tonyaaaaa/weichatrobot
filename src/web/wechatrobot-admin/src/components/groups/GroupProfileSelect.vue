<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { ElOption, ElSelect } from 'element-plus';
import {
  groupOptionApi,
  type GroupOption,
  type GroupOptionApi
} from '../../api/groupOptions';

const props = withDefaults(
  defineProps<{
    modelValue: string;
    api?: GroupOptionApi;
    placeholder?: string;
  }>(),
  {
    api: () => groupOptionApi,
    placeholder: '全部群'
  }
);
const emit = defineEmits<{
  'update:modelValue': [value: string];
  change: [value: string];
  'load-error': [];
}>();

const loading = ref(true);
const options = ref<GroupOption[]>([]);
const hasUnknownSelection = computed(() =>
  Boolean(props.modelValue)
  && !options.value.some(group => group.id === props.modelValue));

function optionLabel(group: GroupOption): string {
  const remark = group.workToolGroupRemark ? `（${group.workToolGroupRemark}）` : '';
  const state = group.state === 'archived'
    ? ' · 已归档'
    : group.state === 'disabled' ? ' · 已停用' : '';
  return `${group.name}${remark} · ${group.robotName}${state}`;
}

function select(value: string | null | undefined): void {
  const normalized = value ?? '';
  emit('update:modelValue', normalized);
  emit('change', normalized);
}

onMounted(async () => {
  try {
    options.value = await props.api.list();
  } catch {
    emit('load-error');
  } finally {
    loading.value = false;
  }
});
</script>

<template>
  <ElSelect
    class="group-profile-select"
    data-testid="group-profile-select"
    :model-value="modelValue"
    :loading="loading"
    :placeholder="placeholder"
    filterable
    clearable
    @update:model-value="select"
  >
    <ElOption
      v-if="hasUnknownSelection"
      :value="modelValue"
      label="群记录不存在或已删除"
      disabled
    />
    <ElOption
      v-for="group in options"
      :key="group.id"
      :value="group.id"
      :label="optionLabel(group)"
    />
  </ElSelect>
</template>

<style scoped>
.group-profile-select {
  width: 100%;
}
</style>
