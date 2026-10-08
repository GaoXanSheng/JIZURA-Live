# AngleSpike — M-G1 POC（ANGLE 零拷贝 → Spout）

## 运行前置
1. `dotnet build tools/prof/AngleSpike`
2. 把 ANGLE 三件套拷到输出目录（dev-only，第三方 DLL 不入库）：
   ```bash
   OUT=tools/prof/AngleSpike/bin/Debug/net10.0-windows10.0.19041.0
   SRC="/c/Program Files/Adobe/Adobe Premiere Pro 2026/CEPHtmlEngine"
   cp "$SRC/libEGL.dll" "$SRC/libGLESv2.dll" "$SRC/d3dcompiler_47.dll" "$OUT/"
   ```
3. 运行 exe，读 `angle-report.txt`（控制台通道在部分环境不可靠，报告一律落盘）

## 首轮实测结论（2026-10-08）
- CPU 基线 47–50ms/帧@1080p ✓
- ANGLE 借 Adobe 构建初始化成功（2.1.17747, NVIDIA）✓
- 该构建无 `EGL_ANGLE_device_d3d`（取设备路不通），但 `EGL_ANGLE_image_d3d11_texture` +
  `glEGLImageTargetTexture2DOES` 齐备 → 改走 A'（自建纹理→EGLImage→FBO），见 DOC/GPU-SPOUT-PLAN.md 二·实测更正
