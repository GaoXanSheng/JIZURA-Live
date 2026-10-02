import {createApp} from 'vue';
import {createPinia} from 'pinia';
import App from './App.vue';
import router from './router';
import './styles/main.css';
import './engine'; // 副作用导入，必须先于任何 jizura/* 用到 J
import {installDevUi} from './jizura/devui';

const app = createApp(App);
app.use(createPinia());
installDevUi(app); // 调试面板 Ctrl+Alt+D 呼出，开着时会记录 Vue 组件渲染耗时
app.use(router);
app.config.errorHandler = (err: any, _inst, info) => {
  console.error('[jizura]', err, info);
  (window as any).__lastVueErr = String(err && err.stack || err) + ' @' + info;
};
app.mount('#app');
