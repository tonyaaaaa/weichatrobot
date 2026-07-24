import axios, { type AxiosInstance } from 'axios';

export const accessTokenStorageKey = 'wechatrobot.accessToken';

export interface CurrentUser {
  id: string;
  email: string;
  displayName: string;
  roles: string[];
}

export interface LoginResponse {
  accessToken: string;
  tokenType: string;
  expiresInSeconds: number;
  user: CurrentUser;
}

export interface AuthApi {
  login(email: string, password: string): Promise<LoginResponse>;
  me(): Promise<CurrentUser>;
}

function isTrustedApiRequest(
  url: string | undefined,
  effectiveBaseUrl: string | undefined,
  configuredApiBaseUrl: string
): boolean {
  const currentOrigin = window.location.origin;
  const trustedBase = new URL(configuredApiBaseUrl || currentOrigin, currentOrigin);
  if (effectiveBaseUrl !== undefined
    && new URL(effectiveBaseUrl || currentOrigin, currentOrigin).href !== trustedBase.href) {
    return false;
  }

  const requestUrl = new URL(url ?? '', trustedBase);
  return requestUrl.origin === trustedBase.origin
    && (requestUrl.pathname === '/api' || requestUrl.pathname.startsWith('/api/'));
}

export function createApiClient(
  getAccessToken: () => string | null,
  onUnauthorized: () => void,
  apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? ''
): AxiosInstance {
  const client = axios.create({ baseURL: apiBaseUrl });
  client.interceptors.request.use(config => {
    const accessToken = getAccessToken();
    if (accessToken && isTrustedApiRequest(config.url, config.baseURL ?? apiBaseUrl, apiBaseUrl)) {
      config.headers.Authorization = `Bearer ${accessToken}`;
    }
    return config;
  });
  client.interceptors.response.use(
    response => response,
    error => {
      if (error.response?.status === 401) {
        onUnauthorized();
      }
      return Promise.reject(error);
    });
  return client;
}

let onUnauthorized: () => void = () => undefined;
export const apiClient = createApiClient(
  () => localStorage.getItem(accessTokenStorageKey),
  () => onUnauthorized());

export function configureUnauthorizedHandler(handler: () => void): void {
  onUnauthorized = handler;
}

export const authApi: AuthApi = {
  async login(email, password) {
    const response = await apiClient.post<LoginResponse>('/api/auth/login', { email, password });
    return response.data;
  },
  async me() {
    const response = await apiClient.get<CurrentUser>('/api/auth/me');
    return response.data;
  }
};
