import {createRouter, createWebHistory} from 'vue-router';

// /render 是纯 canvas 渲染页，给 Spout 渲染进程和双窗口监看用
const router = createRouter({
  history: createWebHistory(),
  routes: [
    {path: '/', name: 'editor', component: () => import('../pages/EditorPage.vue')},
    {path: '/render', name: 'render', component: () => import('../pages/RenderPage.vue')},
  ],
});

export default router;
