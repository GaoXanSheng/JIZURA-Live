/* 导出流程（原 12_ui.js 的 syncOut/codecNote/runExport 的非 DOM 部分）
   进度与状态经 bus 通知 OutTab / EasyPanel。 */
import { J } from '../engine';
import { S } from './state';
import { bus } from './bus';
import { toast, exportRange, rangeSuffix } from './look';
import { flushSave } from './project';
import { ensureFonts, invalidateFontKey, pause, replan } from './engine';
import { autosave } from './project';

function baseName() {
  const k = J.keyMode(S.project);
  return ((S.project.title || 'jizura').replace(/[\\/:*?"<>|]+/g, '_').slice(0, 60) || 'jizura') + (k ? (k === 'green' ? '_greenback' : '_blackback') : '');
}

export const canPickFile = () => typeof (window as any).showSaveFilePicker === 'function' && typeof VideoEncoder !== 'undefined';

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

export async function runExport(kind: 'mp4' | 'mp4file' | 'png' | 'pnga' | 'pngl') {
  if (S.exporting) return;
  let file: any = null, fileName = '';
  if (kind === 'mp4file') {
    try {
      const hnd = await (window as any).showSaveFilePicker({
        suggestedName: baseName() + rangeSuffix() + '.mp4',
        types: [{ description: 'MP4', accept: { 'video/mp4': ['.mp4'] } }],
      });
      file = await hnd.createWritable(); fileName = hnd.name;
    } catch (e: any) {
      if (e && e.name === 'AbortError') return;
      toast('无法打开保存位置：' + (e && e.message ? e.message : e));
      return;
    }
  }
  pause();
  const ac = new AbortController(); S.exporting = ac;
  bus.emit('export-start');
  const onProgress = (p: number, m?: string) => bus.emit('export-progress', p * 100, m);
  bus.emit('export-progress', 0, '准备中…');
  let wake: any = null;
  try { if ((navigator as any).wakeLock) wake = await (navigator as any).wakeLock.request('screen'); } catch { wake = null; }
  const t0 = performance.now();
  try {
    await ensureFonts();
    const lost = J.missingUserFonts(J.fontsOfPlan(S.plan).concat(Object.values(S.project.fonts || {})));
    if (lost.length) throw new Error(`导入的字体（${[...new Set(lost as string[])].join('・')}）不在此浏览器中，已停止导出。请在「字体」里重新载入同一文件，或改用其它字体`);
    if (kind === 'mp4' || kind === 'mp4file') {
      const plan = S.plan, range = exportRange(), span = J.exportSpan(plan, range);
      const r = await J.exportMP4({ plan, project: S.project, audio: S.project.includeAudio !== false ? S.audio : null, quality: S.project.quality || 'high', onProgress, signal: ac.signal, range, file });
      file = null;
      let msg = `完成 ${r.blob ? (r.blob.size / 1048576).toFixed(1) + 'MB・' : ''}${r.codec}${r.audio ? ' + ' + r.audio.toUpperCase() : ''}・${((performance.now() - t0) / 1000).toFixed(0)}秒`;
      if (r.blob) {
        const name = baseName() + rangeSuffix() + '.mp4';
        const res = await J.saveFile(name, r.blob);
        if (res === 'declined') msg += '（保存已取消）';
        offerShare(r.blob, name);
      } else msg += `・已保存到「${fileName}」`;
      if (r.tried && r.tried.length) msg += '（第一种方法失败，已改用其它编码器导出）';
      if (r.audioWanted && r.audio !== 'aac') {
        await J.saveFile(baseName() + rangeSuffix() + '_audio.wav', J.audioWav(S.audio.buffer, span.dur, span.t0));
        msg += r.audio ? '。此浏览器音频为 Opus，iPhone・QuickTime 等可能无声，因此额外保存了 WAV 音频' : '。此浏览器无法导出音频，已单独保存 WAV（请在视频编辑软件中叠加）';
      } else if (S.project.includeAudio !== false && !S.audio && S.project.audioName) msg += '。未载入歌曲，导出为无音频（请从「载入歌曲」重新载入）';
      bus.emit('export-progress', 100, msg);
    } else {
      const blob = await J.exportPNGZip({ plan: S.plan, project: S.project, transparent: kind === 'pnga', layers: kind === 'pngl', onProgress, signal: ac.signal, range: exportRange() });
      bus.emit('export-progress', 100, `完成 ${(blob.size / 1048576).toFixed(1)}MB`);
      await J.saveFile(baseName() + rangeSuffix() + (kind === 'pnga' ? '_alpha' : kind === 'pngl' ? '_layers' : '') + '_png.zip', blob);
    }
  } catch (e: any) {
    bus.emit('export-progress', 0, '错误: ' + (e && e.message ? e.message : e));
    console.error(e);
    if (file) { try { await file.abort(); } catch {} }
  } finally {
    S.exporting = null; S.need = true;
    bus.emit('export-end');
    try { if (wake) await wake.release(); } catch {}
    codecNote();
  }
}

/* 手机：分享面板是进相册/文件的可靠途径 */
function offerShare(blob: Blob, name: string) {
  let f: File | null = null;
  try { f = new File([blob], name, { type: blob.type || 'video/mp4' }); } catch { return; }
  const nav = navigator as any;
  if (!nav.canShare || !nav.share || !nav.canShare({ files: [f] })) return;
  bus.emit('export-share', { file: f, name });
}

/* 输出参数同步 + 广播（组件监听 'out' 事件刷新表单） */
function syncOut() {
  bus.emit('out');
}

export function setAspect(v: string) { S.project.aspect = v; syncOut(); invalidateFontKey(); replan(); }
export function setRes(v: number) { S.project.res = v; syncOut(); autosave(); }
export function setFps(v: number) { S.project.fps = v; syncOut(); replan(); }
export function setQuality(v: string) { S.project.quality = v; autosave(); }
export function setKeyBg(v: string) {
  S.project.keyBg = v; syncOut(); bus.emit('sync-ui'); replan(); flushSave();
  const k = J.keyMode(S.project);
  toast(k ? `背景：${k === 'green' ? '绿幕' : '黑幕'}（只有白色文字与演出）` : '背景：常规（风格配色）');
}
export function setIncludeAudio(v: boolean) { S.project.includeAudio = v; autosave(); }
export function setCenterFree(v: boolean) {
  S.project.centerFree = v; syncOut(); replan(); flushSave();
  const tall = S.plan.H > S.plan.W * 1.1 && S.project.centerDir !== 'lr';
  toast(v ? `已留空中央：文字与演出置于${tall ? '上下' : '左右'}` : '已取消留空中央');
}
export function setCenterDir(v: string) {
  S.project.centerDir = v; syncOut(); replan(); flushSave();
  toast(v === 'lr' ? '竖屏画面：左右分列' : '竖屏画面：上下分列');
}
