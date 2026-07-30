import { describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import GroupAdvancedSettingsPanel from './GroupAdvancedSettingsPanel.vue';

describe('GroupAdvancedSettingsPanel', () => {
  it('keeps matching and preview collapsed by default for a WorkTool import', () => {
    const wrapper = mount(GroupAdvancedSettingsPanel, {
      props: {
        registrationSource: 'WorkToolImport',
        includeRules: [],
        excludeRules: [],
        previewResults: [],
        previewGroupNames: ''
      },
      global: {
        stubs: {
          RuleEditor: true,
          RulePreview: true
        }
      }
    });

    expect(wrapper.text()).toContain('当前群已通过 WorkTool 准确登记');
    expect(wrapper.find('[data-testid="advanced-rule-content"]').exists()).toBe(false);
    expect(wrapper.find('[data-testid="advanced-preview-content"]').exists()).toBe(false);
  });
});
