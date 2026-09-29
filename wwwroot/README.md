# wwwroot — 桌面端静态资源

本目录由 EmbedIO 直接 serve，是**构建产物**，不要手改：

- `dist/` — 新前端（Vue 3 + Vite + TS）构建产物。源码在仓库根的 `frontend/`，
  改动后运行 `cd frontend && npm run build` 重新生成。服务端 `/` 指向这里。
- `zh-hans/` — 旧版单文件页面，仅作为 `/legacy` 对照回退保留，重构验收完成后可删除。
- 其余为 LICENSE / VERSION / favicon 等静态文件。

前端开发文档见 [`frontend/README.md`](../frontend/README.md)。
