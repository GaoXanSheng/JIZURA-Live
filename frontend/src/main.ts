import { createApp } from 'vue';
import { createPinia } from 'pinia';
import App from './App.vue';
import './styles/tokens.css';
import './styles/base.css';
import './styles/app.css';
import './engine';   // 引擎副作用导入（必须在任何 jizura/* 使用 J 之前）
import './jizura/boot';

createApp(App).use(createPinia()).mount('#app');
