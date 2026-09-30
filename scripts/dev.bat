@echo off
rem ============================================================
rem 前端开发模式一键启动：
rem   1. 启动 Vite dev server（http://localhost:5174，前端改动热更新）
rem   2. 以 JIZURA_DEV=1 启动应用（CEF 加载 Vite 页面，
rem      /api 自动代理回本机 8642 服务）
rem 改 frontend/ 下任何文件即时生效，无需 build / 重启应用。
rem 对应停止脚本：dev-stop.bat
rem ============================================================
rem first run: fetches ~50MB of self-hosted fonts (offline afterwards; may take minutes)
start "jizura-vite" cmd /c "cd /d %~dp0..\frontend && (if not exist public\fonts\manifest.json node scripts\fetch-fonts.mjs) & npm run dev"
timeout /t 2 /nobreak >nul
set JIZURA_DEV=1
start "" "%~dp0..\bin\Debug\net10.0-windows\JizuraLive.exe"
