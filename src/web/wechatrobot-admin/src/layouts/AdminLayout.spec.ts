import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { createPinia, setActivePinia } from 'pinia';
import { mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import AdminLayout from './AdminLayout.vue';
import { useAuthStore } from '../stores/auth';

describe('AdminLayout responsive navigation', () => {
  it('uses the WeChat Robot product name', () => {
    setActivePinia(createPinia());
    const wrapper = mount(AdminLayout, {
      global: { stubs: { RouterLink: { template: '<a><slot /></a>' }, RouterView: true } }
    });

    expect(wrapper.get('header strong').text()).toBe('微信机器人');
  });

  it('falls back to the account email when the stored display name is corrupted', async () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    const auth = useAuthStore(pinia);
    auth.setApi({
      login: async () => ({
        accessToken: 'token',
        tokenType: 'Bearer',
        expiresInSeconds: 900,
        user: {
          id: '13a7f6c8-0a12-4a2e-8d55-9984cef6bc22',
          email: 'admin@example.com',
          displayName: 'ϵͳ����Ա',
          roles: ['Admin']
        }
      }),
      me: async () => { throw new Error('not used'); }
    });
    await auth.login('admin@example.com', 'password');
    const wrapper = mount(AdminLayout, {
      global: {
        plugins: [pinia],
        stubs: { RouterLink: { template: '<a><slot /></a>' }, RouterView: true }
      }
    });

    expect(wrapper.get('header span').text()).toBe('admin@example.com');
  });

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
