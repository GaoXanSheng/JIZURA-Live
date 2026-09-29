/* 手法预览循环（原 12_ui.js 的 previewR/previewPlans/IntersectionObserver 体系）
   Vue 组件把含 canvas[data-g][data-k] 的容器注册进来，循环只画可见的。 */
import { J } from '../engine';
import { S } from './state';

const previewR = new J.Renderer();
const previewPlans = new Map<string, any>();
const previewLive = new Set<HTMLCanvasElement>();
let previewObs: IntersectionObserver | null = null;
let previewRaf = 0;
let previewLast = 0;

/* 手法 tab 可见 或 切分替换面板打开 时才跑预览循环 */
function paneOpen(): boolean {
  const pane = document.querySelector('[data-pane="tech"]') as HTMLElement | null;
  if (pane && pane.offsetParent) return true;
  const pick = document.querySelector('.cut-pick') as HTMLElement | null;
  return !!(pick && pick.offsetParent);
}

function cacheKey(g: string, k: string) {
  const p = S.project;
  return [p.style, p.aspect, g, k, p.colors && p.colors.enabled ? JSON.stringify(p.colors) : '', JSON.stringify(p.fonts || {})].join('|');
}

function getPreviewPlan(g: string, k: string) {
  const id = cacheKey(g, k);
  let plan = previewPlans.get(id);
  if (plan) return plan;
  plan = J.previewPlan(S.project, g, k);
  previewPlans.set(id, plan);
  if (previewPlans.size > 500) {
    const first = previewPlans.keys().next().value;
    if (first !== undefined) previewPlans.delete(first);
  }
  return plan;
}

export function previewTime(plan: any, g: string, now: number) {
  const c = plan.cuts[plan.cuts.length - 1];
  const u = now / 1000;
  if (g === 'enter') return c.start + ((u % 1.5) / 1.5) * Math.max(0.3, c.inDur);
  if (g === 'exit') { const od = Math.max(0.3, c.outDur || 0.5); return c.end - od + ((u % 1.5) / 1.5) * od; }
  if (g === 'trans') return c.start + ((u % 1.7) / 1.7) * (c.transDur || 0.35);
  if (g === 'fx') return (u % 1.05);
  const span = Math.max(1.6, c.dur * 0.96);
  return c.start + ((u % span) / span) * (c.dur * 0.96);
}

export function paintTechCanvas(cv: HTMLCanvasElement, g: string, k: string, t: number) {
  const plan = getPreviewPlan(g, k);
  const ctx = cv.getContext('2d')!;
  try {
    previewR.frame(ctx, plan, t, { scale: cv.width / plan.W, fast: true, noHud: true, noGhost: g !== 'fx' });
  } catch {
    ctx.fillStyle = '#131316'; ctx.fillRect(0, 0, cv.width, cv.height);
  }
}

function ensureObs() {
  if (previewObs) return previewObs;
  previewObs = new IntersectionObserver((ents) => {
    ents.forEach(e => { if (e.isIntersecting && e.intersectionRatio > 0) previewLive.add(e.target as HTMLCanvasElement); else previewLive.delete(e.target as HTMLCanvasElement); });
    kick();
  }, { root: null, rootMargin: '40px 0px', threshold: [0, 0.12, 0.4] });
  return previewObs;
}

export function resetWatch() {
  previewLive.clear();
  if (previewObs) { previewObs.disconnect(); previewObs = null; }
}

function kick() {
  if (previewRaf) return;
  const tickFn = (now: number) => {
    previewRaf = 0;
    if (document.hidden || S.exporting || !paneOpen() || !previewLive.size) return;
    if (now - previewLast >= 70) {
      previewLast = now;
      for (const cv of previewLive) {
        if (!cv.isConnected) { previewLive.delete(cv); continue; }
        const g = cv.dataset.g!, k = cv.dataset.k!;
        if (!g || !k) continue;
        paintTechCanvas(cv, g, k, previewTime(getPreviewPlan(g, k), g, now));
      }
    }
    previewRaf = requestAnimationFrame(tickFn);
  };
  previewRaf = requestAnimationFrame(tickFn);
}

/* 注册容器内的所有 canvas[data-g]（组件挂载/展开/过滤后调用） */
export function registerThumbs(list: HTMLElement) {
  const now = performance.now();
  list.querySelectorAll<HTMLCanvasElement>('canvas[data-g]').forEach(cv => {
    previewLive.add(cv);
    ensureObs().observe(cv);
    const g = cv.dataset.g!, k = cv.dataset.k!;
    if (g && k) paintTechCanvas(cv, g, k, previewTime(getPreviewPlan(g, k), g, now));
  });
  kick();
}

document.addEventListener('visibilitychange', () => { if (!document.hidden) kick(); });
