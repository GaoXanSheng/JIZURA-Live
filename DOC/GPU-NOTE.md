# 引擎 GPU 光栅可行性备忘（立项项）

## 背景
CPU 光栅下大工程（377 切分/1080p）单帧 ~46ms（优化后）；后期纹理链 ~36ms 是剩余主体。
perf 专项已把 finalizer/分配类热点清掉（-17%、像素位级一致），进一步提速需 GPU surface。

## 路线评估（实测）
1. **OpenGL/ANGLE（GRContext.CreateGl）**：SkiaSharp.NativeAssets.Win32 只带
   libSkiaSharp.dll，**不含 libEGL/libGLESv2**（各版本核实过 2.88/3.119/4.153）。
   走此路需外部引入 ANGLE 二进制（额外供应链/版本锁定成本）。
2. **Vulkan（GRContext.CreateVulkan）**：SkiaSharp 3.116+ 暴露 GRVkBackendContext，
   但 VkDevice/VkQueue 需 P/Invoke vulkan-1.dll 手工创建（~200+ 行 unsafe 互操作）。
   机器侧现代显卡驱动（NVIDIA/AMD/Intel）均自带 vulkan-1.dll，可探测为主路径。
3. **Spout 直出的连带工作**：GPU 渲染后 Spout 需要 GPU→D3D11 共享纹理互操作
   （即当年 CEF shared-texture 那条路），v1 若走 Vulkan 需 VK_KHR_external_memory_win32
   导出 D3D11 共享句柄；预览侧 WPF 需换 D3DImage/交换链。
4. **收益预估**：光栅主体转移 GPU 后 CPU 仅剩场景构建（文本排版/路径顶点），
   预估帧成本降到 5-15ms 量级；预览满帧、导出吞吐同比例提升。

## 结论
属架构级改动（互操作 + WPF/Spout 双侧改造），建议按独立里程碑立项；
短期替代已落地：预览光栅长边封顶 1100px + 后台重规划 + 分配热点清理。
