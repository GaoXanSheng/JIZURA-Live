import { createApp } from 'vue';
import { createPinia } from 'pinia';
import App from './App.vue';
import './styles/main.css';
import './engine';   // 引擎副作用导入（必须在任何 jizura/* 使用 J 之前）
import './jizura/boot';

const app = createApp(App);
app.use(createPinia());
app.config.errorHandler = (err: any, _inst, info) => {
  console.error('[jizura]', err, info);
  (window as any).__lastVueErr = String(err && err.stack || err) + ' @' + info;
};
app.mount('#app');
