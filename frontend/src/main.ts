import {createApp} from 'vue';
import {createPinia} from 'pinia';
import App from './App.vue';
import router from './router';
import './styles/main.css';
import './engine'; // 引擎副作用导入（必须在任何 jizura/* 使用 J 之前）
import {installDevUi} from './jizura/devui';

const app = createApp(App);
app.use(createPinia());
installDevUi(app);   // 渲染调试面板：Ctrl+Alt+D 呼出；开启时记录 Vue 组件渲染耗时
app.use(router);
app.config.errorHandler = (err: any, _inst, info) => {
  console.error('[jizura]', err, info);
  (window as any).__lastVueErr = String(err && err.stack || err) + ' @' + info;
};
app.mount('#app');
