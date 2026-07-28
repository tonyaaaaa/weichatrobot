import { createApp } from 'vue';
import { createPinia } from 'pinia';
import 'element-plus/es/components/alert/style/css';
import 'element-plus/es/components/button/style/css';
import 'element-plus/es/components/config-provider/style/css';
import 'element-plus/es/components/dialog/style/css';
import 'element-plus/es/components/empty/style/css';
import 'element-plus/es/components/form/style/css';
import 'element-plus/es/components/input/style/css';
import 'element-plus/es/components/input-number/style/css';
import 'element-plus/es/components/message-box/style/css';
import 'element-plus/es/components/message/style/css';
import 'element-plus/es/components/pagination/style/css';
import 'element-plus/es/components/progress/style/css';
import 'element-plus/es/components/select/style/css';
import 'element-plus/es/components/skeleton/style/css';
import 'element-plus/es/components/switch/style/css';
import 'element-plus/es/components/table/style/css';
import 'element-plus/es/components/tag/style/css';
import './styles.css';
import { configureUnauthorizedHandler } from './api/http';
import { router } from './router';
import { createUnauthorizedHandler, useAuthStore } from './stores/auth';
import AdminApp from './AdminApp.vue';

const app = createApp(AdminApp);
const pinia = createPinia();
app.use(pinia);
configureUnauthorizedHandler(createUnauthorizedHandler(
  useAuthStore(pinia),
  () => router.replace({ name: 'login' })
));
await useAuthStore(pinia).hydrate();
app.use(router);
app.mount('#app');
