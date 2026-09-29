import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

// 产物输出到 wwwroot/dist，由桌面端 EmbedIO(StaticSiteModule) 直接 serve。
// 开发时 vite dev server 把本机服务 API 代理到正在运行的桌面应用（基准端口 8642）。
export default defineConfig({
  base: './',
  plugins: [vue()],
  build: {
    outDir: '../wwwroot/dist',
    emptyOutDir: true,
    target: 'chrome120',
    chunkSizeWarningLimit: 4096,
  },
  server: {
    port: 5173,
    proxy: {
      '/kg': 'http://127.0.0.1:8642',
      '/ne': 'http://127.0.0.1:8642',
    },
  },
});
