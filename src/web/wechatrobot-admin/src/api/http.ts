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

export function createApiClient(getAccessToken: () => string | null, onUnauthorized: () => void): AxiosInstance {
  const client = axios.create({ baseURL: import.meta.env.VITE_API_BASE_URL ?? '' });
  client.interceptors.request.use(config => {
    const accessToken = getAccessToken();
    if (accessToken) {
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
const client = createApiClient(
  () => localStorage.getItem(accessTokenStorageKey),
  () => onUnauthorized());

export function configureUnauthorizedHandler(handler: () => void): void {
  onUnauthorized = handler;
}

export const authApi: AuthApi = {
  async login(email, password) {
    const response = await client.post<LoginResponse>('/api/auth/login', { email, password });
    return response.data;
  },
  async me() {
    const response = await client.get<CurrentUser>('/api/auth/me');
    return response.data;
  }
};
