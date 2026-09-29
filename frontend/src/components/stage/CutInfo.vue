<script setup lang="ts">
/* 当前切分信息 chips + 手法替换面板（原 cutInfo / cutPick 的 Vue 化）
   详细模式：chip 点击打开替换网格；简单/手机模式：静态展示 */
import { onMounted, onUnmounted, ref } from 'vue';
import { J } from '../../engine';
import { S } from '../../jizura/state';
import { bus } from '../../jizura/bus';
import { replan, seek } from '../../jizura/engine';
import { CHIP_GROUPS, groupName, cutGroupVal, cutTechSlot, cutQuietSlot, setCutTech, lyricCutK, rerollCurrentCut } from '../../jizura/look';
import { registerThumbs } from '../../jizura/preview';
import { badgesOf } from '../../jizura/ui-helpers';

const cut = ref<any>(null);
const items = ref<{ g: string; cls: string; label: string; value: string; forced: boolean; enabled: boolean }[]>([]);
const isPro = ref(true);
const pick = ref<{ g: string; line: number; k: number; title: string } | null>(null);
const pickCards = ref<any[]>([]);
const pickGrid = ref<HTMLElement | null>(null);
const offs: (() => void)[] = [];

const layoutHue = (k: string) => (J.LAYOUT_ORDER.indexOf(k) * 37 + 30) % 360;

function refresh(c: any) {
  cut.value = c;
  if (!c) { items.value = []; closePick(); return; }
  if (S.mode !== 'pro') {
    closePick();
    const n = (tbl: any, k: string) => (tbl[k] ? tbl[k].name : k);
    items.value = [
      { g: '', cls: 'mono', label: '#', value: String(c.index + 1).padStart(2, '0'), forced: false, enabled: true },
      { g: 'layout', cls: 'l', label: '布局', value: n(J.LAYOUTS, c.layout), forced: false, enabled: true },
      { g: 'enter', cls: 'e', label: '入场', value: n(J.ENTER, c.enter), forced: false, enabled: true },
      { g: 'hold', cls: 'h', label: '保持', value: n(J.HOLD, c.hold), forced: false, enabled: true },
      { g: 'exit', cls: 'x', label: '退场', value: n(J.EXIT, c.exit), forced: false, enabled: true },
      ...(c.decor && c.decor.length ? [{ g: '', cls: '', label: '装饰', value: c.decor.map((d: any) => n(J.DECOR, d.id)).join('・'), forced: false, enabled: true }] : []),
      ...(c.treat && c.treat !== 'none' ? [{ g: '', cls: 't', label: '加工', value: n(J.TREAT, c.treat), forced: false, enabled: true }] : []),
      ...(c.bg && c.bg !== 'none' ? [{ g: '', cls: 'b', label: '背景', value: n(J.BG, c.bg), forced: false, enabled: true }] : []),
      ...(c.cam && c.cam !== 'push' ? [{ g: '', cls: 'c', label: '镜头', value: n(J.CAMERA, c.cam), forced: false, enabled: true }] : []),
      ...(c.trans ? [{ g: '', cls: 'c', label: '衔接', value: n(J.TRANS, c.trans), forced: false, enabled: true }] : []),
    ];
    return;
  }
  const k = lyricCutK(c);
  const slot = k >= 0 ? cutTechSlot(c.line, k) : {};
  const quiet = k >= 0 ? cutQuietSlot(c.line, k) : {};
  items.value = [
    { g: '', cls: 'mono', label: '#', value: String(c.index + 1).padStart(2, '0'), forced: false, enabled: true },
    ...CHIP_GROUPS.map(([g, cls, label]) => {
      const v = cutGroupVal(c, g);
      const forced = slot[g] != null && slot[g] !== '' && !quiet[g];
      return { g, cls, label, value: groupName(g, v), forced, enabled: k >= 0 };
    }),
  ];
  // 保留已打开的替换面板跟随当前切分
  if (pick.value && k >= 0) { pick.value.line = c.line; pick.value.k = k; fillPick(); }
  else if (pick.value && k < 0) closePick();
}

function onChip(it: { g: string; enabled: boolean }) {
  if (!it.g || !it.enabled || S.mode !== 'pro') return;
  const c = cut.value;
  const k = lyricCutK(c);
  if (k < 0) return;
  if (pick.value && pick.value.g === it.g && pick.value.line === c.line && pick.value.k === k) { closePick(); return; }
  pick.value = { g: it.g, line: c.line, k, title: '' };
  fillPick();
}

function closePick() { pick.value = null; pickCards.value = []; }

function fillPick() {
  const p = pick.value; if (!p) return;
  const meta = CHIP_GROUPS.find(x => x[0] === p.g);
  const c = cut.value;
  p.title = (meta ? meta[2] : p.g) + (c ? ' · #' + String(c.index + 1).padStart(2, '0') : '');
  const cur = c ? cutGroupVal(c, p.g) : '';
  const forced = cutTechSlot(p.line, p.k)[p.g];
  const onKey = forced || cur;
  const cards: any[] = [];
  if (p.g === 'decor' || p.g === 'trans') {
    cards.push({ key: 'none', name: '无', badges: [], isNone: true, on: forced === 'none', tw: 80, th: 80 });
  }
  const [W, H] = J.designSize(S.project.aspect || '16:9');
  const th = 80, tw = Math.max(72, Math.round(th * W / H));
  const itemsList = J.order(p.g).filter((key: string) => J.registry(p.g)[key] && !J.registry(p.g)[key].special);
  itemsList.forEach((key: string) => {
    const def = J.registry(p.g)[key];
    cards.push({ key, name: def.name, badges: badgesOf(def), isNone: false, on: key === onKey, tw, th });
  });
  pickCards.value = cards;
  requestAnimationFrame(() => { if (pickGrid.value) registerThumbs(pickGrid.value); });
}

function onPickCard(card: any) {
  const p = pick.value; if (!p) return;
  setCutTech(p.line, p.k, p.g, card.key);
  replan();
  fillPick();
}

function onPickAuto() {
  const p = pick.value; if (!p) return;
  setCutTech(p.line, p.k, p.g, '');
  replan();
}

onMounted(() => {
  offs.push(bus.on('cutchange', refresh));
  offs.push(bus.on('replan', () => refresh(J.cutAt(S.plan, S.t))));
  offs.push(bus.on('close-cutpick', closePick));
  offs.push(bus.on('mode', () => { isPro.value = S.mode === 'pro'; closePick(); refresh(cut.value); }));
  isPro.value = S.mode === 'pro';
  refresh(J.cutAt(S.plan, S.t));
});
onUnmounted(() => offs.forEach(f => f()));
</script>

<template>
  <div class="cut-info">
    <template v-if="!cut">
      <span class="hint">此位置没有切分</span>
    </template>
    <template v-else>
      <button v-for="(it, i) in items" :key="i" type="button"
              class="chip" :class="[it.cls, { 'is-forced': it.forced, clickable: it.g && it.enabled && isPro }]"
              :aria-pressed="pick && pick.g === it.g ? 'true' : 'false'"
              :disabled="!it.g || !it.enabled"
              @click="onChip(it)">
        <b v-if="it.label !== '#'">{{ it.label }}</b>{{ it.value }}
      </button>
      <button v-if="isPro" type="button" class="ghost small" :disabled="!items.some(it => it.enabled)" title="只重抽这个切分的构成" @click="rerollCurrentCut('shuffle')">重抽</button>
      <button v-if="isPro" type="button" class="ghost small accent" :disabled="!items.some(it => it.enabled)" title="这个切分的手法全部随机" @click="rerollCurrentCut('omakase')">一键随机</button>
    </template>
  </div>
  <div v-if="pick" class="cut-pick">
    <div class="cut-pick-h">
      <strong>{{ pick.title }}</strong>
      <span class="muted">只替换这个切分</span>
      <button type="button" class="ghost small" @click="onPickAuto">自动</button>
      <button type="button" class="ghost small" aria-label="关闭" @click="closePick">✕</button>
    </div>
    <div ref="pickGrid" class="tech-grid">
      <button v-for="card in pickCards" :key="card.key" type="button" class="tcard" :class="{ 'is-on': card.on }" @click="onPickCard(card)">
        <canvas v-if="!card.isNone" :width="card.tw" :height="card.th" :data-g="pick.g" :data-k="card.key"></canvas>
        <span v-else class="none-slot">无</span>
        <span v-if="!card.isNone" class="tcard-name"><span>{{ card.name }}</span><i v-for="(b, i) in card.badges" :key="i" class="set-badge" :class="b.cls" :title="b.title">{{ b.text }}</i></span>
      </button>
    </div>
  </div>
</template>

<style scoped>
.cut-info { display: flex; gap: 6px; flex-wrap: wrap; align-items: center; min-height: 26px; flex: none; }
.chip.clickable { cursor: pointer; }
.chip.clickable:hover { border-color: var(--amber); }
.chip.is-forced { border-color: var(--amber); }
.chip.l { border-color: rgba(245, 165, 12, 0.6); }
.chip.e { border-color: rgba(22, 244, 212, 0.55); }
.chip.x { border-color: rgba(255, 111, 156, 0.6); }
.chip.t { border-color: rgba(180, 140, 255, 0.6); }
.chip.b { border-color: rgba(120, 200, 120, 0.55); }
.chip.c { border-color: rgba(120, 170, 255, 0.6); }
.chip.h { border-color: #55525c; }
.cut-pick { border: 1px solid var(--line); border-radius: var(--r-sm); background: var(--panel); padding: 8px; max-height: min(42vh, 420px); overflow: auto; }
.cut-pick-h { display: flex; align-items: center; gap: 8px; margin-bottom: 8px; position: sticky; top: 0; background: var(--panel); z-index: 1; padding-bottom: 4px; }
.cut-pick-h strong { font-size: 12px; }
.cut-pick-h .muted { flex: 1; font-size: 11px; }
.tech-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(112px, 1fr)); gap: 8px; padding: 0; min-height: 96px; }
.tcard { cursor: pointer; width: 100%; text-align: left; flex-direction: column; }
.tcard.is-on { border-color: var(--amber); box-shadow: 0 0 0 1px var(--amber); }
.tcard canvas { width: 100%; aspect-ratio: 16 / 9; display: block; background: #0c0c0e; border-radius: 0; }
.none-slot { display: grid; place-items: center; width: 100%; aspect-ratio: 16 / 9; color: var(--muted); }
.tcard-name { display: flex; align-items: center; gap: 5px; font-size: 11px; padding: 0 6px 6px; min-width: 0; }
.tcard-name span { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; min-width: 0; }
.set-badge { display: inline-block; padding: 0 4px; border: 1px solid var(--amber); border-radius: 3px; color: var(--amber); font-size: 10px; line-height: 15px; font-style: normal; }
.set-badge.ex { border-color: var(--cyan); color: var(--cyan); }
.set-badge.set-horror { border-color: #c0544a; color: #e0786e; }
.set-badge.set-typo { border-color: #9aa3ad; color: #c5ccd3; }
.set-badge.set-kinetic { border-color: #7fbf6a; color: #9fd98a; }
</style>
