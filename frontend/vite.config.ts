import {defineConfig} from 'vite';
import vue from '@vitejs/plugin-vue';
import tailwindcss from '@tailwindcss/vite';
import VueDevTools from 'vite-plugin-vue-devtools';
import pkg from './package.json';

// 产物进 obj/frontend-dist，由 csproj 以 EmbeddedResource 嵌进 JizuraLive.dll（jizura-www/ 前缀），
// 运行时 StaticSiteModule 从程序集内存直接 serve，磁盘上不留散装产物
export default defineConfig({
  base: './',
  plugins: [
    vue(),
    tailwindcss(),
    // CEF 装不了 Chrome 扩展，用页内嵌版的 devtools 调组件树/状态
    VueDevTools(),
  ],
  // 版本号单一事实源是 package.json，编译期注入，TopBar 写包和 MCP serverInfo 共用
  define: {
    __APP_VERSION__: JSON.stringify(pkg.version),
  },
  build: {
    outDir: '../obj/frontend-dist',
    emptyOutDir: true,
    target: 'chrome120',
    chunkSizeWarningLimit: 4096,
  },
  server: {
    port: 5174,
    proxy: {
      // 本机服务 API 都以 /api 开头，dev 时代理到正在跑的桌面应用，基准端口 8642
      '/api': 'http://127.0.0.1:8642',
    },
  },
});
