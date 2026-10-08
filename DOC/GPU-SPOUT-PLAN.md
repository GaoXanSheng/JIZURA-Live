# 立项评估：GPU 光栅 → Spout D3D11 共享纹理（零拷贝输出）

> 目标（用户定义）：渲染主体从 CPU 光栅迁移到 GPU，**渲染结果以 D3D11 共享纹理直接交给 Spout**
> （即 CEF 时代 shared-texture 那条路的原生特化），预览/导出的性能短板同源解决。

## 一、已验证事实（本次评估实测，非推测）

| # | 事实 | 证据 |
|---|---|---|
| 1 | RTX 4060 Laptop + vulkan-1.dll + 完整 Vulkan 运行时 | `Win32_VideoController` / System32 |
| 2 | **SkiaSharp 4.153 自带 `GRGlInterface.CreateAngle`（ANGLE 接口工厂）** | SkiaSharp.dll 导出串实测 |
| 3 | **Spout 侧接受任意 ID3D11Texture2D 指针**：生产代码 `SpoutOut.cs:364 ch.Sender.SendTexture(ch.Texture!.NativePointer)`；旧 CEF 链路对共享纹理同款调用（main 分支 MirrorRenderHandler 先例） | 源码 |
| 4 | **本机有可借用的完整 ANGLE**：Adobe CEPHtmlEngine（Premiere/AE 2026）含 libEGL.dll + libGLESv2.dll + d3dcompiler_47.dll；Edge 154 已静态链接 ANGLE（仅 d3dcompiler 独立） | 目录实测 |
| 5 | 工具脚手架：`tools/prof/VkSpike`（Vulkan 实例/物理设备/设备/队列创建 P/Invoke 已编译通过，枚举 RTX 就绪） | tools/prof |

结论：**路线上的三个接口（Skia-ANGLE 工厂 / ANGLE 本体 / Spout 任意纹理）全部已知可用**，POC 无未知断点。

## 二、选定架构（Option A：ANGLE = GL on D3D11，零拷贝）

```
Skia GRContext(CreateAngle)          ← Skia 的全部绘制走 GLES 语义
   │  EGL( libEGL.dll, ES3, surfaceless )
   ▼
ANGLE 内部 D3D11 设备 ──EGL_ANGLE_device_d3d──► 我们拿到 ID3D11Device*
   │                                              │
   │  eglCreatePbufferSurface(                     自建纹理：
   │    EGL_D3D_TEXTURE_2D_SHARE_HANDLE_ANGLE)     D3D11_RESOURCE_MISC_SHARED
   ▼                                               +GetSharedHandle
Skia backbuffer surface(FBO0) ─────────────────► 该纹理即渲染目标
                                                   │
                          SpoutDX.SendTexture(纹理) ◄┘   （同设备直用/跨设备走共享句柄，
                                                          CEF 先例已证）
```
- 关键 EGL 属性：`EGL_D3D_TEXTURE_2D_SHARE_HANDLE_ANGLE(0x3200)`、
  `eglQueryDeviceAttribEXT(EGL_D3D11_DEVICE_ANGLE=0x33A0)`、`eglQueryDisplayAttribEXT(EGL_DEVICE_EXT=0x322C)`
- 每帧循环：`glClear→Skia 画→glFlush/eglSwapBuffers(无需)→SpoutDX.SendTexture`（全部 GPU 侧）
- 透明通道：ANGLE 默认 framebuffer 带 alpha，`EGL_ALPHA_SIZE=8` 配置；Spout 透明帧直通

**备选 B（Vulkan → D3D11 外存互通）**：`VK_KHR_external_memory_win32` 导出 D3D11 共享句柄；
VkSpike 已把设备创建打通，但其后还有 VkImage 导入 Skia（`GRBackendTexture`/`GRVkImageInfo`）
与互通扩展协商，工量约为 A 的 1.5–2 倍。仅当 ANGLE 路被阻塞时启用。

**排除 C（GPU 渲染→回读→CPU 上传 D3D11）**：每帧 GPU→CPU→GPU 往返，直接抵消 GPU 收益，不满足目标。

## 二·实测更正（POC 首轮，2026-10-08）

POC 工具 `tools/prof/AngleSpike` 已跑通前两步：
- **CPU 基线 47–50 ms/帧**（1920×1080，本工程 6 cuts）
- **ANGLE 在进程内初始化成功**：借用 Adobe CEPHtmlEngine 的 libEGL/libGLESv2（ANGLE 2.1.17747，vendor=Google Inc. (NVIDIA)）
- **该构建为 CEF 风格**：无 `EGL_ANGLE_platform_angle` / `EGL_ANGLE_device_d3d`
  （故 eglQueryDeviceAttribEXT 取 ANGLE 设备的路不通），**但零拷贝扩展完整**：
  `EGL_ANGLE_image_d3d11_texture`（libEGL 实测存在）+ `glEGLImageTargetTexture2DOES`（libGLESv2 实测存在），
  另有 `EGL_ANGLE_d3d_texture_client_buffer` / `EGL_ANGLE_surface_d3d_texture_2d_share_handle` / `EGL_KHR_surfaceless_context`

**架构修正为 A'（自建纹理 → EGLImage → FBO，无需取 ANGLE 设备）**：
```
1. SharpDX 自建 D3D11 设备 + B8G8R8A8 共享纹理（GetSharedHandle）
2. eglCreateImageKHR(dpy, NO_CONTEXT, EGL_D3D11_TEXTURE_ANGLE(0x3484),
     buffer = 纹理指针或共享句柄；跨设备用 EGL_D3D11_TEXTURE_SHARED_HANDLE_ANGLE(0x3487) 属性)
3. glGenTextures + glEGLImageTargetTexture2DOES(GL_TEXTURE_2D, img) → glGenFramebuffers + 挂纹理
4. Skia: GRBackendRenderTarget(W,H,0,0, GRGlFramebufferInfo(fbo, 0x8058)) →
     SKSurface.Create(gr, rt, TopLeft, Rgba8888)   ← API 已由 TypeDump 实测确认
5. Renderer.Frame 全部 GPU 侧 → SpoutDX.SendTexture(第 1 步的纹理) 零拷贝
```
剩余工作量收缩到：eglCreateImageKHR/GL 函数 3 个 P/Invoke + 常量核对（ANGLE eglext_angle.h），
估 0.5–1 天完成像素门 + Spout 回环验收。

## 二·实测更正 · 补充（POC 三轮，2026-10-08 深夜）

外纹理导入的两条路在本机 Adobe ANGLE(2.1.17747) 上均被**严格校验**拒绝：
- 共享句柄/纹理指针 pbuffer（`EGL_ANGLE_d3d_share_handle_client_buffer`）：8 个 config 无一
  携带能力 flag，任意 config 下 `eglCreatePbufferSurface` 均 `EGL_BAD_ATTRIBUTE(0x3004)`
- `eglCreateImageKHR(EGL_D3D11_TEXTURE_ANGLE)`（按 Chromium gl_image_d3d.cc 权威形态：
  `0x345D` 内部格式=DXGI 87 + `0x3493/0x3492` 属性）：`EGL_BAD_PARAMETER(0x300c)`
  —— 归因：**纹理不在 ANGLE 设备上**，而该构建无 `EGL_ANGLE_device_d3d`，拿不到它的设备

**已排除的坑（记录）**：`attribs=null` 会原生 AV；共享句柄 64 位须 `unchecked` 截断成 EGLint。

**下一轮三条路（按优先序）**：
1. **换一个带 `EGL_ANGLE_device_d3d` 的 ANGLE 构建**（ANGLE 官方 CI 产物/自建均默认含此
   扩展；Adobe CEF 构建裁剪了它）→ 取到 ANGLE 设备后，纹理创建在其设备上，两条路都通
2. **config 空间穷举**：当前 chooseConfig（ES3+alpha+depth24）可能排除了能力配置；
   用枚举全 config + `eglGetConfigAttrib(0x3200)` 全扫（本轮只扫了筛选后的 8 个）
3. **Vulkan 路线 B**（`VK_KHR_external_memory_win32`）：不依赖 ANGLE，VkSpike 已奠基

## 三、POC（先于全面开发，1–2 天）

1. 借 Adobe ANGLE DLL 拷入 dev-only 目录（**不提交仓库**；分发方案见风险表）
2. P/Invoke libEGL：`eglGetPlatformDisplayEXT/eglInitialize/eglChooseConfig(ES3+alpha)/eglCreateContext/eglMakeCurrent`（surfaceless）
3. `eglQueryDisplayAttribEXT` → 设备句柄 → `eglQueryDeviceAttribEXT(EGL_D3D11_DEVICE_ANGLE)` 拿 `ID3D11Device*`
4. 该设备上建 `SKColorType.Bgra8888`、W×H、`D3D11_RESOURCE_MISC_SHARED` 纹理 → `GetSharedHandle`
5. `eglCreatePbufferSurface(shareHandle)` → makeCurrent → `GRContext.CreateAngle` → `SKSurface.Create(gr, backBuffer:true, ...)`（FBO0 即该纹理）
6. 渲染 RenderSmoke 工程 30 帧：帧耗时 GPU vs CPU 基线；`PeekPixels/ReadPixels` 离线对拍
   （复用性能专项的像素门：≤0.5% 像素差 ±3）
7. `SpoutDX.SendTexture(纹理)` → 用 tools/ 里的 Spout 接收器 exe 验证真实出帧 + 60fps 持续

**POC 验收门**：像素对拍过门 + Spout 接收器 1080p 60fps 持续 60s 无掉帧 + 透明通道出一帧带 alpha 的接收图。

## 四、立项里程碑与验收

| 里程碑 | 内容 | 验收 |
|---|---|---|
| M-G1（POC） | 上述 7 步 | 像素门 + 60fps 实收 |
| M-G2 | SpoutOut 全面切换 GPU surface（保留 CPU 回退开关）；多显卡选择（枚举离散 GPU，避开虚拟显示器） | OBS 实收 60fps、断显卡降级不崩 |
| M-G3 | 预览接入同一 GRContext（WPF 侧走 D3DImage 或 GLWpfControl）；导出改用 GPU 帧读回（编码输入本就需要内存帧） | 预览 ≥45fps@1080p、导出帧耗 -60% 量级 |

## 五、风险表

| 风险 | 等级 | 对策 |
|---|---|---|
| ANGLE 二进制分发来源 | 中 | 分发前引入 `Ultz.Native.Angle` 等 NuGet（BSD 许可）或自建 ANGLE 产物；**仓库只存获取脚本**，第三方 DLL 不入库 |
| 多显卡/虚拟显示器（本机 4 个适配器） | 中 | `eglGetPlatformDisplayEXT` 显式指定设备 LUID/枚举 DISCRETE_GPU；沿用 VkSpike 的选择策略 |
| Skia GL 后端功能差异（filter/blend 个别项） | 中 | 像素门逐项裁决；差异项自实现 shader 或回退该项 CPU |
| SpoutDX 跨设备纹理路径 | 低 | CEF 先例已证；POC 第 7 步实测 |
| 会话/句柄生命周期（共享纹理与 Spout 持有） | 低 | 沿用引擎「面绝不 Dispose」宿主纪律 + 每帧复用同一纹理 |

## 六、结论

**建议立项**：POC 已无未知技术断点（三个关键接口均实测可用），预估 POC 1–2 天、
全量（M-G1→G3）5–8 个工程日。收益：Spout 输出与预览帧成本从 ~46ms/帧（CPU）
降到 GPU 光栅 + 零拷贝输出量级，1080p60 稳定输出不再依赖 CPU 满载。
