<script setup lang="ts">
/* 风格 tab：风格网格（canvas 缩略图）、字体角色、自定义配色 */
import { onMounted, onUnmounted, ref } from 'vue';
import { J } from '../../engine';
import { S } from '../../jizura/state';
import { bus } from '../../jizura/bus';
import { replan, replanSoon, invalidateFontKey, loadThumbFonts } from '../../jizura/engine';
import { remember, commit, toast, randomPalette } from '../../jizura/look';
import { flushSave } from '../../jizura/project';
import { badgesOf, toColorInput, swatchHTML } from '../../jizura/ui-helpers';

const gridEl = ref<HTMLElement | null>(null);
const offs: (() => void)[] = [];

const styles = ref<{ k: string; name: string; desc: string; badges: ReturnType<typeof badgesOf>; off: boolean; offNote: string; on: boolean }[]>([]);
const fontRoles = ref<{ role: string; label: string; value: string; options: { v: string; t: string }[] }[]>([]);
const localFontName = ref('');
const colorOn = ref(false), accentOn = ref(false);
const baseColors = ref<{ k: string; label: string; value: string }[]>([]);
const accentColors = ref<{ k: string; label: string; value: string }[]>([]);
const swatch = ref<string[]>([]);
const isPro = ref(true);

const BASE_KEYS: [string, string][] = [['bg', '背景'], ['fg', '文字'], ['sub', '辅助']];
const ACCENT_KEYS: [string, string][] = [['accent', '强调色'], ['ghostA', '错位色A'], ['ghostB', '错位色B']];

function rebuildStyles() {
  if (!S.project) return;
  styles.value = J.STYLE_ORDER.map((k: string) => {
    const st = J.STYLES[k];
    const off = !J.randomOk(S.project, 'style', k);
    const offNote = off
      ? (st.extra && S.project.extra !== true ? '（追加项未开启，一键随机不会选中）'
        : st.set && !J.setOn(S.project, st.set) ? '（该套装未开启，一键随机不会选中）'
        : '（和风演出未开启，一键随机不会选中）') : '';
    return { k, name: st.name, desc: st.desc + offNote, badges: badgesOf(st), off, on: S.project.style === k };
  });
  paintGrid();
}

function paintGrid() {
  const g = gridEl.value; if (!g) return;
  g.querySelectorAll('canvas').forEach(cv => {
    const k = cv.dataset.k!, st = J.STYLES[k], sc = st.schemes[0], x = cv.getContext('2d')!;
    x.fillStyle = sc.bg; x.fillRect(0, 0, 192, 108);
    st.schemes.slice(1, 4).forEach((s2: any, i: number) => { x.fillStyle = s2.bg; x.fillRect(192 - 14 * (i + 1), 0, 14, 10); });
    const f = st.fonts.display[0];
    x.font = J.fontCSS(f, 46); x.textAlign = 'center'; x.textBaseline = 'middle';
    x.fillStyle = sc.ghostB; x.fillText('字面', 96 - 3, 54 - 1);
    x.fillStyle = sc.ghostA; x.fillText('字面', 96 + 3, 54 + 2);
    x.fillStyle = sc.fg; x.fillText('字面', 96, 54);
    x.fillStyle = sc.accent; x.fillRect(12, 90, 30, 4);
    x.font = J.fontCSS('mono', 9); x.textAlign = 'left'; x.fillStyle = sc.sub; x.fillText(k.toUpperCase(), 48, 93);
  });
}

function pickStyle(k: string) {
  remember();
  S.project.style = k;
  S.project.colors.enabled = false;
  bus.emit('sync-ui');
  replan(); commit();
}

function rebuildFonts() {
  const mk = (role: string, label: string) => ({
    role, label, value: S.project.fonts[role] || '',
    options: [{ v: '', t: '风格默认' }, ...Object.entries(J.FONTS).map(([k, f]: [string, any]) => {
      const g = J.faceOf ? J.faceOf(k) : f;
      const alt = g.label && g.label !== f.label ? ' → ' + g.label : '';
      return { v: k, t: f.label + alt };
    })],
  });
  fontRoles.value = [mk('display', '标题'), mk('serif', '明朝枠'), mk('body', '小文字')];
}

function onFontRole(role: string, ev: Event) {
  const v = (ev.target as HTMLSelectElement).value;
  if (v) S.project.fonts[role] = v; else delete S.project.fonts[role];
  invalidateFontKey(); replan();
}

function addLocalFont() {
  const name = localFontName.value.trim(); if (!name) return;
  const key = 'local_' + name.replace(/\s+/g, '_');
  const weight = /bold|太|black|heavy|w[6-9]|[6-9]00/i.test(name) ? 700 : 400;
  J.addUserFont(key, name + '（本机）', name, weight);
  S.project.userFonts = (S.project.userFonts || []).filter((u: any) => u.key !== key).concat([{ key, label: name + '（本机）', family: name, weight }]);
  S.project.fonts.display = key;
  localFontName.value = '';
  invalidateFontKey(); rebuildFonts(); replan();
}

async function onFontFile(e: Event) {
  const f = (e.target as HTMLInputElement).files?.[0];
  if (!f) return;
  try {
    const uf = await J.loadFontFile(f);
    S.project.userFonts = (S.project.userFonts || []).filter((x: any) => x.key !== uf.key).concat([uf]);
    S.project.fonts.display = uf.key;
    invalidateFontKey(); rebuildFonts(); replan(); flushSave();
  } catch {
    bus.emit('msg', '无法读取该字体文件');
    setTimeout(() => bus.emit('msg', null), 2500);
  }
  (e.target as HTMLInputElement).value = '';
}

function rebuildColors() {
  if (!S.project) return;
  const st = J.STYLES[S.project.style] || J.STYLES.noir, sc = st.schemes[0];
  const c = S.project.colors;
  colorOn.value = !!c.enabled;
  accentOn.value = !!c.accentOn;
  const fill = (keys: [string, string][], flag: string) => keys.map(([k, label]) => ({
    k, label,
    value: toColorInput(((c as any)[flag] && (c as any)[k]) || (c as any)[k] || sc[k]),
  }));
  baseColors.value = fill(BASE_KEYS, 'enabled');
  accentColors.value = fill(ACCENT_KEYS, 'accentOn');
  drawSwatch();
}

function onColor(k: string, flag: string, ev: Event) {
  const c = S.project.colors;
  (c as any)[k] = (ev.target as HTMLInputElement).value.toUpperCase();
  if (!(c as any)[flag]) { (c as any)[flag] = true; }
  rebuildColors(); replanSoon(60);
}

function onColorToggle(flag: string, keys: [string, string][]) {
  remember();
  const c = S.project.colors;
  (c as any)[flag] = !(c as any)[flag];
  if ((c as any)[flag]) {
    const sc0 = J.STYLES[S.project.style].schemes[0];
    keys.forEach(([k]) => { if (!(c as any)[k]) (c as any)[k] = sc0[k]; });
  }
  rebuildColors(); replan(); commit();
}

function drawSwatch() {
  const sc = S.plan ? S.plan.style.schemes[0] : null;
  swatch.value = sc ? swatchHTML([sc.accent, sc.ghostA, sc.ghostB]) : [];
}

function onSyncUi() { rebuildStyles(); rebuildFonts(); rebuildColors(); isPro.value = S.mode !== 'easy' && S.mode !== 'mobile'; }

onMounted(() => {
  offs.push(bus.on('sync-ui', onSyncUi));
  offs.push(bus.on('replan', () => { drawSwatch(); }));
  offs.push(bus.on('fonts', () => { rebuildStyles(); }));
  onSyncUi();
  loadThumbFonts();
});
onUnmounted(() => offs.forEach(f => f()));
</script>

<template>
  <div class="tabpane" data-pane="style">
    <div ref="gridEl" class="style-grid">
      <button v-for="s in styles" :key="s.k" class="stile" :class="{ 'set-off': s.off }" :title="s.desc"
              :aria-pressed="s.on" @click="pickStyle(s.k)">
        <canvas width="192" height="108" :data-k="s.k"></canvas>
        <span>{{ s.name }}</span>
        <span class="badges">
          <i v-for="(b, i) in s.badges" :key="i" class="set-badge" :class="b.cls" :title="b.title">{{ b.text }}</i>
        </span>
      </button>
    </div>
    <h3>字体角色</h3>
    <div>
      <div v-for="r in fontRoles" :key="r.role" class="font-row">
        <span class="muted">{{ r.label }}</span>
        <select :value="r.value" :aria-label="`${r.label}的字体`" @change="onFontRole(r.role, $event)">
          <option v-for="o in r.options" :key="o.v" :value="o.v">{{ o.t }}</option>
        </select>
      </div>
    </div>
    <div class="row" style="margin-top: 8px">
      <input v-model="localFontName" type="text" placeholder="本机字体名（例：游明朝）" style="flex: 1">
      <button @click="addLocalFont">添加</button>
    </div>
    <div class="row" style="margin-top: 8px">
      <label class="file">字体文件（.ttf/.otf）<input type="file" accept=".ttf,.otf,.woff,.woff2" @change="onFontFile"></label>
    </div>
    <h3>强调色・错位色</h3>
    <label class="check"><input v-model="accentOn" type="checkbox" @change="onColorToggle('accentOn', ACCENT_KEYS)"><span>用自定义颜色覆盖<small>应用到全部场景，亮度会随背景自动调整</small></span></label>
    <div class="color-row">
      <label v-for="c in accentColors" :key="c.k">{{ c.label }}<input type="color" :value="c.value" @input="onColor(c.k, 'accentOn', $event)"></label>
    </div>
    <div class="row" style="margin-top: 10px">
      <button class="accent" @click="randomPalette">随机配色</button>
      <span class="swatches"><i v-for="(c, i) in swatch" :key="i" :style="{ background: c }" :title="c"></i></span>
    </div>
    <h3>背景・文字色</h3>
    <label class="check"><input v-model="colorOn" type="checkbox" @change="onColorToggle('enabled', BASE_KEYS)"><span>把主背景与文字换成自定义颜色</span></label>
    <div class="color-row">
      <label v-for="c in baseColors" :key="c.k">{{ c.label }}<input type="color" :value="c.value" @input="onColor(c.k, 'enabled', $event)"></label>
    </div>
  </div>
</template>

<style scoped>
.style-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 8px; }
.stile {
  position: relative;
  display: flex; flex-direction: column; gap: 5px; padding: 0;
  border: 1px solid var(--line); background: transparent;
  border-radius: var(--r-sm); overflow: hidden; text-align: left; min-height: 0;
}
.stile canvas { width: 100%; aspect-ratio: 16 / 9; display: block; }
.stile span { font-size: 11px; padding: 0 7px 6px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.stile[aria-pressed='true'] { border-color: var(--amber); box-shadow: 0 0 0 1px var(--amber); color: var(--text); }
.stile.set-off { opacity: 0.55; }
.stile .badges { position: absolute; top: 4px; left: 4px; padding: 0; display: flex; gap: 3px; }
.font-row { display: grid; grid-template-columns: 70px minmax(0, 1fr); gap: 8px; align-items: center; margin-bottom: 6px; font-size: 12px; }
.color-row { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 8px; }
.color-row label { display: flex; flex-direction: column; gap: 3px; font-size: 11px; color: var(--muted); }
.color-row input[type='color'] { width: 100%; height: 28px; padding: 0; border: 1px solid var(--line); background: transparent; border-radius: var(--r-sm); }
.swatches { display: inline-flex; gap: 3px; vertical-align: middle; }
.swatches i { width: 22px; height: 22px; border-radius: 3px; border: 1px solid rgba(255, 255, 255, 0.18); display: inline-block; }
.set-badge { display: inline-block; margin: 0; padding: 0 4px; border: 1px solid var(--amber); border-radius: 3px; color: var(--amber); font-size: 10px; line-height: 15px; background: rgba(0, 0, 0, 0.55); font-style: normal; }
.set-badge.ex { border-color: var(--cyan); color: var(--cyan); }
.set-badge.set-horror { border-color: #c0544a; color: #e0786e; }
.set-badge.set-typo { border-color: #9aa3ad; color: #c5ccd3; }
.set-badge.set-kinetic { border-color: #7fbf6a; color: #9fd98a; }
label.check { display: flex; align-items: flex-start; gap: 8px; margin-bottom: 8px; cursor: pointer; }
label.check input { margin: 3px 0 0; flex: none; }
label.check small { display: block; font-size: 11px; color: var(--muted); }
</style>
