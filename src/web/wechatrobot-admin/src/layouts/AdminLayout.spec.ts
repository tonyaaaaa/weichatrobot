import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createPinia, setActivePinia } from 'pinia';
import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import AdminLayout from './AdminLayout.vue';

describe('AdminLayout responsive navigation', () => {
  it('provides an accessible compact-screen navigation toggle', async () => {
    setActivePinia(createPinia());
    const wrapper = mount(AdminLayout, {
      global: { stubs: { RouterLink: { template: '<a><slot /></a>' }, RouterView: true } }
    });
    const toggle = wrapper.get('[data-testid="navigation-toggle"]');
    expect(toggle.attributes('aria-expanded')).toBe('false');
    await toggle.trigger('click');
    expect(toggle.attributes('aria-expanded')).toBe('true');
    expect(wrapper.get('nav').classes()).toContain('is-open');
  });

  it('keeps the compact navigation toggle visible on hover and keyboard focus', () => {
    const source = readFileSync(join(process.cwd(), 'src', 'layouts', 'AdminLayout.vue'), 'utf8');

    expect(source).toContain('.nav-toggle:hover:not(.is-disabled)');
    expect(source).toContain('.nav-toggle:focus-visible');
  });

  it('does not repeat the public-read OSS warning above every administration page', () => {
    setActivePinia(createPinia());
    const wrapper = mount(AdminLayout, {
      global: { stubs: { RouterLink: { template: '<a><slot /></a>' }, RouterView: true } }
    });

    expect(wrapper.text()).not.toContain('公共读 OSS 风险提示');
  });
});
