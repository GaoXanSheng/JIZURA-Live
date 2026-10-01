<script lang="ts" setup>
/* 风格 tab：风格网格（canvas 缩略图）、字体角色、自定义配色 */
import {nextTick, onMounted, onUnmounted, ref} from 'vue';
import {J} from '../../engine';
import {S} from '../../jizura/state';
import {bus} from '../../jizura/bus';
import {invalidateFontKey, loadThumbFonts, replan, replanSoon} from '../../jizura/engine';
import {commit, randomPalette, remember} from '../../jizura/look';
import {flushSave} from '../../jizura/project';
import {badgesOf, swatchHTML, toColorInput} from '../../jizura/ui-helpers';

const gridEl = ref<HTMLElement | null>(null);
const offs: (() => void)[] = [];

const styles = ref<{
  k: string;
  name: string;
  desc: string;
  badges: ReturnType<typeof badgesOf>;
  off: boolean;
  on: boolean
}[]>([]);
const fontRoles = ref<{ role: string; label: string; value: string; options: { v: string; t: string }[] }[]>([]);
const localFontName = ref('');
const colorOn = ref(false), accentOn = ref(false);
const baseColors = ref<{ k: string; label: string; value: string }[]>([]);
const accentColors = ref<{ k: string; label: string; value: string }[]>([]);
const swatch = ref<string[]>([]);

const BASE_KEYS: [string, string][] = [['bg', '背景'], ['fg', '文字'], ['sub', '辅助']];
const ACCENT_KEYS: [string, string][] = [['accent', '强调色'], ['ghostA', '错位色A'], ['ghostB', '错位色B']];

function rebuildStyles() {
  if (!S.project) return;
  styles.value = J.STYLE_ORDER.map((k: string) => {
    const st = J.STYLES[k];
    const off = !J.randomOk(S.project, 'style', k);
    const offNote = off
        ? (st.extra && S.project.extra !== true ? '（新增特效未开启，一键随机不会选中）'
            : st.set && !J.setOn(S.project, st.set) ? '（该套装未开启，一键随机不会选中）'
                : '（和风特效未开启，一键随机不会选中）') : '';
    return {k, name: st.name, desc: st.desc + offNote, badges: badgesOf(st), off, on: S.project.style === k};
  });
  paintGrid();
}

function paintGrid() {
  const g = gridEl.value;
  if (!g) return;
  g.querySelectorAll('canvas').forEach(cv => {
    const k = cv.dataset.k!, st = J.STYLES[k], sc = st.schemes[0], x = cv.getContext('2d')!;
    x.fillStyle = sc.bg;
    x.fillRect(0, 0, 192, 108);
    st.schemes.slice(1, 4).forEach((s2: any, i: number) => {
      x.fillStyle = s2.bg;
      x.fillRect(192 - 14 * (i + 1), 0, 14, 10);
    });
    const f = st.fonts.display[0];
    x.font = J.fontCSS(f, 46);
    x.textAlign = 'center';
    x.textBaseline = 'middle';
    x.fillStyle = sc.ghostB;
    x.fillText('字面', 96 - 3, 54 - 1);
    x.fillStyle = sc.ghostA;
    x.fillText('字面', 96 + 3, 54 + 2);
    x.fillStyle = sc.fg;
    x.fillText('字面', 96, 54);
    x.fillStyle = sc.accent;
    x.fillRect(12, 90, 30, 4);
    x.font = J.fontCSS('mono', 9);
    x.textAlign = 'left';
    x.fillStyle = sc.sub;
    x.fillText(k.toUpperCase(), 48, 93);
  });
}

function pickStyle(k: string) {
  remember();
  S.project.style = k;
  S.project.colors.enabled = false;
  bus.emit('sync-ui');
  replan();
  commit();
}

function rebuildFonts() {
  const mk = (role: string, label: string) => ({
    role, label, value: S.project.fonts[role] || '',
    options: [{v: '', t: '风格默认'}, ...Object.entries(J.FONTS).map(([k, f]: [string, any]) => {
      const g = J.faceOf ? J.faceOf(k) : f;
      const alt = g.label && g.label !== f.label ? ' → ' + g.label : '';
      return {v: k, t: f.label + alt};
    })],
  });
  fontRoles.value = [mk('display', '标题'), mk('serif', '明朝枠'), mk('body', '小文字')];
}

function onFontRole(role: string, ev: Event) {
  const v = (ev.target as HTMLSelectElement).value;
  if (v) S.project.fonts[role] = v; else delete S.project.fonts[role];
  invalidateFontKey();
  replan();
}

function addLocalFont() {
  const name = localFontName.value.trim();
  if (!name) return;
  const key = 'local_' + name.replace(/\s+/g, '_');
  const weight = /bold|太|black|heavy|w[6-9]|[6-9]00/i.test(name) ? 700 : 400;
  J.addUserFont(key, name + '（本机）', name, weight);
  S.project.userFonts = (S.project.userFonts || []).filter((u: any) => u.key !== key).concat([{
    key,
    label: name + '（本机）',
    family: name,
    weight
  }]);
  S.project.fonts.display = key;
  localFontName.value = '';
  invalidateFontKey();
  rebuildFonts();
  replan();
}

async function onFontFile(e: Event) {
  const f = (e.target as HTMLInputElement).files?.[0];
  if (!f) return;
  try {
    const uf = await J.loadFontFile(f);
    S.project.userFonts = (S.project.userFonts || []).filter((x: any) => x.key !== uf.key).concat([uf]);
    S.project.fonts.display = uf.key;
    invalidateFontKey();
    rebuildFonts();
    replan();
    flushSave();
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
  if (!(c as any)[flag]) {
    (c as any)[flag] = true;
  }
  rebuildColors();
  replanSoon(60);
}

function onColorToggle(flag: string, keys: [string, string][]) {
  remember();
  const c = S.project.colors;
  (c as any)[flag] = !(c as any)[flag];
  if ((c as any)[flag]) {
    const sc0 = J.STYLES[S.project.style].schemes[0];
    keys.forEach(([k]) => {
      if (!(c as any)[k]) (c as any)[k] = sc0[k];
    });
  }
  rebuildColors();
  replan();
  commit();
}

function drawSwatch() {
  const sc = S.plan ? S.plan.style.schemes[0] : null;
  swatch.value = sc ? swatchHTML([sc.accent, sc.ghostA, sc.ghostB]) : [];
}

function onSyncUi() {
  rebuildStyles();
  rebuildFonts();
  rebuildColors();
}

onMounted(async () => {
  offs.push(bus.on('sync-ui', onSyncUi));
  offs.push(bus.on('replan', () => {
    drawSwatch();
  }));
  offs.push(bus.on('fonts', () => {
    rebuildStyles();
  }));
  await nextTick();          // v-for 的 canvas 要等 DOM flush 后才存在，同步画会扑空
  onSyncUi();
  loadThumbFonts();
});
onUnmounted(() => offs.forEach(f => f()));
</script>

<template>
  <div class="px-4 py-3.5">
    <div ref="gridEl" class="grid grid-cols-3 gap-2">
      <button v-for="s in styles" :key="s.k"
              :aria-pressed="s.on"
              :class="s.on ? 'border-amber shadow-[0_0_0_1px_var(--color-amber)]' : 'border-line hover:border-line2'"
              :title="s.desc"
              class="relative flex flex-col gap-1 p-0 border rounded-md overflow-hidden text-left min-h-0 bg-transparent transition-colors"
              @click="pickStyle(s.k)">
        <canvas :data-k="s.k" class="w-full aspect-video block" height="108" width="192"></canvas>
        <span class="text-[11px] px-1.5 pb-1.5 truncate">{{ s.name }}</span>
        <span class="absolute top-1 left-1 flex gap-0.5">
          <i v-for="(b, i) in s.badges" :key="i" :class="b.cls" :title="b.title"
             class="badge-set !bg-black/55">{{ b.text }}</i>
        </span>
      </button>
    </div>
    <h3 class="sec-sub">字体角色</h3>
    <div>
      <div v-for="r in fontRoles" :key="r.role"
           class="grid grid-cols-[70px_minmax(0,1fr)] gap-2 items-center mb-1.5 text-xs">
        <span class="text-mut">{{ r.label }}</span>
        <select :aria-label="`${r.label}的字体`" :value="r.value" @change="onFontRole(r.role, $event)">
          <option v-for="o in r.options" :key="o.v" :value="o.v">{{ o.t }}</option>
        </select>
      </div>
    </div>
    <div class="row mt-2">
      <input v-model="localFontName" class="flex-1" placeholder="本机字体名（例：游明朝）" type="text">
      <button class="btn" @click="addLocalFont">添加</button>
    </div>
    <div class="row mt-2">
      <label class="btn-file">字体文件（.ttf/.otf）<input accept=".ttf,.otf,.woff,.woff2" type="file"
                                                        @change="onFontFile"></label>
    </div>
    <h3 class="sec-sub">强调色・错位色</h3>
    <label class="checkblock mb-2"><input v-model="accentOn" type="checkbox"
                                          @change="onColorToggle('accentOn', ACCENT_KEYS)"><span>用自定义颜色覆盖<small>应用到全部场景，亮度会随背景自动调整</small></span></label>
    <div class="grid grid-cols-3 gap-2">
      <label v-for="c in accentColors" :key="c.k" class="field">{{ c.label }}<input :value="c.value" type="color"
                                                                                    @input="onColor(c.k, 'accentOn', $event)"></label>
    </div>
    <div class="row mt-2.5">
      <button class="btn btn-accent" @click="randomPalette">随机配色</button>
      <span class="inline-flex gap-0.5 items-center">
        <i v-for="(c, i) in swatch" :key="i" :style="{ background: c }"
           :title="c" class="w-5 h-5 rounded border border-white/20 inline-block"></i>
      </span>
    </div>
    <h3 class="sec-sub">背景・文字色</h3>
    <label class="checkblock mb-2"><input v-model="colorOn" type="checkbox"
                                          @change="onColorToggle('enabled', BASE_KEYS)"><span>把主背景与文字换成自定义颜色</span></label>
    <div class="grid grid-cols-3 gap-2">
      <label v-for="c in baseColors" :key="c.k" class="field">{{ c.label }}<input :value="c.value" type="color"
                                                                                  @input="onColor(c.k, 'enabled', $event)"></label>
    </div>
  </div>
</template>
