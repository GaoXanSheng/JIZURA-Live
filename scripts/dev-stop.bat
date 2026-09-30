@echo off
rem 停止开发模式：杀 Vite dev 与应用（保留本机 8642 服务由应用持有，一并退出）
taskkill /IM JizuraLive.exe /F >nul 2>&1
taskkill /IM node.exe /F >nul 2>&1
echo dev stopped
