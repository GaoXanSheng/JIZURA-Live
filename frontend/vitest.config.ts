import { defineConfig } from 'vitest/config';

/* 单元测试配置：jsdom 环境 + canvas 2d 桩（engine 的字体度量在模块加载期就要 2d context）。
   不复用 vite.config.ts（避免拖入 vue/tailwind 插件，测试只跑 ts/js 逻辑层）。 */
export default defineConfig({
  test: {
    environment: 'jsdom',
    setupFiles: ['./tests/setup.ts'],
    include: ['tests/**/*.spec.ts'],
    reporters: ['default'],
  },
});
