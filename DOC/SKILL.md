---
name: jizura-live
description: 通过 jizura MCP 驱动本地「字面 Live」（JIZURA Live / JizuraLive）制作歌词 PV。当用户提到 字面 Live、JIZURA、jizura、歌词 PV/歌词视频/歌词动效/文字 PV，或要求通过 MCP 操作本地歌词应用——改歌词、换风格/字体/画幅、调手法切分、omakase 随机、打点、预览播放、导出 MP4/PNG 序列——即使没说"MCP"也应使用本技能。17 个 mcp__jizura__* 工具的完整用法、revision 契约与避坑指南。
---

# 字面 Live MCP 操作指南

字面 Live 是本地歌词 PV 生成器（WPF + SkiaSharp 桌面应用，3.0 纯 C# 版无浏览器内核），通过 MCP 暴露编辑接口。你的任务是用这些工具为用户制作并调整歌词 PV。

## 前置条件（不满足时先处理，不要硬调工具）

1. **应用必须在运行**：确认字面 Live 窗口已打开。没开就让用户启动（`bin/Debug/net10.0-windows/JizuraLive.exe` 或已安装的快捷方式）。
2. **MCP 服务器已连接**：工具形如 `mcp__jizura__get_state`（服务器名 `jizura`，工具名**无 `jizura_` 前缀**）。连接失败时检查应用是否运行，然后在 ZCode 设置 → MCP 里重连。
3. 刚启动瞬间工程/云面板还在装载，写操作可能报忙——`get_state.loading` 全为 false 再动手，或等几秒重试。

## 两条硬契约（违反即报错）

- **revision 契约**：写操作（edit_line / edit_cut / randomize line|cut / set_timing 行引用）必须带 `revision`，值为 `get_state` 返回的最新值。每次写操作后 revision 会变——**用上一次写操作返回值里带的新 revision 做下一次写**，不必反复 get_state。收到 `stale_revision` 说明有别的修改插了进来，重新 get_state 再写。
- **行/切分编号是 1 起始**，且随歌词/设置变化重新规划（replan）后**编号会变**。凡是跨过一次 set_lyrics / update_settings / randomize 之后再引用行号，先用 get_state 确认最新编号。

## 标准工作流

```
get_state → list_options → set_lyrics → update_settings / set_timing
→ edit_line / edit_cut / randomize（微调）→ preview（确认观感）
→ start_export → get_export_status（轮询）
```

1. `get_state`（detail=false 轻量）读状态：`loading` 各项为 false 才继续编辑。
2. `list_options` 查可用 ID。category：`style`、`font`，或手法组 `layout/enter/hold/exit/decor/treat/bg/cam/fx/trans`。返回 id + 本地化名称 + 是否启用，别凭空猜 ID。
3. `set_lyrics` 整体写入歌词（记法见下节）。
4. `update_settings` 定风格/画幅/配色/字体（settings 里只传要改的字段）；`set_timing` 定 BPM 或手动行时间。
5. `edit_line` / `edit_cut` / `randomize` 按行、按切分微调；`randomize target=all` 一键随机整套观感。
6. `preview` 播放/暂停/定位确认观感；`history` 撤销（注意 edit 与 look 是两条独立历史）。
7. `start_export`（kind: mp4 / png / pnga 透明 / pngl 分层）立即返回任务，用 `get_export_status` 轮询到 completed/failed。**导出进行中其他工具会被拒**（"导出进行中"），导完再继续编辑。

## 歌词记法（set_lyrics 的 text）

支持 LRC 时间戳行与行内记法：

```
[00:12.00]第一行歌词/这样切分成两段
*强调词*   ！闪烁   | 这是注释不显示
[间奏 4]   # 整行注释
```

- `/` 切分一行；`*…*` 强调；`!` 闪烁；`|` 后面是注释；`[间奏 N]` 插入 N 秒间奏行；`#` 开头整行注释。
- 带 LRC 时间戳时行时间取自时间戳；没有时按 BPM 推算，或用 `set_timing` 的 `times` 手动指定。

## 工具速查

| 工具 | 作用 / 关键参数 |
|---|---|
| `get_state` | 读完整状态（只读）。detail=true 附 project、逐行列表（start/end/text/layout/cuts/locked）、当前切分手法 |
| `get_frame` | 把当前（或 `t` 指定）时间点用引擎离屏渲成一帧 PNG 落盘并返回路径（默认 960 宽）。预览走 GPU 直显抓不到像素，核对真实画面用它（只读） |
| `list_options` | 查 ID（只读）。category 必填，query/page/pageSize 可选 |
| `set_lyrics` | 整体替换歌词（text 或 clear=true）。**见下方警告** |
| `update_settings` | 按字段改设置：title/artist/lang/style/seed/fx*/fonts/套装开关/aspect/res/fps/quality/keyBg/exportRange |
| `set_timing` | bpm/offset/lineScale/snap；times=[{line,time}] 手动行时间；clear 清除 |
| `edit_line` | 单行：text（保留 LRC 前缀）/layout/cuts（0=恢复自动）/lock。需 revision+line |
| `edit_cut` | 单切分手法：line+cut+group+key（""=恢复自动），quiet 抑制附加效果。需 revision |
| `set_techniques` | 整组启停：group+enabled，省略 keys 表示整组 |
| `set_locks` | 随机锁定：kind=tech/params + keys + locked |
| `randomize` | all=一键随机；line / cut=重抽（strategy: omakase/shuffle），会解除该行锁定 |
| `history` | 撤销/重做：kind=edit（歌词时间）/look（外观）两条独立历史，direction=back/forward |
| `preview` | action=play/pause，time 定位（秒），loop（all/line/cut/range/off），volume，muted |
| `tap_sync` | 打点：start（从某行）/record/back/stop。需用户配合节奏，代理一般只发起 |
| `project` | action=get 读工程 JSON / save 写 localStorage 自动保存 |
| `start_export` | kind=mp4/png/pnga/pngl，立即返回任务 |
| `get_export_status` | 导出任务状态（只读），轮询用 |
| `reset_project` | 只打开「初始化」确认框，**绝不绕过用户确认**；返回 needs_user_action 后提醒用户去界面点 |

## 避坑（都踩过实坑）

- **set_lyrics 是整体替换、立即自动保存、且不走 undo 历史**——history 撤不回来。改歌词前先 `project action=get` 把当前工程 JSON 存一份（至少记住原歌词），需要时才能恢复。
- **音频、字体、.jizuralive 工程包文件只能用户通过界面投入**，工具不接收文件路径。没有音频时无法导出成片，提醒用户先投音频。
  投音频有两条界面路：左栏「载入歌曲」（本地文件）与「云端音乐」（酷狗/网易云搜索、**我的歌单**：点歌单整单载入，或粘贴歌单链接/ID 回车载入；VIP 歌需在「账号」页扫码登录）。载入即自动分析节拍并重排计划。
- **字体**：PV 渲染的字体按「用户上传/本机字体 → exe 旁 `fonts\` 与用户字体库（`%LOCALAPPDATA%\JizuraLiveonts`，扫 TTF/OTF/TTC）→ 系统已装家族 → 按气质（黑体/明朝/圆体/像素/书法/等宽）落到系统字体 → 逐字兜底 → 微软雅黑保底」解析；目录与系统都没有的家族会后台从 Google Fonts 仓库镜像按需拉完整 TTF 进用户字体库（设置页可关）。所以同一工程在不同机器上字形可能不同（缺字体自动回退），不是 bug。
- 改歌词后 plan 会重规划，旧的行号/切分号全部作废——重新 get_state。
- `randomize` 显式重抽会解除该行锁定（与界面行为一致），锁定的行别随便重抽。
- 写操作返回值就是「写后事实」（含新 revision），拿它继续，别用缓存值。
- 一次只推进一个用户的意图，大量微调前先小步确认观感（preview），避免一键到底后大范围返工。

## 调试技巧

工具不经过客户端也能直连验证（应用运行时）：

```bash
curl -s -X POST http://127.0.0.1:8642/mcp -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_state","arguments":{"detail":false}}}'
```

接入架构、端口探测、错误码映射见仓库根目录的 `DOC/MCP.md`。
