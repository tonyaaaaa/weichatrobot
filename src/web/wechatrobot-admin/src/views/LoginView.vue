<script setup lang="ts">
import { ref } from 'vue';
import { useRouter } from 'vue-router';
import { useAuthStore } from '../stores/auth';

const router = useRouter();
const auth = useAuthStore();
const email = ref('');
const password = ref('');
const submitting = ref(false);
async function submit(): Promise<void> {
  submitting.value = true;
  const succeeded = await auth.login(email.value, password.value);
  submitting.value = false;
  if (succeeded) await router.replace({ name: 'dashboard' });
}
</script>

<template>
  <main class="login-page">
    <form class="login-card" @submit.prevent="submit">
      <h1>微信机器人</h1>
      <p>企业微信 AI 员工助手后台</p>
      <label>邮箱<input v-model="email" type="email" autocomplete="username" required></label>
      <label>密码<input v-model="password" type="password" autocomplete="current-password" required></label>
      <p v-if="auth.loginError" role="alert" class="login-error">{{ auth.loginError }}</p>
      <button type="submit" :disabled="submitting">{{ submitting ? '正在登录…' : '登录' }}</button>
    </form>
  </main>
</template>

<style scoped>
.login-page { display: grid; min-height: 100vh; place-items: center; background: #f4f6fb; }
.login-card { display: grid; gap: 1rem; width: min(24rem, calc(100% - 2rem)); padding: 2rem; border-radius: .75rem; background: #fff; box-shadow: 0 8px 32px #19325a1a; }
.login-card h1, .login-card p { margin: 0; }.login-card label { display: grid; gap: .375rem; font-weight: 600; }
.login-card input, .login-card button { padding: .75rem; border-radius: .375rem; font: inherit; }.login-card input { border: 1px solid #c6cedd; }
.login-card button { border: 0; color: #fff; background: #2563eb; cursor: pointer; }.login-error { color: #b42318; }
</style>
