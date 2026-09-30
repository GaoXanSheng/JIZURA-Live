/* 仿真性能测试：用真实歌曲夹具（tools/perf/capture-fixture.ps1 抓取）离线重放引擎负载。
   不需要音频文件 / 云源 / Spout —— 只伪造 planner 需要的音频元数据（duration/beats/energy/peaks），
   渲染、排布、字体、特效全部与编辑器同一条代码路径。

   页面：/perfsim?fixture=jinge2&probe=1
     fixture   public/perf-fixtures/<fixture>.json（默认 jinge2）
     res       preview(1280宽，默认) | full(plan.W 全分辨率，≈导出负载)
     hz        扫描帧率（默认 60 = 全曲每帧都画；30 可省一半时间）
     sweep     只扫前 N 秒（默认 0 = 全曲）
     rt        实时播放仿真秒数（默认 30，0 = 跳过）
     rtwin     实时窗口 auto(默认，取最忙的 30s) | start-end | boot(从 0 开始)
     thumbs    1 = 手法预览卡仿真（默认关）
     replann   replan 采样次数（默认 5，模拟一键生成/改手法的重排开销）

   四个阶段：
     1. replan   排 plan 多次采样（分布，不只看单次）
     2. sweep    全曲逐帧离线扫描 → draw 耗时分布 / 每 10s 桶 / 最贵切分榜
     3. realtime 真实 60fps 播放仿真（rAF 节拍 = 墙钟）→ 掉帧率 / gapMax / longtask 增量
     4. thumbs   手法预览卡 plan+首画 成本（可选）

   读取：window.__perfsim = { status, phase, progress, report(), download() }
   判分：内置阈值（可通过 &thr 查看报告里的阈值表），run.ps1 据此 PASS/FAIL。 */

import { J } from '../engine';
import { probeRecord, reset as probeReset, dump as probeDump } from './probe';

const QS = new URLSearchParams(window.location.search);
function qnum(k: string, d: number) { const v = parseFloat(QS.get(k) || ''); return isFinite(v) && v >= 0 ? v : d; }
function qbool(k: string) { return QS.get(k) === '1'; }

const HUD_CHARS = '0123456789:./-_()【】・No.LYRICRECUNTITLEDXYlinebpminterlude—─／ ';
const PREVIEW_W = 1280;
const GROUPS = ['layout', 'enter', 'hold', 'exit', 'decor', 'treat', 'bg', 'cam', 'fx', 'trans'];

const raf = () => new Promise<number>(r => requestAnimationFrame(r));
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));

function percentile(sorted: number[], p: number): number {
  if (!sorted.length) return 0;
  const i = Math.min(sorted.length - 1, Math.max(0, Math.round((sorted.length - 1) * p)));
  return sorted[i];
}
function stats(samples: number[]) {
  const sorted = samples.slice().sort((a, b) => a - b);
  const sum = samples.reduce((a, b) => a + b, 0);
  return {
    n: samples.length,
    avg: samples.length ? +(sum / samples.length).toFixed(2) : 0,
    p50: +percentile(sorted, 0.5).toFixed(2),
    p95: +percentile(sorted, 0.95).toFixed(2),
    p99: +percentile(sorted, 0.99).toFixed(2),
    max: +percentile(sorted, 1).toFixed(2),
  };
}

/* ---- 阈值（判 PASS/FAIL）---- */
const THR = {
  replanP50Max: 100,      // 单次 plan 中位数上限 ms
  sweepDrawP95Max: 16.7,  // 预览帧 p95 上限（60fps 帧预算）
  sweepDrawMax: 150,      // 预览帧最坏上限（编辑器单帧卡顿红线）
  rtDroppedPctMax: 2,     // 实时播放掉帧占比上限 %
  rtGapMaxMax: 150,       // 实时最大帧间隔上限 ms
  rtLongTaskMax: 250,     // 实时阶段新增 longtask 最坏值上限 ms
  rtDrawP95Max: 16.7,
  thumbPaintAvgMax: 8,    // 单张预览卡首画均值上限 ms
  memGrowMax: 60,         // sweep→rt 堆增长上限 MB（泄漏粗检）
};

const CFG = {
  fixture: QS.get('fixture') || 'jinge2',
  res: QS.get('res') === 'full' ? 'full' : 'preview',
  hz: qnum('hz', 60),
  sweepSec: qnum('sweep', 0),
  rtSec: qnum('rt', 30),
  rtWin: QS.get('rtwin') || 'auto',
  thumbs: qbool('thumbs'),
  replanN: Math.max(1, Math.round(qnum('replann', 5))),
  title: QS.get('title') || '',
  artist: QS.get('artist') || '',
};

export interface SimReport { [k: string]: any }

const g: any = window as any;
g.__perfsim = {
  status: 'boot',   // boot | fonts | replan | sweep | realtime | thumbs | done | error
  phase: '启动', progress: 0, error: null as string | null,
  startedAt: Date.now(),
  quick: null as any,
  report: null as SimReport | null,
  download() {
    if (!g.__perfsim.report) return;
    const r = g.__perfsim.report as SimReport;
    const blob = new Blob([JSON.stringify(r, null, 1)], { type: 'application/json' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = `perfsim-${r.cfg.fixture}-${r.cfg.res}-${new Date().toISOString().replace(/[:.]/g, '-')}.json`;
    a.click();
    setTimeout(() => URL.revokeObjectURL(a.href), 4000);
  },
};
const P = g.__perfsim;
function setStatus(status: string, phase: string, progress = -1) {
  P.status = status; P.phase = phase;
  if (progress >= 0) P.progress = +progress.toFixed(3);
}

function heapMB(): number | null {
  const m = (performance as any).memory;
  return m ? +(m.usedJSHeapSize / 1048576).toFixed(1) : null;
}

export function startPerfSim() {
  if (P.status !== 'boot' && P.status !== 'error') return;   // 热更新重挂载不重跑
  run().catch(e => { P.status = 'error'; P.error = String(e && e.stack || e); console.error('[perfsim]', e); });
}

async function run() {
  const env = {
    ua: navigator.userAgent,
    dpr: window.devicePixelRatio || 1,
    cores: navigator.hardwareConcurrency || 0,
    heapMB: heapMB(),
  };

  /* ---- 1. 夹具 ---- */
  setStatus('boot', '加载夹具');
  // fixture 支持两种写法：名字（→ /perf-fixtures/<名>.json，随 public/ 打包）或完整 URL/路径（不进包）
  const fxUrl = /^https?:|^\/|\//.test(CFG.fixture) ? CFG.fixture : `/perf-fixtures/${encodeURIComponent(CFG.fixture)}.json`;
  const res = await fetch(fxUrl);
  if (!res.ok) throw new Error(`夹具加载失败 ${fxUrl} → ${res.status}（先跑 tools/perf/capture-fixture.ps1）`);
  const fx = await res.json();
  if (!fx.ok || !fx.project || !fx.audio) throw new Error('夹具内容无效（缺 project/audio）');

  const project: any = fx.project;
  if (CFG.title) project.title = CFG.title;
  if (CFG.artist) project.artist = CFG.artist;
  try { if (project.userFonts && project.userFonts.length) await J.restoreUserFonts(project.userFonts); } catch { /* 字体缓冲不在本机：按近似字体度量 */ }

  // 伪造音频：planner 只用 duration/beats/energy/energyRate（peaks 留给 decor 一致性）
  const T = project.timing || {};
  let beats: number[] = fx.audio.beats || [];
  if ((T.bpm | 0) > 0) beats = J.beatGrid(T.bpm, T.beatOffset || 0, fx.audio.duration);
  const audio = {
    name: fx.audio.name || CFG.fixture, duration: fx.audio.duration,
    sampleRate: fx.audio.sampleRate || 44100, buffer: null,
    bpm: fx.audio.bpm || 0, beats,
    energy: Float32Array.from(fx.audio.energy || []),
    energyRate: fx.audio.energyRate || 0,
    peaks: Float32Array.from(fx.audio.peaks || []),
  };

  /* ---- 2. 字体 + 基础 plan ---- */
  setStatus('fonts', '加载字体');
  let plan: any = J.plan(project, audio);
  try {
    await J.ensureFonts(project.lyrics + (project.title || '') + (project.artist || '') + HUD_CHARS, J.fontsOfPlan(plan));
  } catch { /* 离线：回退字体，度量略偏但可跑 */ }
  plan = J.plan(project, audio);

  /* ---- 3. replan 采样 ---- */
  setStatus('replan', '重排采样');
  const baseSeed = project.seed;
  const replanMs: number[] = [];
  for (let i = 0; i < CFG.replanN; i++) {
    project.seed = (baseSeed + i * 7919) | 0;
    const t0 = performance.now();
    plan = J.plan(project, audio);
    replanMs.push(performance.now() - t0);
    probeRecord('sim:replan', replanMs[replanMs.length - 1]);
    await raf();
  }
  project.seed = baseSeed;

  /* ---- 4. 全曲逐帧扫描 ---- */
  const R = new J.Renderer();
  const cw = CFG.res === 'full' ? plan.W : Math.min(PREVIEW_W, plan.W);
  const ch = Math.round(cw / plan.W * plan.H);
  const cv = document.createElement('canvas');
  cv.width = cw; cv.height = ch;
  const ctx = cv.getContext('2d', { alpha: false })!;

  const hz = Math.min(60, Math.max(1, CFG.hz));
  const step = 1 / hz;
  const sweepDur = CFG.sweepSec > 0 ? Math.min(CFG.sweepSec, plan.duration) : plan.duration;
  const totalFrames = Math.max(1, Math.floor(sweepDur / step));

  const frameMs = new Array<number>(totalFrames);
  const frameCut = new Int32Array(totalFrames);
  let slow = false;   // 与编辑器 draw() 相同的自适应降级逻辑
  const drawOnce = (t: number): number => {
    const t0 = performance.now();
    R.frame(ctx, plan, t, { scale: cw / plan.W, fast: slow });
    const dt = performance.now() - t0;
    slow = dt > 30 ? true : (dt < 14 ? false : slow);
    probeRecord('sim:draw', dt);
    return dt;
  };

  setStatus('sweep', '全曲扫描');
  let i = 0, lastTick = performance.now();
  while (i < totalFrames) {
    const now = await raf();
    const deadline = performance.now() + 12;   // 每 rAF 至多吃 12ms，保持页面响应
    let drawn = 0;
    while (i < totalFrames && (performance.now() < deadline || drawn === 0)) {
      const t = i * step;
      const c = J.cutAt(plan, t);
      frameCut[i] = c ? c.index : -1;
      frameMs[i] = drawOnce(t);
      i++; drawn++;
    }
    P.quick = { i, total: totalFrames, lastMs: +frameMs[i - 1].toFixed(1), slow };
    if (now - lastTick > 500) { lastTick = now; setStatus('sweep', `全曲扫描 ${i}/${totalFrames}`, i / totalFrames); }
  }
  setStatus('sweep', '全曲扫描汇总');

  // 聚合：切分榜 + 10s 桶
  interface CutAgg { n: number; sum: number; max: number; maxT: number; samples: number[] }
  const cutAgg = new Map<number, CutAgg>();
  const bucketN = Math.ceil(sweepDur / 10);
  const bucketSum = new Float64Array(bucketN), bucketCnt = new Float64Array(bucketN), bucketMax = new Float64Array(bucketN);
  for (let f = 0; f < totalFrames; f++) {
    const ms = frameMs[f];
    let a = cutAgg.get(frameCut[f]);
    if (!a) { a = { n: 0, sum: 0, max: 0, maxT: 0, samples: [] }; cutAgg.set(frameCut[f], a); }
    a.n++; a.sum += ms; a.samples.push(ms);
    if (ms > a.max) { a.max = ms; a.maxT = f * step; }
    const b = Math.min(bucketN - 1, Math.floor(f * step / 10));
    bucketSum[b] += ms; bucketCnt[b]++; if (ms > bucketMax[b]) bucketMax[b] = ms;
  }
  const cutOf = (idx: number) => (idx >= 0 && plan.cuts[idx]) || null;
  const worstCuts = [...cutAgg.entries()]
    .filter(([idx]) => cutOf(idx))
    .map(([idx, a]) => {
      const c = cutOf(idx);
      return {
        cut: idx, t: +a.maxT.toFixed(1), line: c.line, layout: c.layout, enter: c.enter, exit: c.exit, hold: c.hold,
        dur: +(c.end - c.start).toFixed(2), chars: (c.text || '').length,
        avg: +(a.sum / a.n).toFixed(2), max: +a.max.toFixed(2), p95: +percentile(a.samples.slice().sort((x, y) => x - y), 0.95).toFixed(2),
      };
    })
    .sort((a, b) => b.max - a.max).slice(0, 12);
  const buckets: any[] = [];
  for (let b = 0; b < bucketN; b++) {
    if (!bucketCnt[b]) continue;
    buckets.push({ t0: b * 10, avg: +(bucketSum[b] / bucketCnt[b]).toFixed(2), max: +bucketMax[b].toFixed(2) });
  }
  // 实时窗口：默认取平均 draw 最贵的一段 30s
  let rtRange: number[] = [0, Math.min(CFG.rtSec || 30, plan.duration)];
  if (CFG.rtWin === 'auto') {
    let best = -1, bestAvg = -1;
    for (let b = 0; b < bucketN; b++) {
      if (!bucketCnt[b]) continue;
      if (bucketSum[b] / bucketCnt[b] > bestAvg) { bestAvg = bucketSum[b] / bucketCnt[b]; best = b; }
    }
    if (best >= 0) rtRange = [best * 10, Math.min(best * 10 + CFG.rtSec, plan.duration)];
  } else if (/^\d+(\.\d+)?-\d+(\.\d+)?$/.test(CFG.rtWin)) {
    const [a, b] = CFG.rtWin.split('-').map(Number);
    rtRange = [a, Math.min(b, plan.duration)];
  }

  const sweep = {
    frames: totalFrames, hz, res: CFG.res, canvasW: cw, canvasH: ch,
    draw: stats(frameMs),
    slowRatio: +(frameMs.reduce((a: number, _: any, f: number) => a + (frameMs[f] > 30 ? 1 : 0), 0) / totalFrames).toFixed(4),
    buckets, worstCuts,
    worstFrames: frameMs
      .map((ms, f) => ({ ms: +ms.toFixed(2), t: +(f * step).toFixed(1), cut: frameCut[f] }))
      .sort((a, b) => b.ms - a.ms).slice(0, 10),
  };
  const heapAfterSweep = heapMB();

  /* ---- 5. 实时播放仿真 ---- */
  let realtime: any = null;
  if (CFG.rtSec > 0) {
    setStatus('realtime', '实时播放仿真');
    await sleep(120);   // 与扫描阶段隔开，避免尾帧混入
    probeReset();
    const lt0 = probeDump().longTasks;
    let lastNow = await raf();
    let t = rtRange[0];
    const rtMs: number[] = [];
    let dropped = 0, gapMax = 0, gapSum = 0, gapN = 0, slowRt = false;
    const endT = rtRange[1];
    const wallStart = performance.now();
    while (t < endT) {
      const now = await raf();
      const gap = now - lastNow; lastNow = now;
      if (document.hidden) { t += Math.min(gap, 100) / 1000; continue; }   // 后台节流：推时间但不计入统计
      gapSum += gap; gapN++;
      if (gap > gapMax) gapMax = gap;
      if (gap > 32) dropped++;
      t += Math.min(gap, 250) / 1000;
      const t0 = performance.now();
      R.frame(ctx, plan, Math.min(t, plan.duration - 1e-3), { scale: cw / plan.W, fast: slowRt });
      const dt = performance.now() - t0;
      slowRt = dt > 30 ? true : (dt < 14 ? false : slowRt);
      rtMs.push(dt);
      probeRecord('sim:rt:draw', dt);
      P.quick = { t: +t.toFixed(1), end: endT, lastMs: +dt.toFixed(1) };
      setStatus('realtime', `实时播放 ${t.toFixed(0)}/${endT.toFixed(0)}s`, (t - rtRange[0]) / Math.max(0.1, endT - rtRange[0]));
    }
    const wallMs = performance.now() - wallStart;
    const lt1 = probeDump().longTasks;
    realtime = {
      window: [+rtRange[0].toFixed(1), +rtRange[1].toFixed(1)],
      wallMs: Math.round(wallMs),
      gapAvg: +(gapSum / Math.max(1, gapN)).toFixed(2),
      gapMax: +gapMax.toFixed(1),
      dropped, droppedPct: +(dropped / Math.max(1, gapN) * 100).toFixed(2),
      draw: stats(rtMs),
      longTasks: { count: lt1.count - lt0.count, worstMs: +(lt1.worstMs > lt0.worstMs ? lt1.worstMs : 0).toFixed(1) },
    };
  }
  const heapAfterRt = heapMB();

  /* ---- 6. 手法预览卡仿真（可选）---- */
  let thumbs: any = null;
  if (CFG.thumbs) {
    setStatus('thumbs', '手法预览卡');
    const [dw, dh] = J.designSize(project.aspect || '16:9');
    const th = 90, tw = Math.max(80, Math.round(th * dw / dh));
    const tcv = document.createElement('canvas');
    tcv.width = tw; tcv.height = th;
    const tctx = tcv.getContext('2d')!;
    const planMs: number[] = [], paintMs: number[] = [];
    const cards: { g: string; k: string }[] = [];
    for (const grp of GROUPS) {
      const reg = J.registry(grp), order = J.order(grp) || [];
      for (const k of order) if (reg[k] && !reg[k].special) cards.push({ g: grp, k });
    }
    let ci = 0;
    while (ci < cards.length) {
      await raf();
      const deadline = performance.now() + 8;
      while (ci < cards.length && (performance.now() < deadline || ci === 0)) {
        const { g: grp, k } = cards[ci++];
        let tp: any;
        const t0 = performance.now();
        try { tp = J.previewPlan(project, grp, k); } catch { continue; }
        planMs.push(performance.now() - t0);
        const t1 = performance.now();
        try { R.frame(tctx, tp, 0.4, { scale: tw / tp.W, fast: true, noHud: true, noGhost: grp !== 'fx' }); } catch {}
        paintMs.push(performance.now() - t1);
        probeRecord('sim:thumb:plan', planMs[planMs.length - 1]);
        probeRecord('sim:thumb:paint', paintMs[paintMs.length - 1]);
      }
      P.quick = { cards: ci, total: cards.length };
      setStatus('thumbs', `预览卡 ${ci}/${cards.length}`, ci / cards.length);
    }
    thumbs = {
      cards: cards.length, size: `${tw}x${th}`,
      plan: stats(planMs), paint: stats(paintMs),
      worstPaint: paintMs
        .map((ms, idx) => ({ ms: +ms.toFixed(2), g: cards[idx].g, k: cards[idx].k }))
        .sort((a, b) => b.ms - a.ms).slice(0, 8),
    };
  }

  /* ---- 7. 汇总 + 判分 ---- */
  setStatus('done', '汇总');
  const replanSt = stats(replanMs);
  const failures: string[] = [];
  if (replanSt.p50 > THR.replanP50Max) failures.push(`replan p50 ${replanSt.p50}ms > ${THR.replanP50Max}ms`);
  if (sweep.draw.p95 > THR.sweepDrawP95Max) failures.push(`扫描 draw p95 ${sweep.draw.p95}ms > ${THR.sweepDrawP95Max}ms`);
  if (sweep.draw.max > THR.sweepDrawMax) failures.push(`扫描 draw max ${sweep.draw.max}ms > ${THR.sweepDrawMax}ms（t=${sweep.worstFrames[0]?.t}s cut#${sweep.worstFrames[0]?.cut}）`);
  if (realtime) {
    if (realtime.droppedPct > THR.rtDroppedPctMax) failures.push(`实时掉帧 ${realtime.droppedPct}% > ${THR.rtDroppedPctMax}%`);
    if (realtime.gapMax > THR.rtGapMaxMax) failures.push(`实时 gapMax ${realtime.gapMax}ms > ${THR.rtGapMaxMax}ms`);
    if (realtime.draw.p95 > THR.rtDrawP95Max) failures.push(`实时 draw p95 ${realtime.draw.p95}ms > ${THR.rtDrawP95Max}ms`);
    if (realtime.longTasks.worstMs > THR.rtLongTaskMax) failures.push(`实时 longtask ${realtime.longTasks.worstMs}ms > ${THR.rtLongTaskMax}ms`);
  }
  if (thumbs && thumbs.paint.avg > THR.thumbPaintAvgMax) failures.push(`预览卡首画 avg ${thumbs.paint.avg}ms > ${THR.thumbPaintAvgMax}ms`);
  if (heapAfterSweep != null && heapAfterRt != null && heapAfterRt - heapAfterSweep > THR.memGrowMax) failures.push(`堆增长 ${Math.round(heapAfterRt - heapAfterSweep)}MB > ${THR.memGrowMax}MB`);

  const report: SimReport = {
    version: 1,
    cfg: CFG,
    thr: THR,
    verdict: { pass: failures.length === 0, failures },
    env,
    song: { title: project.title || '', artist: project.artist || '', audio: fx.audioName, duration: +fx.audio.duration.toFixed(1), bpm: fx.audio.bpm },
    plan: {
      W: plan.W, H: plan.H, fps: plan.fps, style: project.style,
      cuts: plan.cuts.length, lines: plan.lines.length,
      duration: +plan.duration.toFixed(1),
      capturedPlan: fx.plan || null,
    },
    replan: replanSt,
    sweep, realtime, thumbs,
    memory: { beforeMB: env.heapMB, afterSweepMB: heapAfterSweep, afterRtMB: heapAfterRt },
    finishedAt: new Date().toISOString(),
    wallSec: +((Date.now() - P.startedAt) / 1000).toFixed(1),
  };
  P.report = report;
  P.progress = 1;
  console.table(sweep.worstCuts);
  console.log(`[perfsim] ${report.verdict.pass ? 'PASS' : 'FAIL'}`, report);
  setStatus('done', report.verdict.pass ? 'PASS' : 'FAIL');
}
