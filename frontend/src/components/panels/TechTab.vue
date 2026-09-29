<script setup lang="ts">
/* 手法 tab：套装开关（新增/和/文字排版/动态文字/恐怖）+ 分组折叠 + 实时预览卡 + 筛选/组锁 */
import { onMounted, onUnmounted, ref } from 'vue';
import { J } from '../../engine';
import { S } from '../../jizura/state';
import { bus } from '../../jizura/bus';
import { replan, replanSoon } from '../../jizura/engine';
import { remember, commit, toggleTechLock, locksOf, toast } from '../../jizura/look';
import { badgesOf } from '../../jizura/ui-helpers';
import { registerThumbs, resetWatch } from '../../jizura/preview';
import { flushSave } from '../../jizura/project';

const GROUPS: [string, string][] = [
  ['layout', '布局'], ['enter', '入场'], ['hold', '保持'], ['exit', '退场'], ['decor', '装饰'],
  ['treat', '文字处理'], ['bg', '背景'], ['cam', '运镜'], ['fx', '画面效果'], ['trans', '镜头转场'],
];
const TECH_LOCK_ON = '锁定：一键生成不改变这一组的开/关';

const SETS = [
  { cls: 'extra', label: '使用新增特效', sub: '带“新增”标记的手法、风格和字体。关闭时，一键生成和随机编排只使用首个公开版的特效' },
  { cls: 'wa', label: '使用和风特效', sub: '带“和”标记的手法和风格。关闭后，一键生成和随机编排不会选用（在新增内容的判断之后应用；逐行手动指定时仍可使用）' },
  { cls: 'typo', label: '使用文字排版部件', sub: '标有“文”的手法。关闭后，一键生成与随机重排不会选用' },
  { cls: 'kinetic', label: '使用动态文字部件', sub: '标有“动”的手法。关闭后，一键生成与随机重排不会选用' },
  { cls: 'horror', label: '也使用恐怖特效', sub: '标有“恐”的手法与风格。一键生成只在“恐怖”氛围时使用' },
] as const;

const filter = ref('');
const groups = ref<any[]>([]);
const totalText = ref('');
const openSet = new Set<string>();
const listEls = new Map<string, HTMLElement>();
const offs: (() => void)[] = [];

const setVal = (cls: string) =>
  cls === 'extra' ? S.project?.extra === true
  : cls === 'wa' ? S.project?.wa !== false
  : J.setOn(S.project, cls);

function onSet(cls: string, ev: Event) {
  const on = (ev.target as HTMLInputElement).checked;
  remember();
  S.project[cls] = on;
  bus.emit('sync-ui');
  rebuild();
  replan(); commit(); flushSave();
}

function techItems(g: string) { return J.order(g).filter((k: string) => J.registry(g)[k] && !J.registry(g)[k].special); }

function rebuild() {
  if (!S.project) return;
  resetWatch();
  const q = filter.value.trim().toLowerCase();
  let total = 0, onAll = 0;
  const out: any[] = [];
  GROUPS.forEach(([g, label]) => {
    const tbl = J.registry(g), items = techItems(g);
    const en = S.project.enabled[g] || (S.project.enabled[g] = {});
    const shown = q ? items.filter((k: string) => (tbl[k].name + ' ' + k).toLowerCase().includes(q)) : items;
    const onN = items.filter((k: string) => en[k] !== false).length;
    total += items.length; onAll += onN;
    if (q && !shown.length) return;
    const [W, H] = J.designSize(S.project.aspect || '16:9');
    const h = 90; const tw = Math.max(80, Math.round(h * W / H)), th = h;
    out.push({
      g, label,
      open: !!q || openSet.has(g),
      locked: !!locksOf().tech[g],
      onN, items,
      cards: shown.map((k: string) => {
        const def = tbl[k];
        let offNote = '';
        if (!J.randomOk(S.project, g, k)) {
          offNote = def.extra && S.project.extra !== true ? '（新增特效未开启，自动不会选中）'
            : def.set && !J.setOn(S.project, def.set) ? '（该套装未开启，自动不会选中）'
            : '（和风特效未开启，自动不会选中）';
        }
        return {
          k, name: def.name, badges: badgesOf(def), on: en[k] !== false,
          off: !J.randomOk(S.project, g, k),
          title: k + (def.tags && def.tags.length ? '（' + def.tags.map((t: string) => (J.MOODS[t] ? J.MOODS[t].name : t)).join('・') + '）' : '') + offNote,
          tw, th,
        };
      }),
    });
  });
  groups.value = out;
  totalText.value = `${onAll}/${total}`;
  bus.emit('tech-rebuilt');
}

function onToggle(g: string, ev: Event) {
  const open = (ev.target as HTMLDetailsElement).open;
  if (open) { openSet.add(g); requestAnimationFrame(() => { const el = listEls.get(g); if (el) registerThumbs(el); }); }
  else openSet.delete(g);
}

function onCheck(g: string, k: string, ev: Event) {
  const en = S.project.enabled[g] || (S.project.enabled[g] = {});
  en[k] = (ev.target as HTMLInputElement).checked;
  S.project.mood = null;
  rebuild();
  replanSoon(60);
}

function onAll(g: string, a: 'on' | 'off' | 'flip') {
  remember();
  const items = techItems(g);
  const en = S.project.enabled[g] || (S.project.enabled[g] = {});
  items.forEach((k: string) => { en[k] = a === 'on' ? true : a === 'off' ? false : en[k] === false; });
  if (g === 'layout' && !items.some((k: string) => en[k] !== false)) en.center = true;
  if (g === 'enter') en.cut = true;
  if (g === 'exit') en.cut = true;
  if (g === 'hold') en.still = true;
  if (g === 'treat') en.none = true;
  if (g === 'bg') en.none = true;
  if (g === 'cam') en.push = true;
  S.project.mood = null;
  openSet.add(g);
  rebuild(); replan();
}

function onLock(g: string) { toggleTechLock(g); rebuild(); }

onMounted(() => {
  offs.push(bus.on('sync-ui', rebuild));
  offs.push(bus.on('tech-rebuilt', () => {
    requestAnimationFrame(() => groups.value.forEach(g => { const el = listEls.get(g.g); if (el && g.open) registerThumbs(el); }));
  }));
  rebuild();
});
onUnmounted(() => { offs.forEach(f => f()); resetWatch(); });
</script>

<template>
  <div class="px-4 py-3.5">
    <div class="flex flex-col gap-1 mb-3">
      <label v-for="t in SETS" :key="t.cls" class="checkblock">
        <input type="checkbox" :checked="setVal(t.cls)" @change="onSet(t.cls, $event)">
        <span>{{ t.label }}<small>{{ t.sub }}</small></span>
      </label>
    </div>
    <div class="row mb-2.5">
      <input v-model="filter" type="text" placeholder="按名称筛选手法" class="flex-1" aria-label="按名称筛选手法" @input="rebuild()">
      <span class="font-mono text-xs text-mut">{{ totalText }}</span>
    </div>
    <p class="hint mb-2">画面中可见的预览会自动循环播放。</p>
    <details v-for="g in groups" :key="g.g" class="border border-line rounded-md bg-raised mb-2" :open="g.open" @toggle="onToggle(g.g, $event)">
      <summary class="flex items-center gap-1.5 px-2.5 py-2 cursor-pointer text-xs list-none [&::-webkit-details-marker]:hidden before:content-['▸'] before:text-mut before:mr-2 before:transition-transform open:before:rotate-90">
        <span class="flex-1 font-semibold">{{ g.label }}</span>
        <span class="font-mono text-[11px] text-mut">{{ g.onN }}/{{ g.items.length }}</span>
        <button type="button" class="btn btn-icon btn-ghost" :class="g.locked ? '!text-amber' : ''" :aria-pressed="g.locked" :title="g.locked ? '锁定中，点击解除' : TECH_LOCK_ON" @click.prevent.stop="onLock(g.g)">
          <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4"><rect x="3" y="7" width="10" height="7" rx="1.5"/><path d="M5 7V5a3 3 0 0 1 6 0v2"/></svg>
        </button>
      </summary>
      <div class="flex gap-1.5 px-2.5 pb-2">
        <button class="btn btn-ghost btn-sm" @click="onAll(g.g, 'on')">全部开启</button>
        <button class="btn btn-ghost btn-sm" @click="onAll(g.g, 'off')">全部关闭</button>
        <button class="btn btn-ghost btn-sm" @click="onAll(g.g, 'flip')">反转</button>
      </div>
      <div :ref="(el: any) => { if (el) listEls.set(g.g, el); }" class="grid gap-2 px-2.5 pb-2.5 [grid-template-columns:repeat(auto-fill,minmax(128px,1fr))]">
        <label v-for="c in g.cards" :key="c.k" class="flex flex-col gap-1 p-0 border border-line rounded-md overflow-hidden bg-panel cursor-pointer" :class="{ 'opacity-50': c.off }" :title="c.title">
          <canvas :width="c.tw" :height="c.th" :data-g="g.g" :data-k="c.k" class="w-full aspect-video block bg-ink"></canvas>
          <span class="flex items-center gap-1 text-[11px] px-1.5 pb-1.5 min-w-0">
            <input type="checkbox" :checked="c.on" @change="onCheck(g.g, c.k, $event)">
            <span class="truncate min-w-0">{{ c.name }}</span>
            <i v-for="(b, i) in c.badges" :key="i" class="badge-set" :class="b.cls" :title="b.title">{{ b.text }}</i>
          </span>
        </label>
      </div>
    </details>
  </div>
</template>
