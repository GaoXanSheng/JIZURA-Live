/* 启动序列：对应旧 12_ui.js 的 boot()。
   另外维护 Spout 镜像依赖的两个全局契约：
   - window.J.ui      —— 状态对象 S
   - window.J.uiApi   —— 宿主动作（SeekSpoutPipeline.cs 注入脚本调用） */
import { J } from '../engine';
import { S, initVolume } from './state';
import { setMode } from './state';
import { loadLocal, flushSave } from './project';
import { replan, seek, bootEngine, loadAudioFile, pause, play, invalidateFontKey } from './engine';
import * as look from './look';
import { bus } from './bus';
import { installSubtitleOverlay } from '../cloud/subtitle';

async function restoreFonts() {
  const list = S.project.userFonts || [];
  if (!list.length) return;
  const missing = await J.restoreUserFonts(list);
  invalidateFontKey(); replan();
  if (missing.length) look.toast(`导入的字体（${missing.join('・')}）不在此浏览器中，请在「字体」里重新载入（此前以近似字体显示）`);
}

function bindKeys() {
  document.addEventListener('keydown', (e: KeyboardEvent) => {
    const tag = (e.target && (e.target as HTMLElement).tagName) || '';
    const typing = /INPUT|TEXTAREA|SELECT/.test(tag) && (e.target as HTMLInputElement).type !== 'range' && (e.target as HTMLInputElement).type !== 'checkbox';
    const dialogOpen = !!document.querySelector('dialog[open]');
    if (S.tap && (e.code === 'Space' || e.code === 'Enter') && !typing) { e.preventDefault(); look.tapNow(); return; }
    if (S.tap && e.code === 'Escape') { pause(); look.stopTap(); return; }
    if (S.tap && e.code === 'Backspace' && !typing) { e.preventDefault(); look.tapBack(); return; }
    if ((e.ctrlKey || e.metaKey) && e.code === 'KeyZ' && !typing && !S.tap) { e.preventDefault(); look.edGo(e.shiftKey ? 1 : -1); return; }
    if ((e.ctrlKey || e.metaKey) && e.code === 'KeyY' && !typing && !S.tap) { e.preventDefault(); look.edGo(1); return; }
    if (typing || dialogOpen) return;
    if (e.code === 'Space') { e.preventDefault(); S.playing ? pause() : play(); }
    else if (e.code === 'ArrowRight') seek(S.t + (e.shiftKey ? 1 : 1 / S.plan.fps));
    else if (e.code === 'ArrowLeft') seek(S.t - (e.shiftKey ? 1 : 1 / S.plan.fps));
    else if (e.code === 'KeyR' && !e.metaKey && !e.ctrlKey && !e.altKey && !S.exporting) { e.preventDefault(); look.omakase(); }
  });
}

export function boot() {
  installSubtitleOverlay();   // 字幕叠加（Renderer.frame 包装，幂等）
  S.project = loadLocal();
  initVolume();
  replan();
  restoreFonts();
  let mode: 'pro' | 'easy' | 'mobile' = window.matchMedia && window.matchMedia('(max-width: 760px)').matches ? 'mobile' : 'easy';
  try { mode = (localStorage.getItem('jizura.mode') as any) || mode; } catch {}
  setMode(mode);
  look.commit();
  bindKeys();
  // 打开在有代表性的帧（第一个切分入场的中段）
  const c0 = S.plan.cuts.find((c: any) => c.line >= 0);
  if (c0) seek(c0.start + Math.min(c0.dur * 0.6, c0.inDur + 0.25));
  bootEngine();
  // 上次的歌曲（与保存项目同名）刷新后自动回来
  if (J.loadSong && S.project.audioName) J.loadSong().then((f: File | null) => { if (f && f.name === S.project.audioName && !S.audio) loadAudioFile(f, true); });

  // ---- 宿主契约（Spout 镜像脚本 / AE 面板） ----
  J.ui = S;
  J.uiApi = {
    toast: look.toast,
    replan,
    syncUI: () => bus.emit('sync-ui'),
    pause,
    play,
    seek,
    flushSave,
    loadAudioFile,
    restartPreview: look.restartPreview,
    exportRange: look.exportRange,
    exportRangeLines: look.exportRangeLines,
  };
  bus.emit('booted');
}

boot();
