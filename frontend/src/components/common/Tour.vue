<script setup lang="ts">
/* かんたん模式新手引导（简中，6 步） */
import { onMounted, onUnmounted, ref } from 'vue';
import { J } from '../../engine';
import { S } from '../../jizura/state';
import { setMode } from '../../jizura/state';
import { pause } from '../../jizura/engine';

const open = ref(false);
const i = ref(-1);
const step = ref<{ title: string; text: string }>({ title: '', text: '' });
const spot = ref({ left: '0px', top: '0px', width: '0px', height: '0px' });
const bubble = ref({ top: '0px', left: '0px' });
const isLast = ref(false);
const root = ref<HTMLElement | null>(null);

function target(n: number): HTMLElement | null {
  switch (n) {
    case 0: return document.getElementById('lyrics');
    case 1: return (document.querySelector('label.file[title*="mp3"]') as HTMLElement) || document.querySelector('label.file');
    case 2: return (document.querySelector('.omakase') as HTMLElement) || (document.querySelector('#btnOmakaseTop') as HTMLElement);
    case 3: return document.querySelector('.transport .play');
    case 4: return document.querySelector('.line-list');
    case 5: return document.querySelector('[data-pane="out"] .outbtns') || document.querySelector('.outbtns');
    default: return null;
  }
}

const STEPS: { title: string; text: string }[] = [
  { title: '1. 输入歌词', text: '一行就是一句歌词。空行表示稍作停顿；写 [间奏 8] 可以插入 8 秒的间奏（只显示背景与装饰）。' },
  { title: '2. 载入歌曲', text: '载入 mp3 等音频后会自动检测节拍，让画面切换对准节拍。没有歌曲也能制作。「打点同步」可以手动对齐每行开头。' },
  { title: '3. 一键随机生成', text: '自动决定风格、氛围、动效、配色与构成。每按一次都会变成新的方案，用「◀ 上一案」可以回退。' },
  { title: '4. 播放预览', text: '播放看看效果。在下方的时间轴上，可以拖动行界调整每行的开始时间（＋－ 可缩放）。' },
  { title: '5. 微调个别行', text: '每一行都可以：修改歌词（✎）、指定切分数、从该行重新打点同步（◎）、只重做这一行（骰子）、锁定构成。' },
  { title: '6. 导出', text: '选择画面比例（如竖屏 9:16）与分辨率后导出 MP4。用「导出范围」也可以只导出选中的行。' },
];

function show(n: number) {
  if (n < 0 || n >= STEPS.length) return end();
  i.value = n;
  step.value = STEPS[n];
  isLast.value = n === STEPS.length - 1;
  open.value = true;
  const tg = target(n);
  tg?.scrollIntoView({ block: 'center', behavior: 'auto' });
  requestAnimationFrame(() => place(tg));
  root.value?.querySelector<HTMLButtonElement>('.tour-next')?.focus();
}

function place(tg: HTMLElement | null) {
  const el = root.value; if (!el) return;
  const vw = window.innerWidth, vh = window.innerHeight, pad = 6;
  const r = tg ? tg.getBoundingClientRect() : { left: vw / 2, top: vh / 2, width: 0, height: 0, right: vw / 2, bottom: vh / 2 };
  const x0 = Math.max(4, r.left - pad), y0 = Math.max(4, r.top - pad);
  const x1 = Math.min(vw - 4, r.right + pad), y1 = Math.min(vh - 4, r.bottom + pad);
  spot.value = { left: x0 + 'px', top: y0 + 'px', width: Math.max(0, x1 - x0) + 'px', height: Math.max(0, y1 - y0) + 'px' };
  const bub = el.querySelector<HTMLElement>('.tour-bub');
  if (!bub) return;
  const bw = bub.offsetWidth, bh = bub.offsetHeight, gap = 12;
  let top = y1 + gap <= vh - bh - 8 ? y1 + gap : y0 - gap - bh >= 8 ? y0 - gap - bh : Math.max(8, vh - bh - 8);
  let left = J.clamp(x0 + (x1 - x0) / 2 - bw / 2, 8, Math.max(8, vw - bw - 8));
  bubble.value = { top: top + 'px', left: left + 'px' };
}

function start() {
  if (S.exporting || S.tap) return;
  if (S.mode !== 'easy') setMode('easy');
  pause();
  setTimeout(() => show(0), 50);
}
function end() {
  open.value = false; i.value = -1;
  try { localStorage.setItem('jizura.tourDone', '1'); } catch {}
}

function onKey(e: KeyboardEvent) {
  if (i.value < 0) return;
  if (e.key === 'ArrowRight' || e.key === 'Enter') { e.preventDefault(); e.stopImmediatePropagation(); show(i.value + 1); }
  else if (e.key === 'ArrowLeft') { e.preventDefault(); e.stopImmediatePropagation(); show(i.value - 1); }
  else if (e.key === 'Escape') { e.preventDefault(); e.stopImmediatePropagation(); end(); }
  else if (e.key !== 'Tab') e.stopImmediatePropagation();
}

function onReflow() { if (i.value >= 0) place(target(i.value)); }

onMounted(() => {
  document.addEventListener('keydown', onKey, true);
  window.addEventListener('resize', onReflow);
  window.addEventListener('scroll', onReflow, true);
  let seen = false;
  try { seen = localStorage.getItem('jizura.tourDone') === '1'; } catch {}
  if (!seen && S.mode === 'easy') setTimeout(start, 800);   // 首次访问自动开始
});
onUnmounted(() => {
  document.removeEventListener('keydown', onKey, true);
  window.removeEventListener('resize', onReflow);
  window.removeEventListener('scroll', onReflow, true);
});
</script>

<template>
  <Teleport to="body">
    <div v-if="open" ref="root" class="tour" role="dialog" aria-modal="true">
      <div class="tour-spot" :style="spot"></div>
      <div class="tour-bub" :style="bubble">
        <div class="tour-step mono muted">{{ i + 1 }} / {{ STEPS.length }}</div>
        <h3 class="tour-title">{{ step.title }}</h3>
        <p class="tour-text">{{ step.text }}</p>
        <div class="tour-nav">
          <button class="ghost small" @click="end">跳过</button>
          <span class="sp"></span>
          <button class="ghost small" :disabled="i === 0" @click="show(i - 1)">上一步</button>
          <button class="primary small tour-next" @click="show(i + 1)">{{ isLast ? '开始创作' : '下一步' }}</button>
        </div>
      </div>
    </div>
  </Teleport>
</template>

<style scoped>
.tour { position: fixed; inset: 0; z-index: 3500; }
.tour-spot {
  position: fixed;
  border-radius: 10px;
  box-shadow: 0 0 0 9999px rgba(8, 8, 10, 0.68);
  outline: 2px solid var(--amber);
  transition: all 0.22s var(--ease);
  pointer-events: none;
}
.tour-bub {
  position: fixed;
  width: min(350px, calc(100vw - 24px));
  background: var(--panel-glass);
  backdrop-filter: blur(14px);
  -webkit-backdrop-filter: blur(14px);
  color: var(--text);
  border: 1px solid var(--line2);
  border-radius: var(--r-lg);
  padding: 14px 16px 12px;
  box-shadow: var(--shadow-2);
  transition: top 0.22s var(--ease), left 0.22s var(--ease);
}
.tour-step { font-size: 11px; }
.tour-title { margin: 4px 0 6px; font-size: 15px; color: var(--text); letter-spacing: 0.02em; }
.tour-text { margin: 0 0 12px; font-size: 13px; line-height: 1.7; color: var(--muted); }
.tour-nav { display: flex; gap: 6px; align-items: center; }
.tour-nav .sp { flex: 1; }
</style>
