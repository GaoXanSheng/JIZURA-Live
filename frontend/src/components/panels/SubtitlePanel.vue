<script setup lang="ts">
/* 字幕设置面板（详细模式右栏）：渲染时叠加的音译/翻译字幕 */
import { onMounted, onUnmounted, reactive } from 'vue';
import { subConfig, saveSubConfig, redrawSub } from '../../cloud/subtitle';
import { bus } from '../../jizura/bus';

const cfg = reactive(subConfig());
const offs: (() => void)[] = [];

function set<K extends keyof typeof cfg>(k: K, v: (typeof cfg)[K]) {
  (cfg as any)[k] = v;
  saveSubConfig({ [k]: v } as any);
  redrawSub();
}

onMounted(() => {
  offs.push(bus.on('subcfg', () => Object.assign(cfg, subConfig())));
});
onUnmounted(() => offs.forEach(f => f()));
</script>

<template>
  <div class="sec pro-only">
    <div class="sec-h"><h2>字幕</h2><span class="muted">渲染时叠加（预览与导出）</span></div>
    <div class="fields">
      <label class="field field-wide">显示
        <select :value="cfg.content" @change="set('content', ($event.target as HTMLSelectElement).value as any)">
          <option value="off">不显示</option>
          <option value="trans">翻译</option>
          <option value="roma">音译</option>
          <option value="both">音译 + 翻译</option>
        </select>
      </label>
      <label class="field">位置
        <select :value="cfg.pos" @change="set('pos', ($event.target as HTMLSelectElement).value as any)">
          <option value="bottom">底部</option>
          <option value="top">顶部</option>
        </select>
      </label>
      <label class="field">字号 %<input :value="cfg.size" type="number" min="2" max="15" step="0.5" @change="set('size', parseFloat(($event.target as HTMLInputElement).value) || 0)"></label>
      <label class="field">描边<input :value="cfg.stroke" type="number" min="0" max="12" step="0.5" title="描边宽度（按 1080p 像素计）" @change="set('stroke', parseFloat(($event.target as HTMLInputElement).value) || 0)"></label>
      <label class="field">文字颜色<input :value="cfg.color" type="color" @input="set('color', ($event.target as HTMLInputElement).value)"></label>
      <label class="field">描边颜色<input :value="cfg.strokeColor" type="color" @input="set('strokeColor', ($event.target as HTMLInputElement).value)"></label>
      <label class="field">水平偏移 %<input :value="cfg.offX" type="number" min="-50" max="50" step="1" title="字幕水平中心偏移（画面宽度百分比，正=右移）" @change="set('offX', parseFloat(($event.target as HTMLInputElement).value) || 0)"></label>
      <label class="field">垂直偏移 %<input :value="cfg.offY" type="number" min="-50" max="50" step="1" title="字幕垂直偏移（画面高度百分比，正=下移）" @change="set('offY', parseFloat(($event.target as HTMLInputElement).value) || 0)"></label>
      <label class="field field-wide">字体
        <select :value="cfg.font" @change="set('font', ($event.target as HTMLSelectElement).value as any)">
          <option value="">跟随画面</option>
          <option value="sans">黑体（无衬线）</option>
          <option value="serif">宋体（衬线）</option>
        </select>
      </label>
      <label class="check field-wide"><input :checked="cfg.bar" type="checkbox" @change="set('bar', ($event.target as HTMLInputElement).checked)"><span>半透明底条</span></label>
    </div>
  </div>
</template>

<style scoped>
.sec { padding: 14px 16px; border-top: 1px solid var(--line); display: flex; flex-direction: column; gap: 10px; }
.sec-h { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
.fields { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 8px; }
.field { display: flex; flex-direction: column; gap: 4px; font-size: 11px; color: var(--muted); }
.field input[type='number'] { width: 100%; }
.field input[type='color'] { width: 100%; height: 28px; padding: 0; border: 1px solid var(--line); background: transparent; border-radius: var(--r-sm); }
.field-wide { grid-column: span 2; }
label.check { display: flex; align-items: center; gap: 8px; cursor: pointer; font-size: 12px; }
</style>
