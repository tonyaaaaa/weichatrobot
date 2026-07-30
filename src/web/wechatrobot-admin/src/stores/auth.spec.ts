import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { createUnauthorizedHandler, useAuthStore } from './auth';
import LoginView from '../views/LoginView.vue';
import { flushPromises, mount } from '@vue/test-utils';

const user = {
  id: '13a7f6c8-0a12-4a2e-8d55-9984cef6bc22',
  email: 'admin@example.com',
  displayName: '管理员',
  roles: ['Admin']
};

describe('authentication state', () => {
  beforeEach(() => {
    localStorage.clear();
    setActivePinia(createPinia());
  });

  it('shows a safe login error when the server rejects credentials', async () => {
    const auth = useAuthStore();
    auth.setApi({
      login: vi.fn().mockRejectedValue(new Error('Request failed with status code 401')),
      me: vi.fn()
    });

    await expect(auth.login('admin@example.com', 'wrong-password')).resolves.toBe(false);

    expect(auth.loginError).toBe('邮箱或密码不正确。');
    expect(auth.isAuthenticated).toBe(false);
  });

  it('hydrates the current user after refresh when an access token exists', async () => {
    localStorage.setItem('wechatrobot.accessToken', 'existing-token');
    const auth = useAuthStore();
    auth.setApi({ login: vi.fn(), me: vi.fn().mockResolvedValue(user) });

    await auth.hydrate();

    expect(auth.user).toEqual(user);
    expect(auth.isAuthenticated).toBe(true);
  });

  it('clears the session when the HTTP client reports an unauthorized response', () => {
    localStorage.setItem('wechatrobot.accessToken', 'existing-token');
    const auth = useAuthStore();
    auth.restoreToken();

    auth.handleUnauthorized();

    expect(auth.user).toBeNull();
    expect(auth.isAuthenticated).toBe(false);
    expect(localStorage.getItem('wechatrobot.accessToken')).toBeNull();
  });

  it('clears the session and replaces the protected route after an unauthorized response', () => {
    localStorage.setItem('wechatrobot.accessToken', 'existing-token');
    const auth = useAuthStore();
    auth.restoreToken();
    const replaceWithLogin = vi.fn();

    createUnauthorizedHandler(auth, replaceWithLogin)();

    expect(auth.isAuthenticated).toBe(false);
    expect(localStorage.getItem('wechatrobot.accessToken')).toBeNull();
    expect(replaceWithLogin).toHaveBeenCalledOnce();
  });
});

describe('LoginView', () => {
  it('uses the WeChat Robot product name', () => {
    const wrapper = mount(LoginView, { global: { plugins: [createPinia()] } });

    expect(wrapper.get('h1').text()).toBe('微信机器人');
  });

  it('renders the authentication error without exposing HTTP details', async () => {
    const pinia = createPinia();
    const auth = useAuthStore(pinia);
    auth.setApi({
      login: vi.fn().mockRejectedValue(new Error('Request failed with status code 401')),
      me: vi.fn()
    });
    const wrapper = mount(LoginView, { global: { plugins: [pinia] } });

    await wrapper.get('input[type="email"]').setValue('admin@example.com');
    await wrapper.get('input[type="password"]').setValue('wrong-password');
    await wrapper.get('form').trigger('submit');
    await flushPromises();

    expect(await wrapper.find('[role="alert"]').text()).toBe('邮箱或密码不正确。');
    expect(wrapper.text()).not.toContain('401');
  });
});
