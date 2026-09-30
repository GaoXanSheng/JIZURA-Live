# 测试体系（perf + 单元测试）

对标 Chrome 测试工程的两层结构：

| 层 | 本仓库 | Chrome 对应 |
|---|---|---|
| **单元测试** | vitest + jsdom（`frontend/tests/`，`npm run test`，~2s） | unittests（gtest/browser_tests） |
| **性能基准** | 歌曲夹具 × 离线重放（`/perfsim` 页 + `tools/perf/run.ps1`） | tools/perf telemetry + trybot |
| **环境控制** | CDP `Emulation.setCPUThrottlingRate`（模拟低端机） | trybot 的 low-end bot |
| **归因** | CDP `Profiler`（按函数 self-time 排名） | trace-based metrics (TBM) |
| **趋势/回归** | `history.jsonl` + `baseline.json` 对比（>10% 标 REGRESSION） | dashboard + perf sheriffs |

目标歌曲：《古巨基 - 劲歌. 金曲2》（夹具 `jinge2`：758.3s 串烧、424 cuts、160 行）。

## 一、单元测试（改动逻辑后必跑）

```powershell
cd frontend
npm run test        # vitest run，~2s
npm run check       # vue-tsc 类型检查
```

覆盖：
- `tests/engine/planner.spec.ts` — plan 确定性（同输入同切分）、不同 seed 产出不同编排、
  切分有序且时长为正、歌词行覆盖、`*强调*`/`!`/`/`手切/间奏解析、标题卡、BPM 节拍网格
- `tests/engine/text.spec.ts` — 字形布局（横/竖排、多行、对齐）、文字整列开关（J.TYPESET）、
  度量缓存
- `tests/cloud/krc.spec.ts` — krc 解密回环（krc1 魔数 + XOR + deflate）、语言分类、
  krc→LRC（逐字时间压缩、`/`→全角、音译/翻译对齐、mapOut）、网易云 LRC 解析/合成、
  无时间戳行均摊
- `tests/jizura/project.spec.ts` — mergeProject 对不可信输入的清洗
  （colors 只留 #hex、locks 只收 plain key 且只允许开、字体 key 白名单）
- `tests/jizura/probe.spec.ts` — 埋点聚合与 dump 结构

测试环境要点（`tests/setup.ts`）：canvas 2d 打桩（确定性 measureText，CJK=1em）、
rAF polyfill、Node 原生 Blob 替换 jsdom Blob（krc 的流式解压需要 `.stream()`）。
引擎模块加载期只有 `02_fonts.js` 需要 2d context，桩必须在 engine 导入前生效（setupFiles 已保证）。

注意两个语义坑（写用例前先知道）：
1. `computeTiming` 只有当**所有**歌词行都带时间戳时才采用 LRC 时间；缺一行整首重排。
2. `J.TYPESET` 是全局开关，控制假名收紧等排版行为，测试里要临时打开并恢复。

## 二、性能基准（可复现、可判分）

### 一次性准备

```powershell
# 应用里载入目标歌曲（等音频分析完成），dev 模式（Vite 5174 + CDP 9222）
powershell -File tools\perf\capture-fixture.ps1
#    → frontend/public/perf-fixtures/<名>.json（工程全量 + 音频元数据，不含音频文件）
#    曲名/歌手为空时加 -Name；不想进包用 -OutFile 放别处，跑的时候 -Fixture 传完整路径
```

### 日常命令（frontend/ 下）

```powershell
npm run bench:quick    # 前 60s 扫描 + 15s 实时，1 rep（~2 分钟，改代码后快速自检）
npm run bench          # 全曲 60Hz + 最忙 30s 实时，3 reps（标准结论）
npm run bench:full     # 全曲 + 实时 + 手法预览卡，3 reps（发版前）
npm run bench:lowend   # quick 参数 + CPU 4x 节流（低端机会不会卡）
```

直接调 run.ps1 可用全部参数：`-Res full`（导出级负载）、`-SweepHz 30`（提速）、
`-RtWin start-end`（指定实时窗口）、`-Profile`（CPU profile 归因）、
`-Baseline tools\perf\baseline.json [-StrictBaseline]`、`-Reps/-Warmup/-Throttle/-NoHistory`。

### 方法论（为什么要这些步骤）

- **预热跑 + N 次测量**：第一次导航包含字体冷加载/JIT，丢弃；测量 rep 聚合用**中位数**
  （抗单次毛刺），worst-case 类指标（draw.max、gapMax、longtask）用 **max-of-runs**
  （漏报比误报更糟）。
- **CPU 节流**：`-Throttle 4` 用 CDP 模拟慢 4 倍的机器，回答"观众的老笔记本能不能放"，
  和真机测试比可复现、可 CI。
- **基线对比只在同参数间有效**：quick（前 60s）和 full（全曲）的 p95 不可互比——
  曲子不同段密度不同。`baseline.json` 是哪个命令生成的就用哪个命令去比。
- **Profile 单独跑一版**：采样本身有开销，归因数据与计时数据不混在同一 rep 里取。

### 结果产物

- `reports/<fixture>-<res>-<时间戳>.json` — 合成报告：中位数聚合 + 每个 rep 明细（`reps[]`）
  + 最贵切分榜 + 每 10s 桶 + 判分
- `reports/*.profile.txt` — CPU profile 前 25 个自耗时函数（`-Profile` 时）
- `history.jsonl` — 每次运行一行（git rev、参数、关键指标、结论），看趋势/找劣化起点
- 退出码：0 = PASS，1 = FAIL 或 `-StrictBaseline` 回归，2/3/4 = 环境/页内/超时

### 判分阈值（perfsim.ts 顶部 `THR`，报告里带 `thr` 字段）

| 指标 | 阈值 |
|---|---|
| replan p50 | ≤ 100ms |
| 扫描 draw p95 | ≤ 16.7ms（60fps 帧预算） |
| 扫描 draw max | ≤ 150ms |
| 实时掉帧占比 | ≤ 2% |
| 实时 gapMax | ≤ 150ms |
| 实时新增 longtask | ≤ 250ms |
| 预览卡首画 avg | ≤ 8ms |
| 堆增长（sweep→rt） | ≤ 60MB |

### 报告怎么读

- `sweep.draw.max` 高但 p95 正常 → 个别切分贵：看 `worstCuts[0]`（切分号/秒/layout/enter→exit），
  编辑器里 seek 过去定位手法。
- `realtime.droppedPct` / `gapMax` 高 → 对照 `sweep.buckets` 找最忙时段。
- `replan` 高 → 改手法/一键生成后的一次性卡顿。
- `-Profile` 报告里找函数名（当前这首歌：drawImage / fillText / strokeText 占大头——
  Canvas 栅格化和文字绘制，符合预期；若出现意外函数名就是优化点）。
- 堆增长超阈值 → 疑似泄漏。

### 《劲歌. 金曲2》当前基线（9a3b8bb，全曲 60Hz，参考值）

- sweep draw：avg ~2.0 / p95 ~8.4 / max ~120ms（worst：#64 t=112.4s lowerThird flicker→echoOut；
  wordCloud 切分是全曲最贵布局）
- 实时（最忙 30s 窗口 560-590s）：掉帧 0.36%、gapMax 53.5ms、longtask 最坏 65ms
- replan p50 ~31ms；860 张预览卡 paint avg ~1ms；heap 30→65MB

### 手动跑一次（可选）

浏览器直接开 `http://localhost:5174/perfsim?fixture=jinge2&probe=1` — 页面会显示阶段进度、
结论面板和探针 HUD，`下载报告 JSON` 按钮可直接导出。所有 URL 参数见 `perfsim.ts` 顶部注释。

## 夹具说明

夹具 = `project`（歌词、手法编排、风格、BPM 设置…）+ `audio` 元数据
（duration/beats/energy/peaks）。planner 只消费这些字段，重放出的 plan 与编辑器完全一致。
编排/风格改动后要重抓夹具才能代表新负载。仿真页与编辑器同源但**不写**工程存储键，
不会碰用户工程。
