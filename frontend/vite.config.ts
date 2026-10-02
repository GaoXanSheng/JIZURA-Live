import {defineConfig} from 'vite';
import vue from '@vitejs/plugin-vue';
import tailwindcss from '@tailwindcss/vite';
import VueDevTools from 'vite-plugin-vue-devtools';
import pkg from './package.json';

// 产物输出到 obj/frontend-dist（git 忽略），由 csproj 的 EmbedFrontend 目标
// 以 EmbeddedResource 形式嵌入 JizuraLive.dll（jizura-www/ 前缀），
// 运行时由 StaticSiteModule 从程序集内存直接 serve，磁盘上不再有可改动的散装产物。
// 开发时 vite dev server 把本机服务 API 代理到正在运行的桌面应用（基准端口 8642）。
export default defineConfig({
  base: './',
  plugins: [
    vue(),
    tailwindcss(),
    // Vue Devtools 页内 overlay（仅 dev server 生效，build 自动跳过）。
    // CEF 装不了 Chrome 扩展，用页面内嵌版：组件树/状态/时间线直接在页里调
    VueDevTools(),
  ],
  // 版本号单一事实源 = package.json，编译期注入（TopBar 写包 / MCP serverInfo 共用）
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
      // 本机服务 API 全部以 /api 开头，dev 时代理到正在运行的桌面应用
      '/api': 'http://127.0.0.1:8642',
    },
  },
});
