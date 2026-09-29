<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue';
import { J } from '../../engine';
import { S } from '../../jizura/state';
import { bus } from '../../jizura/bus';
import { replan, replanSoon } from '../../jizura/engine';
import { remember, commit, toast, clearLyrics } from '../../jizura/look';
import { flushSave } from '../../jizura/project';
import { lyricOptions, setLyricOptions, applyLyricOptions, versions, versionLabel, switchVersion, currentLyric } from '../../cloud/lyricStore';
import { useCloud } from '../../stores/cloud';

const lyrics = ref('');
const lang = ref('auto');
const langNote = ref('');
const syntaxOpen = ref(false);
const ta = ref<HTMLTextAreaElement | null>(null);
const offs: (() => void)[] = [];

const cloud = useCloud();
const optRoma = ref(lyricOptions().roma);
const optTrans = ref(lyricOptions().trans);
const verIdx = ref('');
const verBox = ref(false);
const verList = ref<{ i: number; label: string }[]>([]);

onMounted(() => {
  offs.push(bus.on('sync-ui', sync));
  offs.push(bus.on('lyrics-sync', sync));
  offs.push(bus.on('focus-lyrics', () => ta.value?.focus()));
  offs.push(bus.on('replan', noteLang));
  offs.push(bus.on('lyric-versions', refreshVersions));
  offs.push(bus.on('booted', refreshVersions));
  sync();
  refreshVersions();
});
onUnmounted(() => offs.forEach(f => f()));

function refreshVersions() {
  const cands = versions();
  verBox.value = cands.length >= 2;
  verList.value = cands.map((c, i) => ({ i, label: versionLabel(c) }));
  verIdx.value = '';
}

function sync() {
  if (document.activeElement !== ta.value) lyrics.value = S.project?.lyrics || '';
  lang.value = J.LANG_LABEL[S.project?.lang] ? S.project.lang : 'auto';
  noteLang();
}

function noteLang() {
  langNote.value = (S.project?.lang || 'auto') === 'auto' && S.plan ? '→ ' + J.LANG_LABEL[J.resolveLang(S.project)] : '';
}

function onInput() {
  S.project.lyrics = lyrics.value;
  replanSoon(260);
}

function onLang() {
  remember();
  S.project.lang = lang.value;
  replan(); commit(); flushSave();
  const l = J.resolveLang(S.project);
  toast((S.project.lang === 'auto' ? '歌词语言：自动判定 → ' : '歌词语言：') + J.LANG_LABEL[l]);
  noteLang();
}

function onLyOpt(kind: 'roma' | 'trans') {
  setLyricOptions(kind === 'roma' ? { roma: optRoma.value } : { trans: optTrans.value });
  applyLyricOptions();
}

function onVersion() {
  const idx = +verIdx.value;
  if (!Number.isNaN(idx) && verIdx.value !== '') switchVersion(cloud.source, idx);
}

const hasLyricSource = () => !!currentLyric();
</script>

<template>
  <div class="sec">
    <div class="sec-h">
      <h2>歌词</h2>
      <span class="sec-btns">
        <button class="ghost small" title="清空歌词与逐行时间、指定、导出范围（可用 Ctrl+Z 撤销）" @click="clearLyrics()">清空歌词</button>
        <button class="ghost small" :aria-expanded="syntaxOpen" @click="syntaxOpen = !syntaxOpen">记法</button>
      </span>
    </div>
    <textarea ref="ta" v-model="lyrics" rows="9" spellcheck="false" aria-label="歌词（1行 = 1短语）" @input="onInput"></textarea>
    <label class="lang-row">歌词语言
      <select v-model="lang" @change="onLang">
        <option value="auto">自动判定</option>
        <option value="ja">日本語</option>
        <option value="zh-Hant">繁體中文</option>
        <option value="zh-Hans">简体中文</option>
        <option value="ko">한국어</option>
        <option value="en">English</option>
      </select>
      <span class="muted">{{ langNote }}</span>
    </label>
    <div class="lyopts">
      <label title="把歌词音译（罗马音/拼音）作为小字注音渲染。勾选变化会对刚载入的酷狗 KRC 歌词（含拖入的 .krc）立即重新生成"><input v-model="optRoma" type="checkbox" @change="onLyOpt('roma')">音译</label>
      <label title="把歌词翻译作为小字注音渲染；与音译同时勾选时两者并列显示"><input v-model="optTrans" type="checkbox" @change="onLyOpt('trans')">翻译</label>
      <label v-if="verBox" class="ver" title="同一首歌酷狗上往往有多个歌词版本（官方/用户上传），翻译和音译各版本不一样，可在此切换">歌词版本
        <select v-model="verIdx" @change="onVersion">
          <option value="" disabled>选择版本</option>
          <option v-for="v in verList" :key="v.i" :value="String(v.i)">{{ v.label }}</option>
        </select>
      </label>
    </div>
    <div v-if="syntaxOpen" class="syntax">
      <ul>
        <li>1 行就是 1 个短语，空行表示稍作停顿</li>
        <li><code>夜色渐染/还记得</code> … 用 <code>/</code> 指定切分点</li>
        <li><code>*强调*</code> … 强调（更大・冲击力更强的演出）</li>
        <li>行尾 <code>!</code> … 加入闪烁与震动</li>
        <li><code>歌词|注释文字</code> … 注释排版里的小字</li>
        <li><code>[01:23.45]歌词</code> … 直接使用 LRC 时间戳</li>
        <li><code>[间奏 8]</code> … 8 秒间奏（只出背景与装饰）。省略秒数默认 4 秒</li>
        <li><code>#</code> 开头的行 … 注释（忽略）</li>
      </ul>
    </div>
  </div>
</template>

<style scoped>
.sec-btns { display: inline-flex; gap: 6px; }
.lang-row { display: flex; align-items: center; flex-wrap: wrap; gap: 6px; font-size: 11px; color: var(--muted); }
.lang-row select { width: auto; }
.lyopts { display: inline-flex; gap: 14px; font-size: 12px; align-items: center; flex-wrap: wrap; }
.lyopts label { display: inline-flex; gap: 4px; align-items: center; cursor: pointer; }
.lyopts .ver select { max-width: 180px; padding: 2px 4px; font-size: 11px; width: auto; }
.syntax {
  font-size: 12px; color: var(--muted);
  background: var(--raised);
  border: 1px solid var(--line);
  border-radius: var(--r-sm);
  padding: 10px 12px;
}
.syntax code { font-family: var(--mono); color: var(--amber); }
.syntax ul { margin: 0; padding-left: 16px; display: grid; gap: 3px; }
</style>
