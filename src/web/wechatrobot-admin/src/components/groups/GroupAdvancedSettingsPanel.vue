<script setup lang="ts">
import { ref } from 'vue';
import { ElAlert, ElButton, ElCollapse, ElCollapseItem, ElInput, ElTag } from 'element-plus';
import type { GroupRule, PatternKind } from '../../api/groups';
import RuleEditor from './RuleEditor.vue';
import RulePreview from './RulePreview.vue';

defineProps<{
  registrationSource: string;
  includeRules: GroupRule[];
  excludeRules: GroupRule[];
  previewResults: { groupName: string; isMatch: boolean; isExcluded: boolean }[];
  previewGroupNames: string;
  agentRuntime?: {
    intentRuntimeMode: string;
    answerRuntimeMode: string;
    templateRoutingRuntimeMode: string;
    editable: boolean;
  };
}>();
const emit = defineEmits<{
  add: [direction: 'include' | 'exclude', patternKind: PatternKind];
  remove: [direction: 'include' | 'exclude', index: number];
  'update:previewGroupNames': [value: string];
  preview: [];
}>();
const activeNames = ref<string[]>([]);
function runtimeLabel(value?: string): string {
  return ({
    Legacy: '现有逻辑',
    Shadow: '影子验证',
    AgentFramework: 'Agent 正式执行',
    Paused: '已暂停',
    Disabled: '未启用'
  } as Record<string, string>)[value ?? ''] ?? (value || '未知');
}
</script>

<template>
  <div class="advanced-panel">
    <ElAlert
      v-if="registrationSource === 'WorkToolImport'"
      title="当前群已通过 WorkTool 准确登记，无需配置匹配规则。"
      description="仅在兼容旧回调或名称无法准确关联时，才需要展开高级匹配。"
      type="info"
      :closable="false"
      show-icon
    />
    <section v-if="agentRuntime" class="runtime-status">
      <div>
        <span>群消息意图</span>
        <ElTag>{{ runtimeLabel(agentRuntime.intentRuntimeMode) }}</ElTag>
      </div>
      <div>
        <span>知识回答</span>
        <ElTag>{{ runtimeLabel(agentRuntime.answerRuntimeMode) }}</ElTag>
      </div>
      <div>
        <span>固定回复模板</span>
        <ElTag>{{ runtimeLabel(agentRuntime.templateRoutingRuntimeMode) }}</ElTag>
      </div>
      <p>这是系统级发布状态，只读展示；请在“智能回复诊断”中查看判断记录。</p>
    </section>
    <ElCollapse v-model="activeNames">
      <ElCollapseItem name="rules" title="匹配规则">
        <div v-if="activeNames.includes('rules')" data-testid="advanced-rule-content">
          <RuleEditor
            :include-rules="includeRules"
            :exclude-rules="excludeRules"
            @add="emit('add', $event[0], $event[1])"
            @remove="emit('remove', $event[0], $event[1])"
          />
        </div>
      </ElCollapseItem>
      <ElCollapseItem name="preview" title="保存前预览">
        <section v-if="activeNames.includes('preview')" class="preview-panel" data-testid="advanced-preview-content">
          <p>每行输入一个已知群名称，检查当前草稿是否会匹配或排除。</p>
          <label>已知群名称
            <ElInput
              :model-value="previewGroupNames"
              type="textarea"
              :rows="4"
              placeholder="例如：技术支持群&#10;售后服务群"
              @update:model-value="emit('update:previewGroupNames', String($event))"
            />
          </label>
          <ElButton type="primary" plain data-testid="preview-rules" @click="emit('preview')">预览匹配结果</ElButton>
          <RulePreview :results="previewResults" />
        </section>
      </ElCollapseItem>
    </ElCollapse>
  </div>
</template>

<style scoped>
.advanced-panel { display: grid; gap: var(--space-lg); }
.runtime-status { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: var(--space-md); padding: var(--space-lg); border: 1px solid var(--color-border); border-radius: .75rem; }
.runtime-status > div { display: grid; gap: var(--space-sm); align-content: start; }
.runtime-status p { grid-column: 1 / -1; margin: 0; color: var(--color-muted-text); }
.preview-panel { display: grid; gap: var(--space-lg); padding: var(--space-lg) 0; }
.preview-panel p { margin: 0; color: var(--color-muted-text); }
.preview-panel label { display: grid; gap: var(--space-sm); font-weight: 600; }
@media (max-width: 720px) { .runtime-status { grid-template-columns: 1fr; } .runtime-status p { grid-column: auto; } }
</style>
