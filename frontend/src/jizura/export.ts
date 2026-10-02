/* 导出流程（原 12_ui.js 的 syncOut/codecNote/runExport 的非 DOM 部分）
   进度与状态经 bus 通知 OutTab / EasyPanel。
   渲染+编码默认在专用 Worker 线程跑（worker/export-worker.ts）：导出期间主线程
   （预览/UI）不再被逐帧渲染占用，也不与引擎抢主线程预算；Worker 失败自动回退
   主线程同路径。「大视频直接写文件」需要主线程的文件句柄，不走 Worker。 */
import {J} from '../engine';
import {applyAudioFx, S} from './state';
import {bus} from './bus';
import {exportRange, rangeSuffix, toast} from './look';
import {autosave, flushSave} from './project';
import {ensureFonts, invalidateFontKey, pause, replan} from './engine';

function baseName() {
  const k = J.keyMode(S.project);
  return ((S.project.title || 'jizura').replace(/[\\/:*?"<>|]+/g, '_').slice(0, 60) || 'jizura') + (k ? (k === 'green' ? '_greenback' : '_blackback') : '');
}

/* 各 kind 的最终文件名（含后缀），「另存为」与静默下载共用 */
function exportFileName(kind: string) {
  return baseName() + rangeSuffix() + (
      kind === 'mp4' ? '.mp4'
          : kind === 'webm' ? '_alpha.webm'
          : kind === 'webmfront' ? '_front.webm'
          : kind === 'subwebm' ? '_subtitle.webm'
          : kind === 'pnga' ? '_alpha_png.zip'
          : kind === 'pngl' ? '_layers_png.zip'
          : kind === 'pngf' ? '_front_png.zip'
          : '_png.zip');
}

export const canPickFile = () => typeof (window as any).showSaveFilePicker === 'function' && typeof VideoEncoder !== 'undefined';

/* 桌面版（CefSharp 宿主）消息通道：MCP 导出用它设置免确认落盘目录 */
const postHost = (m: any) => {
  try {
    const c = (window as any).CefSharp;
    if (c && typeof c.PostMessage === 'function') c.PostMessage(m);
  } catch { /* 浏览器环境无宿主，忽略 */ }
};

/* 编解码检查在部分浏览器较慢：等首帧预览画完再问 */
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

/* ---- Worker 导出线程 ----
   kind=mp4/png/pnga/pngl/pngf/webm/webmfront 可进 Worker；mp4file 需要主线程弹出的可写文件流，不适用。
   失败一律抛给调用方回退主线程同路径（Worker 侧字体缺失等错误在主线程多半能成）。 */
const workerExportable = () =>
    typeof Worker !== 'undefined' && typeof OffscreenCanvas !== 'undefined' && typeof VideoEncoder !== 'undefined';

function runExportInWorker(kind: 'mp4' | 'png' | 'pnga' | 'pngl' | 'pngf' | 'webm' | 'webmfront',
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
      } catch { /* 已退出 */
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
      cmd: 'start', kind,
      project: S.project,
      beats: S.audio ? (S.audio.beats || []) : [],
      quality: S.project.quality || 'high',
      range: exportRange(),
      audio: null,
    };
    const transfer: Transferable[] = [];
    // AudioBuffer 不可跨线程克隆：改发各声道数据（转移所有权），Worker 端重建
    if (kind === 'mp4' && S.audio && S.project.includeAudio !== false) {
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

export async function runExport(kind: 'mp4' | 'mp4file' | 'png' | 'pnga' | 'pngl' | 'pngf' | 'webm' | 'webmfront' | 'subwebm',
                                opts: {dir?: string} = {}) {
  if (S.exporting) return;
  let file: any = null, fileName = '';
  if (kind === 'mp4file') {
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
  /* 「导出时询问保存位置」：showSaveFilePicker 需要用户手势，必须在点击现场把句柄选好，
     导完直接写入该句柄；取消选择即放弃本次导出 */
  let handle: any = null;
  if (kind !== 'mp4file' && S.project.askSaveLocation && canPickFile()) {
    const accept = kind === 'mp4' ? {description: 'MP4 视频', accept: {'video/mp4': ['.mp4']}}
        : kind === 'webm' || kind === 'webmfront' ? {description: 'WebM 视频', accept: {'video/webm': ['.webm']}}
        : {description: 'ZIP 压缩包', accept: {'application/zip': ['.zip']}};
    try {
      handle = await (window as any).showSaveFilePicker({suggestedName: exportFileName(kind), types: [accept]});
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
  /* MCP 指定导出目录：桌面版宿主把本次所有下载直接落盘到该目录，不弹另存为；
     浏览器环境没有宿主，回退默认下载目录。导出结束（含出错）恢复弹另存为 */
  if (opts.dir) postHost({type: 'set_autosave_dir', dir: opts.dir});
  const savedNote = () => opts.dir ? `・已保存到 ${opts.dir}` : '';
  /* 云字幕是预览叠加层，文件导出一律不烤入：与 Worker 路径行为保持一致
     （Worker 里本就没有 overlay）。__jl_no_sub 是 subtitle overlay 的既有抑制开关 */
  const prevNoSub = (window as any).__jl_no_sub;
  (window as any).__jl_no_sub = true;
  pause();
  const ac = new AbortController();
  S.exporting = ac;
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
    if (kind === 'mp4' || kind === 'mp4file') {
      const plan = S.plan, range = exportRange(), span = J.exportSpan(plan, range);
      let r: any = null;
      if (kind === 'mp4' && workerExportable()) {
        try {
          r = await runExportInWorker('mp4', onProgress, ac.signal);
        } catch (we: any) {
          if (ac.signal.aborted) throw new Error('キャンセルしました');
          console.warn('Worker 导出失败，回退主线程渲染：', we && we.message || we);
          bus.emit('export-progress', 0, 'Worker 导出失败，回退主线程渲染…');
          r = null;
        }
      }
      if (!r) {
        r = await J.exportMP4({
          plan,
          project: S.project,
          audio: S.project.includeAudio !== false ? S.audio : null,
          quality: S.project.quality || 'high',
          onProgress,
          signal: ac.signal,
          range,
          file
        });
      }
      file = null;
      let msg = `完成 ${r.blob ? (r.blob.size / 1048576).toFixed(1) + 'MB・' : ''}${r.codec}${r.audio ? ' + ' + r.audio.toUpperCase() : ''}・${((performance.now() - t0) / 1000).toFixed(0)}秒`;
      if (r.blob) {
        const name = exportFileName('mp4');
        const res = await persist(r.blob, name);
        if (res === 'declined') msg += '（保存已取消）';
        offerShare(r.blob, name);
      } else msg += `・已保存到「${fileName}」`;
      if (r.tried && r.tried.length) msg += '（第一种方法失败，已改用其它编码器导出）';
      if (r.audioWanted && r.audio !== 'aac') {
        await J.saveFile(baseName() + rangeSuffix() + '_audio.wav', J.audioWav(S.audio.buffer, span.dur, span.t0));
        msg += r.audio ? '。此浏览器音频为 Opus，iPhone・QuickTime 等可能无声，因此额外保存了 WAV 音频' : '。此浏览器无法导出音频，已单独保存 WAV（请在视频编辑软件中叠加）';
      } else if (S.project.includeAudio !== false && !S.audio && S.project.audioName) msg += '。未载入歌曲，导出为无音频（请从「载入歌曲」重新载入）';
      msg += savedNote();
      bus.emit('export-progress', 100, msg);
    } else if (kind === 'webm' || kind === 'webmfront' || kind === 'subwebm') {
      let r: any = null;
      // subwebm 只走主线程：字幕绘制（翻译映射/样式配置）只存在于主线程，Worker 不参与
      if (kind !== 'subwebm' && workerExportable()) {
        try {
          r = await runExportInWorker(kind, onProgress, ac.signal);
        } catch (we: any) {
          if (ac.signal.aborted) throw new Error('キャンセルしました');
          console.warn('Worker 导出失败，回退主线程渲染：', we && we.message || we);
          bus.emit('export-progress', 0, 'Worker 导出失败，回退主线程渲染…');
          r = null;
        }
      }
      if (!r) {
        const customDraw = kind === 'subwebm' ? J.subtitleFrame : null;
        if (kind === 'subwebm' && !customDraw) throw new Error('字幕模块未就绪，请刷新页面后重试');
        if (customDraw && J.subtitleCfg && J.subtitleCfg().content === 'off') {
          throw new Error('字幕渲染未开启：请先在「字幕」面板选择要导出的字幕内容（翻译/音译）');
        }
        r = await J.exportWebM({
          plan: S.plan,
          project: S.project,
          frontOnly: kind === 'webmfront',
          customDraw,
          onProgress,
          signal: ac.signal,
          range: exportRange()
        });
      }
      const name = baseName() + rangeSuffix() + (kind === 'webm' ? '_alpha' : kind === 'webmfront' ? '_front' : '_subtitle') + '.webm';
      const res = await J.saveFile(name, r.blob);
      let msg = `完成 ${(r.blob.size / 1048576).toFixed(1)}MB・${r.codec}・${((performance.now() - t0) / 1000).toFixed(0)}秒`;
      if (res === 'declined') msg += '（保存已取消）';
      else offerShare(r.blob, name);
      bus.emit('export-progress', 100, msg + savedNote());
    } else {
      let fromWorker: Blob | null = null;
      if (workerExportable()) {
        try {
          fromWorker = (await runExportInWorker(kind, onProgress, ac.signal)).blob;
        } catch (we: any) {
          if (ac.signal.aborted) throw new Error('キャンセルしました');
          console.warn('Worker 导出失败，回退主线程渲染：', we && we.message || we);
          bus.emit('export-progress', 0, 'Worker 导出失败，回退主线程渲染…');
        }
      }
      const blob = fromWorker ?? await J.exportPNGZip({
        plan: S.plan,
        project: S.project,
        transparent: kind === 'pnga' || kind === 'pngf',
        layers: kind === 'pngl',
        frontOnly: kind === 'pngf',
        onProgress,
        signal: ac.signal,
        range: exportRange()
      });
      bus.emit('export-progress', 100, `完成 ${(blob.size / 1048576).toFixed(1)}MB` + savedNote());
      await J.saveFile(baseName() + rangeSuffix() + (kind === 'pnga' ? '_alpha' : kind === 'pngl' ? '_layers' : kind === 'pngf' ? '_front' : '') + '_png.zip', blob);
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

/* 手机：分享面板是进相册/文件的可靠途径 */
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

/* 输出参数同步 + 广播（组件监听 'out' 事件刷新表单） */
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

/* AE 工程 JSON：完整烤熟的 plan（v2，含每个切分的手法/参数/时间、events、
   字体表与语言映射、AE_MAP 对照表——本 fork 新增条目由它映射到最近似原版手法）。
   在 AE 里运行上游发布的 JIZURA_AE.jsx（File→Scripts→Run Script File），
   面板里选择本文件即可逐步重建合成；morph 在 AE 侧以 inkBlob 转场近似（webMorph 标记）。 */
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

export function setKeyBg(v: string) {
  S.project.keyBg = v;
  syncOut();
  bus.emit('sync-ui');
  replan();
  flushSave();
  const k = J.keyMode(S.project);
  toast(k ? `背景：${k === 'green' ? '绿幕' : '黑幕'}（只有白色文字与演出）` : '背景：常规（风格配色）');
}

export function setIncludeAudio(v: boolean) {
  S.project.includeAudio = v;
  autosave();
}

export function setAskSaveLocation(v: boolean) {
  S.project.askSaveLocation = v;
  autosave();
}

/* 音频特效（预览与导出同时生效）：清洗后落 project，立即更新预览图并广播 */
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
