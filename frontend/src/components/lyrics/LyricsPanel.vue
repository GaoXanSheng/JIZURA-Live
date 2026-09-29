<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue';
import { J } from '../../engine';
import { S } from '../../jizura/state';
import { bus } from '../../jizura/bus';
import { replan, replanSoon } from '../../jizura/engine';
import { remember, commit, toast } from '../../jizura/look';
import { flushSave } from '../../jizura/project';

const lyrics = ref('');
const lang = ref('auto');
const langNote = ref('');
const syntaxOpen = ref(false);
const ta = ref<HTMLTextAreaElement | null>(null);
const offs: (() => void)[] = [];

onMounted(() => {
  offs.push(bus.on('sync-ui', sync));
  offs.push(bus.on('lyrics-sync', sync));
  offs.push(bus.on('focus-lyrics', () => ta.value?.focus()));
  offs.push(bus.on('replan', noteLang));
  sync();
});
onUnmounted(() => offs.forEach(f => f()));

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
</script>

<template>
  <div class="sec">
    <div class="sec-h">
      <h2>歌词</h2>
      <span class="sec-btns">
        <button class="ghost small" title="清空歌词与逐行时间、指定、导出范围（可用 Ctrl+Z 撤销）">清空歌词</button>
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
