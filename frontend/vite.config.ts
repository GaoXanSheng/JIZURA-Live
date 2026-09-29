import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import tailwindcss from '@tailwindcss/vite';

// 产物输出到 obj/frontend-dist（git 忽略），由 csproj 的 EmbedFrontend 目标
// 以 EmbeddedResource 形式嵌入 JizuraLive.dll（jizura-www/ 前缀），
// 运行时由 StaticSiteModule 从程序集内存直接 serve，磁盘上不再有可改动的散装产物。
// 开发时 vite dev server 把本机服务 API 代理到正在运行的桌面应用（基准端口 8642）。
export default defineConfig({
  base: './',
  plugins: [vue(), tailwindcss()],
  build: {
    outDir: '../obj/frontend-dist',
    emptyOutDir: true,
    target: 'chrome120',
    chunkSizeWarningLimit: 4096,
  },
  server: {
    port: 5174,
    proxy: {
      // 本机服务 API 全部以 /api 开头，dev 时代理到正在运行的桌面应用
      '/api': 'http://127.0.0.1:8642',
    },
  },
});
