import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import PublicOssWarning from './PublicOssWarning.vue';

describe('PublicOssWarning', () => {
  it('explains that a public OSS URL is not a document permission boundary', () => {
    const wrapper = mount(PublicOssWarning);

    expect(wrapper.text()).toContain('公共读 OSS');
    expect(wrapper.text()).toContain('不构成文档访问权限控制');
  });
});
