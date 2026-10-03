// 导出流程：进度与状态经 bus 通知 OutTab。
// 勾选模型（export-spec.ts）：OutTab 把 ExportCfg 交给 buildSpec 校验出 ExportSpec，
// runSpec 按 spec 分发；runExport(kind) 只是 MCP/agent-api 的兼容层（旧 kind 归一成
// spec，行为与勾选模型上线前逐一对齐）。
// 渲染+编码默认在专用 Worker 线程跑，导出期间预览照常（S.exportOnMain=false）；
// Worker 失败自动回退主线程同路径，回退期间置 S.exportOnMain 让预览循环让路。
// 「大视频直接写文件」需要主线程的文件句柄；纯字幕层（customDrawOnly）依赖主线程
// 字幕模块——两者都只走主线程。
import {J} from '../engine';
import {applyAudioFx, S} from './state';
import {subPayloadForPlan} from '../cloud/subtitle';
import {makeSubtitleOverlay, type SubPayload} from '../cloud/subtitle-draw';
import {buildSpec, type ExportCfg, type ExportSpec, normalizeCfg, specFromLegacyKind} from './export-spec';
import {bus} from './bus';
import {exportRange, rangeSuffix, toast} from './look';
import {autosave, flushSave} from './project';
import {ensureFonts, invalidateFontKey, pause, replan} from './engine';

function baseName() {
  const k = J.keyMode(S.project);
  return ((S.project.title || 'jizura').replace(/[\\/:*?"<>|]+/g, '_').slice(0, 60) || 'jizura') + (k ? (k === 'green' ? '_greenback' : '_blackback') : '');
}

// 各 spec 的最终文件名（含后缀），另存为与静默下载共用。
// 旧 kind 的名字对齐旧版：webm=_alpha / webmfront=_front / subwebm=_subtitle，
// png 家族 = _png / _alpha_png / _layers_png / _front_png；新增纯字幕 PNG = _subtitle_png
function exportFileName(spec: ExportSpec) {
  const suffix =
      spec.kind === 'mp4' || spec.kind === 'mp4file' ? '.mp4'
          : spec.kind === 'webm' ? (spec.customDrawOnly ? '_subtitle.webm'
              : spec.layer === 'front' ? '_front.webm'
                  : spec.transparent ? '_alpha.webm' : '.webm')
              : spec.customDrawOnly ? '_subtitle_png.zip'
                  : spec.layers ? '_layers_png.zip'
                      : spec.layer === 'front' ? '_front_png.zip'
                          : spec.transparent ? '_alpha_png.zip' : '_png.zip';
  return baseName() + rangeSuffix() + suffix;
}

export const canPickFile = () => typeof (window as any).showSaveFilePicker === 'function' && typeof VideoEncoder !== 'undefined';

// 桌面版（CefSharp 宿主）消息通道：MCP 导出用它设置免确认落盘目录
const postHost = (m: any) => {
  try {
    const c = (window as any).CefSharp;
    if (c && typeof c.PostMessage === 'function') c.PostMessage(m);
  } catch { // 浏览器环境无宿主，忽略
  }
};

// 编解码检查在部分浏览器较慢：等首帧预览画完再问
export async function codecNote() {
  const [w, h] = J.outputSize(S.project);
  const vc = await J.pickVideoCodec(w, h, S.project.fps, 12e6);
  bus.emit('codec', {
    ok: !!vc,
    label: vc ? vc.label : null,
    text: vc
        ? `此浏览器将以 ${vc.label} 导出（${w}×${h} / ${S.project.fps}fps）。导出期间请保持页面打开。`
        : '此浏览器不支持视频编码（WebCodecs）。请用最新版 Chrome / Edge 打开，或使用连号 PNG。',
  });
  return vc;
}

// Worker 导出线程：spec.kind=mp4/webm/png 可进 Worker；mp4file 需要主线程弹出的
// 可写文件流，纯字幕层（customDrawOnly）依赖主线程字幕模块，都不适用。
// 失败一律抛回调用方走主线程同路径（Worker 侧字体缺失等错误在主线程多半能成）
const workerExportable = () =>
    typeof Worker !== 'undefined' && typeof OffscreenCanvas !== 'undefined' && typeof VideoEncoder !== 'undefined';

function runExportInWorker(spec: ExportSpec,
                           sub: SubPayload | null,
                           onProgress: (p: number, m?: string) => void,
                           signal: AbortSignal): Promise<any> {
  return new Promise((resolve, reject) => {
    let w: Worker;
    try {
      w = new Worker(new URL('./worker/export-worker.ts', import.meta.url), {type: 'module'});
    } catch (e: any) {
      reject(Object.assign(new Error('worker 创建失败：' + (e && e.message || e)), {workerFallback: true}));
      return;
    }
    let settled = false;
    const onAbort = () => finish(() => reject(new Error('キャンセルしました')));
    const finish = (fn: () => void) => {
      if (settled) return;
      settled = true;
      signal.removeEventListener('abort', onAbort);
      try {
        w.terminate();
      } catch { // 已退出
      }
      fn();
    };
    signal.addEventListener('abort', onAbort);
    w.onerror = (e) => finish(() => reject(Object.assign(new Error('worker 加载失败：' + (e.message || '')), {workerFallback: true})));
    w.onmessage = (ev) => {
      const m = ev.data;
      if (m.type === 'progress') onProgress(m.p, m.msg);
      else if (m.type === 'done') finish(() => resolve(m.result));
      else if (m.type === 'error') finish(() => reject(Object.assign(new Error(m.message), {workerFallback: true})));
    };

    const start: any = {
      cmd: 'start', spec,
      project: S.project,
      beats: S.audio ? (S.audio.beats || []) : [],
      quality: S.project.quality || 'high',
      range: exportRange(),
      audio: null,
      sub: sub || null,
    };
    const transfer: Transferable[] = [];
    // AudioBuffer 不可跨线程克隆：改发各声道数据（转移所有权），Worker 端重建
    if (spec.kind === 'mp4' && spec.embedAudio && S.audio) {
      const buf = S.audio.buffer;
      const channels: Float32Array[] = [];
      for (let c = 0; c < Math.min(2, buf.numberOfChannels); c++) {
        const d = buf.getChannelData(c).slice();
        channels.push(d);
        transfer.push(d.buffer);
      }
      start.audio = {channels, sampleRate: buf.sampleRate};
    }
    w.postMessage(start, transfer);
  });
}

// 字幕载荷：bakeSubs 时按本页语义（翻译映射→行内注音→内容模式）预解析，
// 主线程回退与 Worker 共用同一份，保证两边逐帧一致
function subPayloadFor(spec: ExportSpec, plan: any): SubPayload | null {
  return spec.bakeSubs ? subPayloadForPlan(plan) : null;
}

// spec 执行器：一次导出 = 校验过的 spec + 落盘选项。opts.wav：成片之外另存 WAV 音频
// （WebM/PNG 勾了音频、用户在确认框选「另存」时为 true）
async function runSpec(spec: ExportSpec, opts: { dir?: string; wav?: boolean } = {}) {
  if (S.exporting) return;
  let file: any = null, fileName = '';
  if (spec.kind === 'mp4file') {
    try {
      const hnd = await (window as any).showSaveFilePicker({
        suggestedName: baseName() + rangeSuffix() + '.mp4',
        types: [{description: 'MP4', accept: {'video/mp4': ['.mp4']}}],
      });
      file = await hnd.createWritable();
      fileName = hnd.name;
    } catch (e: any) {
      if (e && e.name === 'AbortError') return;
      toast('无法打开保存位置：' + (e && e.message ? e.message : e));
      return;
    }
  }
  // 「导出时询问保存位置」：showSaveFilePicker 需要用户手势，必须在点击现场把句柄选好，
  // 导完直接写入该句柄；取消选择即放弃本次导出
  let handle: any = null;
  if (spec.kind !== 'mp4file' && S.project.askSaveLocation && canPickFile()) {
    const accept = spec.kind === 'mp4' ? {description: 'MP4 视频', accept: {'video/mp4': ['.mp4']}}
        : spec.kind === 'webm' ? {description: 'WebM 视频', accept: {'video/webm': ['.webm']}}
            : {description: 'ZIP 压缩包', accept: {'application/zip': ['.zip']}};
    try {
      handle = await (window as any).showSaveFilePicker({suggestedName: exportFileName(spec), types: [accept]});
    } catch (e: any) {
      if (e && e.name === 'AbortError') return;
      toast('无法打开保存位置：' + (e && e.message ? e.message : e));
      return;
    }
  }
  const persist = async (blob: Blob, name: string) => {
    if (!handle) return J.saveFile(name, blob);
    const w = await handle.createWritable();
    await w.write(blob);
    await w.close();
    return 'saved';
  };
  // MCP 指定导出目录：桌面版宿主把本次所有下载直接落盘到该目录，不弹另存为；
  // 浏览器环境没有宿主，回退默认下载目录。导出结束（含出错）恢复弹另存为
  if (opts.dir) postHost({type: 'set_autosave_dir', dir: opts.dir});
  const savedNote = () => opts.dir ? `・已保存到 ${opts.dir}` : '';
  // 纯字幕层校验：content 是 off 的话导出只会得到空文件（buildSpec 已挡 UI 路径，
  // 这里兜住 MCP 旧 kind）
  if (spec.customDrawOnly && (!J.subtitleCfg || J.subtitleCfg().content === 'off')) {
    throw new Error('字幕渲染未开启：请先在「字幕」面板选择要导出的字幕内容（翻译/音译）');
  }
  // 字幕预览叠加层在导出期间一律关闭：烤入走显式 overlay（主线程/Worker 同一载荷），
  // 关掉全局叠加才不会在主线程路径画两层
  const prevNoSub = (window as any).__jl_no_sub;
  (window as any).__jl_no_sub = true;
  pause();
  const ac = new AbortController();
  S.exporting = ac;
  S.exportOnMain = false;   // Worker 优先：预览不熄火；落到主线程回退时再置 true
  bus.emit('export-start');
  const onProgress = (p: number, m?: string) => bus.emit('export-progress', p * 100, m);
  bus.emit('export-progress', 0, '准备中…');
  let wake: any = null;
  try {
    if ((navigator as any).wakeLock) wake = await (navigator as any).wakeLock.request('screen');
  } catch {
    wake = null;
  }
  const t0 = performance.now();
  try {
    await ensureFonts();
    const lost = J.missingUserFonts(J.fontsOfPlan(S.plan).concat(Object.values(S.project.fonts || {})));
    if (lost.length) throw new Error(`导入的字体（${[...new Set(lost as string[])].join('・')}）不在此浏览器中，已停止导出。请在「字体」里重新载入同一文件，或改用其它字体`);
    if (spec.kind === 'mp4' || spec.kind === 'mp4file') {
      const plan = S.plan, range = exportRange();
      const sub = subPayloadFor(spec, plan);
      const overlay = sub ? makeSubtitleOverlay(sub) : null;
      let r: any = null;
      if (spec.kind === 'mp4' && workerExportable()) {
        try {
          r = await runExportInWorker(spec, sub, onProgress, ac.signal);
        } catch (we: any) {
          if (ac.signal.aborted) throw new Error('キャンセルしました');
          console.warn('Worker 导出失败，回退主线程渲染：', we && we.message || we);
          bus.emit('export-progress', 0, 'Worker 导出失败，回退主线程渲染…');
          r = null;
        }
      }
      if (!r) {
        S.exportOnMain = true;   // 主线程逐帧渲染，预览循环让路（engine.tick 门控）
        r = await J.exportMP4({
          plan,
          project: S.project,
          audio: spec.embedAudio ? S.audio : null,
          quality: S.project.quality || 'high',
          onProgress,
          signal: ac.signal,
          range,
          file,
          overlay,
        });
      }
      file = null;
      let msg = `完成 ${r.blob ? (r.blob.size / 1048576).toFixed(1) + 'MB・' : ''}${r.codec}${r.audio ? ' + ' + r.audio.toUpperCase() : ''}・${((performance.now() - t0) / 1000).toFixed(0)}秒`;
      if (r.blob) {
        const name = exportFileName(spec);
        const res = await persist(r.blob, name);
        if (res === 'declined') msg += '（保存已取消）';
        offerShare(r.blob, name);
      } else msg += `・已保存到「${fileName}」`;
      if (r.tried && r.tried.length) msg += '（第一种方法失败，已改用其它编码器导出）';
      if (r.audioWanted && r.audio !== 'aac') {
        // 与内嵌音轨同一条链路（exportSpan 选段 + audioFx 离线渲染）补一份 WAV
        const w = await J.exportWav({plan, project: S.project, audio: S.audio, signal: ac.signal, range});
        await J.saveFile(baseName() + rangeSuffix() + '_audio.wav', w.blob);
        msg += r.audio ? '。此浏览器音频为 Opus，iPhone・QuickTime 等可能无声，因此额外保存了 WAV 音频' : '。此浏览器无法导出音频，已单独保存 WAV（请在视频编辑软件中叠加）';
      } else if (spec.embedAudio && !S.audio && S.project.audioName) msg += '。未载入歌曲，导出为无音频（请从「载入歌曲」重新载入）';
      msg += savedNote();
      bus.emit('export-progress', 100, msg);
    } else if (spec.kind === 'webm') {
      let r: any = null;
      // 纯字幕层只走主线程：字幕绘制（翻译映射/样式配置）只存在于主线程，Worker 不参与
      if (!spec.customDrawOnly && workerExportable()) {
        try {
          r = await runExportInWorker(spec, subPayloadFor(spec, S.plan), onProgress, ac.signal);
        } catch (we: any) {
          if (ac.signal.aborted) throw new Error('キャンセルしました');
          console.warn('Worker 导出失败，回退主线程渲染：', we && we.message || we);
          bus.emit('export-progress', 0, 'Worker 导出失败，回退主线程渲染…');
          r = null;
        }
      }
      if (!r) {
        const customDraw = spec.customDrawOnly ? J.subtitleFrame : null;
        if (spec.customDrawOnly && !customDraw) throw new Error('字幕模块未就绪，请刷新页面后重试');
        const sub = subPayloadFor(spec, S.plan);
        S.exportOnMain = true;
        r = await J.exportWebM({
          plan: S.plan,
          project: S.project,
          frontOnly: spec.layer === 'front',
          transparent: spec.transparent,
          customDraw,
          overlay: sub ? makeSubtitleOverlay(sub) : null,
          onProgress,
          signal: ac.signal,
          range: exportRange(),
        });
      }
      const name = exportFileName(spec);
      const res = await J.saveFile(name, r.blob);
      let msg = `完成 ${(r.blob.size / 1048576).toFixed(1)}MB・${r.codec}・${((performance.now() - t0) / 1000).toFixed(0)}秒`;
      if (res === 'declined') msg += '（保存已取消）';
      else offerShare(r.blob, name);
      bus.emit('export-progress', 100, msg + savedNote());
    } else if (spec.kind === 'png') {
      let fromWorker: Blob | null = null;
      if (!spec.customDrawOnly && workerExportable()) {
        try {
          fromWorker = (await runExportInWorker(spec, subPayloadFor(spec, S.plan), onProgress, ac.signal)).blob;
        } catch (we: any) {
          if (ac.signal.aborted) throw new Error('キャンセルしました');
          console.warn('Worker 导出失败，回退主线程渲染：', we && we.message || we);
          bus.emit('export-progress', 0, 'Worker 导出失败，回退主线程渲染…');
        }
      }
      let blob = fromWorker;
      if (!blob) {
        const sub = subPayloadFor(spec, S.plan);
        S.exportOnMain = true;
        blob = await J.exportPNGZip({
          plan: S.plan,
          project: S.project,
          transparent: spec.transparent,
          layers: spec.layers,
          frontOnly: spec.layer === 'front' && !spec.layers,
          customDraw: spec.customDrawOnly ? J.subtitleFrame : null,
          overlay: sub ? makeSubtitleOverlay(sub) : null,
          onProgress,
          signal: ac.signal,
          range: exportRange(),
        });
      }
      // 两条路径必有一份产物（Worker result.blob / 主线程 ZIP），d.ts 的可空标注只是保守
      const out = blob as Blob;
      bus.emit('export-progress', 100, `完成 ${(out.size / 1048576).toFixed(1)}MB` + savedNote());
      await J.saveFile(exportFileName(spec), out);
    }
    // WebM/PNG 勾了音频（用户确认「另存 WAV」）：成片落盘后补一份音轨
    if (opts.wav && S.audio) {
      bus.emit('export-progress', 100, '导出 WAV 音频…');
      const w = await J.exportWav({plan: S.plan, project: S.project, audio: S.audio, signal: ac.signal, range: exportRange()});
      await J.saveFile(baseName() + rangeSuffix() + '_audio.wav', w.blob);
    }
  } catch (e: any) {
    bus.emit('export-progress', 0, '错误: ' + (e && e.message ? e.message : e));
    console.error(e);
    if (file) {
      try {
        await file.abort();
      } catch {
      }
    }
  } finally {
    (window as any).__jl_no_sub = prevNoSub;
    S.exportOnMain = false;
    if (opts.dir) postHost({type: 'set_autosave_dir', dir: null});
    S.exporting = null;
    S.need = true;
    bus.emit('export-end');
    try {
      if (wake) await wake.release();
    } catch {
    }
    codecNote();
  }
}

// MCP/agent-api 兼容层：旧 kind 归一成 spec 执行。MCP 的 start_export 零改动
export async function runExport(kind: 'mp4' | 'mp4file' | 'png' | 'pnga' | 'pngl' | 'pngf' | 'webm' | 'webmfront' | 'subwebm',
                                opts: { dir?: string } = {}) {
  const spec = specFromLegacyKind(kind, S.project);
  if (!spec) throw new Error('未知导出类型：' + kind);
  await runSpec(spec, opts);
}

// 导出面板勾选：校验 + 执行。返回错误文案（UI 直接 toast），成功返回 null。
// cfgOverride 允许单次覆盖（MCP 未来可复用）；opts.wav 仅在 spec 判定 wavToo 且已载歌时生效
export async function runExportCfg(cfgOverride: Partial<ExportCfg> | null,
                                   opts: { dir?: string; wav?: boolean } = {}): Promise<string | null> {
  const cfg = normalizeCfg(Object.assign({}, exportCfg(), cfgOverride || {}));
  const r = buildSpec(cfg, {
    subOn: !!(J.subtitleCfg && J.subtitleCfg().content !== 'off'),
    audioLoaded: !!S.audio,
  });
  if (!r.ok) return r.error;
  await runSpec(r.spec, Object.assign({}, opts, {wav: !!opts.wav && r.wavToo && !!S.audio}));
  return null;
}

// 独立音频导出（WAV 按钮）：不渲染画面，只出音轨
export async function runExportWav() {
  if (S.exporting) return;
  if (!S.audio) {
    toast('未载入歌曲，无法导出音频（请从「载入歌曲」重新载入）');
    return;
  }
  const ac = new AbortController();
  S.exporting = ac;
  bus.emit('export-start');
  const onProgress = (p: number, m?: string) => bus.emit('export-progress', p * 100, m);
  bus.emit('export-progress', 0, '准备中…');
  const t0 = performance.now();
  try {
    const w = await J.exportWav({plan: S.plan, project: S.project, audio: S.audio, onProgress, signal: ac.signal, range: exportRange()});
    const res = await J.saveFile(baseName() + rangeSuffix() + '_audio.wav', w.blob);
    let msg = `完成 ${(w.blob.size / 1048576).toFixed(1)}MB・WAV・${((performance.now() - t0) / 1000).toFixed(0)}秒`;
    if (res === 'declined') msg += '（保存已取消）';
    bus.emit('export-progress', 100, msg);
  } catch (e: any) {
    bus.emit('export-progress', 0, '错误: ' + (e && e.message ? e.message : e));
    console.error(e);
  } finally {
    S.exporting = null;
    S.need = true;
    bus.emit('export-end');
    codecNote();
  }
}

// 当前勾选（project.exportCfg 归一化视图）
export function exportCfg(): ExportCfg {
  return normalizeCfg(S.project.exportCfg);
}

// 导出面板勾选变更：落 project.exportCfg。bgStyle 同时镜像到 project.keyBg
// （plan 从 keyBg 读抠像底色），所以变更背景形态要 replan
export function setExportCfg(patch: Partial<ExportCfg>) {
  const next = normalizeCfg(Object.assign({}, S.project.exportCfg, patch));
  S.project.exportCfg = next;
  if (patch.bgStyle !== undefined && next.bgStyle !== (S.project.keyBg || 'off')) {
    setKeyBg(next.bgStyle);
    return;
  }
  autosave();
  bus.emit('sync-ui');
}

// 手机：分享面板是进相册/文件的可靠途径
function offerShare(blob: Blob, name: string) {
  let f: File | null = null;
  try {
    f = new File([blob], name, {type: blob.type || 'video/mp4'});
  } catch {
    return;
  }
  const nav = navigator as any;
  if (!nav.canShare || !nav.share || !nav.canShare({files: [f]})) return;
  bus.emit('export-share', {file: f, name});
}

// 输出参数同步 + 广播（组件监听 'out' 事件刷新表单）
function syncOut() {
  bus.emit('out');
}

export function setAspect(v: string) {
  S.project.aspect = v;
  syncOut();
  invalidateFontKey();
  replan();
}

export function setRes(v: number) {
  S.project.res = v;
  syncOut();
  autosave();
}

export function setFps(v: number) {
  S.project.fps = v;
  syncOut();
  replan();
}

// AE 工程 JSON：完整烤熟的 plan（v2，含每个切分的手法/参数/时间、events、字体表与
// 语言映射、AE_MAP 对照表，本 fork 新增条目由它映射到最近似原版手法）。
// 在 AE 里 File→Scripts→Run Script File 跑上游发布的 JIZURA_AE.jsx，面板里选本文件
// 即可逐步重建合成；morph 在 AE 侧以 inkBlob 转场近似（webMorph 标记）。
export async function exportAeJson() {
    if (!S.plan) return;
    const data = J.planForAE(S.plan, S.project, exportRange());
    await J.saveFile(baseName() + rangeSuffix() + '_ae.json', new Blob([JSON.stringify(data, null, 1)], {type: 'application/json'}));
    toast('已导出 AE 工程 JSON：在 AE 中运行 JIZURA_AE.jsx 后选择该文件');
}

export function setQuality(v: string) {
  S.project.quality = v;
  autosave();
}

// 背景形态（常规/绿幕/黑幕）：写 project.keyBg（plan 渲染用）并镜像进 exportCfg，
// SettingsTab 的同款下拉也走这里
export function setKeyBg(v: string) {
  const k = v === 'green' || v === 'black' ? v : 'off';
  S.project.keyBg = k;
  S.project.exportCfg = normalizeCfg(Object.assign({}, S.project.exportCfg, {bgStyle: k}));
  syncOut();
  bus.emit('sync-ui');
  replan();
  flushSave();
  toast(k ? `背景：${k === 'green' ? '绿幕' : '黑幕'}（只有白色文字与演出）` : '背景：常规（风格配色）');
}

export function setAskSaveLocation(v: boolean) {
  S.project.askSaveLocation = v;
  autosave();
}

// 音频特效（预览与导出同时生效）：清洗后落 project，立即更新预览图并广播
export function setAudioFx(patch: any) {
  S.project.audioFx = J.sanitizeAudioFx(Object.assign({}, S.project.audioFx || {}, patch || {}));
  autosave();
  applyAudioFx();
  syncOut();
  bus.emit('sync-ui');   // Transport 的「特效」按钮点亮态
}

export function setCenterFree(v: boolean) {
  S.project.centerFree = v;
  syncOut();
  replan();
  flushSave();
  const tall = S.plan.H > S.plan.W * 1.1 && S.project.centerDir !== 'lr';
  toast(v ? `已留空中央：文字与演出置于${tall ? '上下' : '左右'}` : '已取消留空中央');
}

export function setCenterDir(v: string) {
  S.project.centerDir = v;
  syncOut();
  replan();
  flushSave();
  toast(v === 'lr' ? '竖屏画面：左右分列' : '竖屏画面：上下分列');
}
