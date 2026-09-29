<script setup lang="ts">
/* 手法 tab：分组折叠列表 + 实时预览卡 + 过滤 + 全开/全关/反转 + 组锁 */
import { onMounted, onUnmounted, ref } from 'vue';
import { J } from '../../engine';
import { S } from '../../jizura/state';
import { bus } from '../../jizura/bus';
import { replan, replanSoon } from '../../jizura/engine';
import { remember, toggleTechLock, locksOf } from '../../jizura/look';
import { badgesOf } from '../../jizura/ui-helpers';
import { registerThumbs, resetWatch } from '../../jizura/preview';

const GROUPS: [string, string][] = [['layout', '布局'], ['enter', '入场'], ['hold', '保持'], ['exit', '退场'], ['decor', '装饰'], ['treat', '文字的加工'], ['bg', '背景'], ['cam', '镜头'], ['fx', '画面效果'], ['trans', '切分间的衔接']];
const TECH_LOCK_ON = '锁定：一键随机不改这一组的开/关';

const filter = ref('');
const groups = ref<any[]>([]);
const totalText = ref('');
const openSet = new Set<string>();
const listEls = new Map<string, HTMLElement>();
const offs: (() => void)[] = [];

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
    const [tw, th] = (() => {
      const [W, H] = J.designSize(S.project.aspect || '16:9');
      const h = 90; return [Math.max(80, Math.round(h * W / H)), h];
    })();
    out.push({
      g, label,
      open: !!q || openSet.has(g),
      locked: !!locksOf().tech[g],
      onN, items,
      cards: shown.map((k: string) => {
        const def = tbl[k];
        let offNote = '';
        if (!J.randomOk(S.project, g, k)) {
          offNote = def.extra && S.project.extra !== true ? '（追加项未开启，自动不会选中）'
            : def.set && !J.setOn(S.project, def.set) ? '（该套装未开启，自动不会选中）'
            : '（和风演出未开启，自动不会选中）';
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
  const tbl = J.registry(g), items = techItems(g);
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
  offs.push(bus.on('replan', () => { /* enabled 变了重新画状态 */ }));
  offs.push(bus.on('tech-rebuilt', () => {
    requestAnimationFrame(() => groups.value.forEach(g => { const el = listEls.get(g.g); if (el && g.open) registerThumbs(el); }));
  }));
  rebuild();
});
onUnmounted(() => { offs.forEach(f => f()); resetWatch(); });
</script>

<template>
  <div class="tabpane" data-pane="tech">
    <div class="row" style="margin-bottom: 10px">
      <input v-model="filter" type="text" placeholder="按名称筛选手法" style="flex: 1" aria-label="按名称筛选手法" @input="rebuild()">
      <span class="mono muted">{{ totalText }}</span>
    </div>
    <p class="note">画面中可见的预览会自动循环播放。</p>
    <details v-for="g in groups" :key="g.g" class="tgroup" :open="g.open" @toggle="onToggle(g.g, $event)">
      <summary>
        <span class="tg-name">{{ g.label }}</span>
        <span class="tg-cnt mono">{{ g.onN }}/{{ g.items.length }}</span>
        <button type="button" class="icon ghost lk" :aria-pressed="g.locked" :title="g.locked ? '锁定中，点击解除' : TECH_LOCK_ON" @click.prevent.stop="onLock(g.g)">
          <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4"><rect x="3" y="7" width="10" height="7" rx="1.5"/><path d="M5 7V5a3 3 0 0 1 6 0v2"/></svg>
        </button>
      </summary>
      <div class="tg-tools">
        <button class="ghost small" @click="onAll(g.g, 'on')">全部开启</button>
        <button class="ghost small" @click="onAll(g.g, 'off')">全部关闭</button>
        <button class="ghost small" @click="onAll(g.g, 'flip')">反转</button>
      </div>
      <div :ref="(el: any) => { if (el) listEls.set(g.g, el); }" class="checks tech-grid">
        <label v-for="c in g.cards" :key="c.k" class="tcard" :class="{ 'set-off': c.off }" :title="c.title">
          <canvas :width="c.tw" :height="c.th" :data-g="g.g" :data-k="c.k"></canvas>
          <span class="tcard-name">
            <input type="checkbox" :checked="c.on" @change="onCheck(g.g, c.k, $event)">
            <span>{{ c.name }}</span>
            <i v-for="(b, i) in c.badges" :key="i" class="set-badge" :class="b.cls" :title="b.title">{{ b.text }}</i>
          </span>
        </label>
      </div>
    </details>
  </div>
</template>

<style scoped>
.tgroup { border: 1px solid var(--line); border-radius: var(--r-sm); margin-bottom: 8px; background: var(--raised); }
.tgroup summary { display: flex; justify-content: space-between; align-items: center; padding: 8px 10px; cursor: pointer; list-style: none; font-size: 12px; gap: 6px; }
.tgroup summary::-webkit-details-marker { display: none; }
.tgroup summary::before { content: '▸'; color: var(--muted); margin-right: 8px; transition: transform 0.15s; }
.tgroup[open] summary::before { transform: rotate(90deg); }
.tg-name { flex: 1; font-weight: 600; }
.tg-cnt { color: var(--muted); font-size: 11px; }
.tg-tools { display: flex; gap: 6px; padding: 0 10px 8px; }
.tgroup .lk[aria-pressed='true'] { color: var(--amber); }
.checks.tech-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(128px, 1fr)); gap: 8px; padding: 0 10px 10px; }
.tcard { display: flex; flex-direction: column; gap: 4px; padding: 0; border: 1px solid var(--line); border-radius: var(--r-sm); overflow: hidden; background: var(--panel); cursor: pointer; }
.tcard:hover { border-color: var(--line2); }
.tcard canvas { width: 100%; aspect-ratio: 16 / 9; display: block; background: #0c0c0e; }
.tcard .tcard-name { display: flex; align-items: center; gap: 5px; font-size: 11px; padding: 0 6px 6px; min-width: 0; }
.tcard .tcard-name span { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; min-width: 0; }
.tcard.set-off { opacity: 0.5; }
.set-badge { display: inline-block; margin-left: 2px; padding: 0 4px; border: 1px solid var(--amber); border-radius: 3px; color: var(--amber); font-size: 10px; line-height: 15px; font-style: normal; }
.set-badge.ex { border-color: var(--cyan); color: var(--cyan); }
.set-badge.set-horror { border-color: #c0544a; color: #e0786e; }
.set-badge.set-typo { border-color: #9aa3ad; color: #c5ccd3; }
.set-badge.set-kinetic { border-color: #7fbf6a; color: #9fd98a; }
.note { font-size: 12px; color: var(--muted); }
</style>
