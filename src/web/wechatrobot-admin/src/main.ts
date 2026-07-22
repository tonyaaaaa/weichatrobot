import { createApp } from 'vue';
import { createPinia } from 'pinia';
import ElementPlus from 'element-plus';
import 'element-plus/dist/index.css';
import './styles.css';
import { configureUnauthorizedHandler } from './api/http';
import { router } from './router';
import { useAuthStore } from './stores/auth';
import AdminApp from './AdminApp.vue';

const app = createApp(AdminApp);
const pinia = createPinia();
app.use(pinia);
configureUnauthorizedHandler(() => useAuthStore(pinia).handleUnauthorized());
await useAuthStore(pinia).hydrate();
app.use(router);
app.use(ElementPlus);
app.mount('#app');
