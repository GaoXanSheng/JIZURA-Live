<script lang="ts" setup>
/* 字幕设置面板（右栏底部）：渲染时叠加的音译/翻译字幕 */
import {onMounted, onUnmounted, reactive} from 'vue';
import {redrawSub, saveSubConfig, subConfig} from '../../cloud/subtitle';
import {bus} from '../../jizura/bus';

const cfg = reactive(subConfig());
const offs: (() => void)[] = [];

function set<K extends keyof typeof cfg>(k: K, v: (typeof cfg)[K]) {
  (cfg as any)[k] = v;
  saveSubConfig({[k]: v} as any);
  redrawSub();
}

onMounted(() => {
  offs.push(bus.on('subcfg', () => Object.assign(cfg, subConfig())));
});
onUnmounted(() => offs.forEach(f => f()));
</script>

<template>
  <div class="border border-line rounded-md bg-raised/60 p-3.5 flex flex-col gap-2.5 mb-3">
    <div class="sec-h"><span class="sec-title">字幕</span><span
        class="text-[11px] text-mut">渲染时叠加（预览与导出）</span></div>
    <div class="grid grid-cols-3 gap-2">
      <label class="field col-span-2">显示
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
      <label class="field">字号 %<input :value="cfg.size" max="15" min="2" step="0.5" type="number"
                                        @change="set('size', parseFloat(($event.target as HTMLInputElement).value) || 0)"></label>
      <label class="field">描边<input :value="cfg.stroke" max="12" min="0" step="0.5" title="描边宽度（按 1080p 像素计）"
                                      type="number"
                                      @change="set('stroke', parseFloat(($event.target as HTMLInputElement).value) || 0)"></label>
      <label class="field">文字颜色<input :value="cfg.color" type="color"
                                          @input="set('color', ($event.target as HTMLInputElement).value)"></label>
      <label class="field">描边颜色<input :value="cfg.strokeColor" type="color"
                                          @input="set('strokeColor', ($event.target as HTMLInputElement).value)"></label>
      <label class="field">水平偏移 %<input :value="cfg.offX" max="50" min="-50" step="1"
                                            title="字幕水平中心偏移（画面宽度百分比，正=右移）"
                                            type="number"
                                            @change="set('offX', parseFloat(($event.target as HTMLInputElement).value) || 0)"></label>
      <label class="field">垂直偏移 %<input :value="cfg.offY" max="50" min="-50" step="1"
                                            title="字幕垂直偏移（画面高度百分比，正=下移）"
                                            type="number"
                                            @change="set('offY', parseFloat(($event.target as HTMLInputElement).value) || 0)"></label>
      <label class="field col-span-2">字体
        <select :value="cfg.font" @change="set('font', ($event.target as HTMLSelectElement).value as any)">
          <option value="">跟随画面</option>
          <option value="sans">黑体（无衬线）</option>
          <option value="serif">宋体（衬线）</option>
        </select>
      </label>
      <label class="checkline col-span-3"><input :checked="cfg.bar" type="checkbox"
                                                 @change="set('bar', ($event.target as HTMLInputElement).checked)"><span>半透明底条</span></label>
    </div>
  </div>
</template>
