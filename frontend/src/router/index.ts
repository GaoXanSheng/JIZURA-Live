import {createRouter, createWebHistory} from 'vue-router';

/* 两个页面：
   /        编辑器（完整 UI）
   /render  纯 canvas 渲染页（100% 画布，Spout 渲染进程 / 双窗口监看加载） */
const router = createRouter({
  history: createWebHistory(),
  routes: [
    {path: '/', name: 'editor', component: () => import('../pages/EditorPage.vue')},
    {path: '/render', name: 'render', component: () => import('../pages/RenderPage.vue')},
  ],
});

export default router;
