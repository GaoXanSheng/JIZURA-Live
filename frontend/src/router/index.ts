import { createRouter, createWebHistory } from 'vue-router';

/* 三个页面：
   /        编辑器（完整 UI）
   /render  纯 canvas 渲染页（100% 画布，Spout 渲染进程 / 双窗口监看加载）
   /perfsim 仿真性能测试页（tools/perf 体系，用歌曲夹具离线重放负载） */
const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'editor', component: () => import('../pages/EditorPage.vue') },
    { path: '/render', name: 'render', component: () => import('../pages/RenderPage.vue') },
    { path: '/perfsim', name: 'perfsim', component: () => import('../pages/PerfSimPage.vue') },
  ],
});

export default router;
