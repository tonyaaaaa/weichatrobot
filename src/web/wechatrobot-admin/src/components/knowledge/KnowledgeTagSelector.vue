<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import {
  knowledgeTagApi,
  type KnowledgeTagApi,
  type KnowledgeTagOption
} from '../../api/knowledgeTags';

const props = withDefaults(defineProps<{
  modelValue: string[];
  api?: Pick<KnowledgeTagApi, 'options'>;
  disabled?: boolean;
  required?: boolean;
}>(), {
  api: () => knowledgeTagApi,
  disabled: false,
  required: false
});

const emit = defineEmits<{
  'update:modelValue': [value: string[]];
}>();

const options = ref<KnowledgeTagOption[]>([]);
const loading = ref(true);
const failed = ref(false);
const selected = computed(() => new Set(props.modelValue));
const showRequired = computed(() =>
  props.required && !loading.value && !failed.value && selected.value.size === 0);

async function load() {
  loading.value = true;
  failed.value = false;
  try {
    options.value = await props.api.options();
  } catch {
    options.value = [];
    failed.value = true;
  } finally {
    loading.value = false;
  }
}

function toggle(id: string, checked: boolean) {
  const next = new Set(props.modelValue);
  if (checked) {
    next.add(id);
  } else {
    next.delete(id);
  }
  emit(
    'update:modelValue',
    options.value
      .map(option => option.id)
      .filter(optionId => next.has(optionId))
  );
}

onMounted(load);
</script>

<template>
  <div class="knowledge-tag-selector">
    <p v-if="loading" class="selector-status" aria-live="polite">正在加载标签…</p>
    <p v-else-if="failed" class="selector-status selector-error" role="alert">
      标签加载失败，请刷新后重试。
    </p>
    <p v-else-if="options.length === 0" class="selector-status">当前没有可用标签</p>
    <fieldset v-else :disabled="disabled" class="tag-options">
      <legend class="visually-hidden">选择知识标签</legend>
      <label v-for="option in options" :key="option.id" class="tag-option">
        <input
          type="checkbox"
          :checked="selected.has(option.id)"
          :data-testid="`knowledge-tag-${option.id}`"
          @change="toggle(option.id, ($event.target as HTMLInputElement).checked)"
        >
        <span>{{ option.name }}{{ option.isGlobalPublic ? '（全局公开）' : '' }}</span>
      </label>
    </fieldset>
    <p
      v-if="showRequired"
      class="selector-required"
      data-testid="knowledge-tag-required"
      role="alert"
    >
      请至少选择一个知识标签。
    </p>
  </div>
</template>

<style scoped>
.knowledge-tag-selector {
  display: grid;
  gap: var(--space-sm);
}

.selector-status,
.selector-required {
  margin: 0;
}

.selector-status {
  color: var(--color-muted-text);
}

.selector-error,
.selector-required {
  color: var(--color-danger, #b42318);
}

.tag-options {
  display: grid;
  min-width: 0;
  margin: 0;
  padding: 0;
  border: 0;
  grid-template-columns: repeat(auto-fit, minmax(min(100%, 12rem), 1fr));
  gap: var(--space-sm);
}

.tag-option {
  display: flex;
  min-height: 44px;
  align-items: flex-start;
  gap: var(--space-sm);
  margin: 0;
  padding: var(--space-md);
  border: 1px solid var(--color-border);
  border-radius: .5rem;
  background: var(--color-background);
  cursor: pointer;
}

.tag-option input {
  width: 1.25rem;
  min-height: 1.25rem;
  margin: .125rem 0 0;
  flex: 0 0 auto;
}

.tag-options:disabled .tag-option {
  cursor: not-allowed;
  opacity: .65;
}

.visually-hidden {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
  white-space: nowrap;
  clip-path: inset(50%);
}
</style>
