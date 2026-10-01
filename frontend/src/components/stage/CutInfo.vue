<script setup lang="ts">
/* 当前切分信息 chips + 手法替换面板（原 cutInfo / cutPick 的 Vue 化）
   详细模式：chip 点击打开替换网格；简单/手机模式：静态展示 */
import {onMounted, onUnmounted, ref} from 'vue';
import {J} from '../../engine';
import {S} from '../../jizura/state';
import {bus} from '../../jizura/bus';
import {replan} from '../../jizura/engine';
import {
  CHIP_GROUPS,
  cutGroupVal,
  cutQuietSlot,
  cutTechSlot,
  groupName,
  lyricCutK,
  rerollCurrentCut,
  setCutTech
} from '../../jizura/look';
import {registerThumbs} from '../../jizura/preview';
import {badgesOf} from '../../jizura/ui-helpers';

const cut = ref<any>(null);
const items = ref<{ g: string; cls: string; label: string; value: string; forced: boolean; enabled: boolean }[]>([]);
const pick = ref<{ g: string; line: number; k: number; title: string } | null>(null);
const pickCards = ref<any[]>([]);
const pickGrid = ref<HTMLElement | null>(null);
const offs: (() => void)[] = [];

const layoutHue = (k: string) => (J.LAYOUT_ORDER.indexOf(k) * 37 + 30) % 360;

function refresh(c: any) {
  cut.value = c;
  if (!c) { items.value = []; closePick(); return; }
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
  if (!it.g || !it.enabled) return;
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
  refresh(J.cutAt(S.plan, S.t));
});
onUnmounted(() => offs.forEach(f => f()));
</script>

<template>
  <div class="flex gap-1.5 flex-wrap items-center min-h-[26px] flex-none">
    <template v-if="!cut">
      <span class="hint">此位置没有切分</span>
    </template>
    <template v-else>
      <button v-for="(it, i) in items" :key="i" type="button"
              class="chip" :class="[it.cls, { 'is-forced': it.forced, 'chip-clickable': it.g && it.enabled }]"
              :aria-pressed="pick && pick.g === it.g ? 'true' : 'false'"
              :disabled="!it.g || !it.enabled"
              @click="onChip(it)">
        <b v-if="it.label !== '#'">{{ it.label }}</b>{{ it.value }}
      </button>
      <button type="button" class="btn btn-ghost btn-sm" :disabled="!items.some(it => it.enabled)" title="只重抽这个切分的构成" @click="rerollCurrentCut('shuffle')">重抽</button>
      <button type="button" class="btn btn-accent btn-sm" :disabled="!items.some(it => it.enabled)" title="这个切分的手法全部随机" @click="rerollCurrentCut('omakase')">一键随机</button>
    </template>
  </div>
  <div v-if="pick" class="cut-pick border border-line rounded-md bg-panel p-2 max-h-[min(42vh,420px)] overflow-auto">
    <div class="flex items-center gap-2 mb-2 sticky top-0 bg-panel z-[1] pb-1">
      <strong class="text-xs">{{ pick.title }}</strong>
      <span class="text-mut flex-1 text-[11px]">只替换这个切分</span>
      <button type="button" class="btn btn-ghost btn-sm" @click="onPickAuto">自动</button>
      <button type="button" class="btn btn-ghost btn-sm" aria-label="关闭" @click="closePick">✕</button>
    </div>
    <div ref="pickGrid" class="grid gap-2 min-h-24 [grid-template-columns:repeat(auto-fill,minmax(112px,1fr))]">
      <button v-for="card in pickCards" :key="card.key" type="button" class="btn !p-0 flex-col !h-auto border-line rounded-md overflow-hidden bg-panel text-left"
              :class="{ '!border-amber shadow-[0_0_0_1px_var(--color-amber)]': card.on }" @click="onPickCard(card)">
        <canvas v-if="!card.isNone" :width="card.tw" :height="card.th" :data-g="pick.g" :data-k="card.key" class="w-full aspect-video block bg-ink rounded-none"></canvas>
        <span v-else class="grid place-items-center w-full aspect-video text-mut">无</span>
        <span v-if="!card.isNone" class="flex items-center gap-1 text-[11px] px-1.5 pb-1.5 min-w-0">
          <span class="truncate min-w-0">{{ card.name }}</span>
          <i v-for="(b, i) in card.badges" :key="i" class="badge-set" :class="b.cls" :title="b.title">{{ b.text }}</i>
        </span>
      </button>
    </div>
  </div>
</template>
