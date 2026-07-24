import { computed, ref } from 'vue';
import { defineStore } from 'pinia';
import { accessTokenStorageKey, authApi, type AuthApi, type CurrentUser } from '../api/http';

export function createUnauthorizedHandler(
  auth: { handleUnauthorized(): void },
  replaceWithLogin: () => void | Promise<unknown>
): () => void {
  return () => {
    auth.handleUnauthorized();
    void replaceWithLogin();
  };
}

export const useAuthStore = defineStore('auth', () => {
  const accessToken = ref<string | null>(null);
  const user = ref<CurrentUser | null>(null);
  const loginError = ref<string | null>(null);
  let api: AuthApi = authApi;

  const isAuthenticated = computed(() => accessToken.value !== null && user.value !== null);

  function setApi(value: AuthApi): void { api = value; }
  function restoreToken(): void { accessToken.value = localStorage.getItem(accessTokenStorageKey); }
  function persistToken(token: string): void {
    accessToken.value = token;
    localStorage.setItem(accessTokenStorageKey, token);
  }
  function handleUnauthorized(): void {
    accessToken.value = null;
    user.value = null;
    loginError.value = null;
    localStorage.removeItem(accessTokenStorageKey);
  }
  async function login(email: string, password: string): Promise<boolean> {
    loginError.value = null;
    try {
      const response = await api.login(email, password);
      persistToken(response.accessToken);
      user.value = response.user;
      return true;
    } catch {
      handleUnauthorized();
      loginError.value = '邮箱或密码不正确。';
      return false;
    }
  }
  async function hydrate(): Promise<void> {
    restoreToken();
    if (!accessToken.value) return;
    try { user.value = await api.me(); } catch { handleUnauthorized(); }
  }

  return { accessToken, user, loginError, isAuthenticated, setApi, restoreToken, handleUnauthorized, login, hydrate };
});
