# src/Export — 纯 C# 导出管线（FFmpeg 编码 + SkiaSharp 封装）

对位 `frontend/src/engine/11_export.js` + `frontend/src/jizura/export.ts/export-spec.ts`（浏览器内
WebCodecs + mp4-muxer/mediabunny）。纯 C# 无 VP9 编码器，编码统一走 **捆绑的 ffmpeg.exe**
（GPL 构建，含 libx264 / libvpx-vp9）：

- 帧数据：渲染循环把 BGRA 帧写 ffmpeg stdin（`-f rawvideo -pix_fmt bgra`）
- 音频：NAudio 解码/重采样 PCM 作为第二输入，与视频同一条命令封装
- 进度：`-progress pipe:1` 解析；支持取消（CancellationToken → 杀进程）

## 格式矩阵（与 export-spec 对齐，保持兼容）

| kind | 内容 | 编码/封装 |
|---|---|---|
| `mp4` | 完整画面+音频 | libx264 High + AAC（mp4） |
| `png` / `pnga` | 序列 zip（不透明/透明） | SkiaSharp PNG + ZipArchive |
| `pngl` / `pngf` | 分层（back//front/）/前景层 | 同上 |
| `webm` | 完整画面 | libvpx-vp9（yuv420p） |
| `webmfront` | 前景层（透明） | libvpx-vp9 **yuva420p** |
| `subwebm` | 字幕层（透明） | libvpx-vp9 yuva420p |
| `wav` | 音频 | C# 16-bit PCM WAV |
| AE JSON | After Effects 工程 | `planForAE` 移植（纯 JSON） |

勾选组合（transparent/layer/layers/bakeSubs/embedAudio/customDrawOnly）逐一对应。

## 结构（M4 落地）

- `FfmpegEncoder.cs`：ffmpeg 进程封装（参数拼装、stdin 帧写入、stderr/进度解析、退出码）
- `PngSequenceWriter.cs`：SKImage.Encode + ZipArchive（分层目录 back//front/）
- `WavWriter.cs`：16-bit PCM
- `ExportJob.cs`：后台 Task + CancellationToken + 进度上报（取代 JS 的 Worker/主线程回退）
- ffmpeg.exe 获取：构建脚本下载放入 `native/ffmpeg/`（M4 落地，加入 .gitignore 与 build.iss）
