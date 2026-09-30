<script setup lang="ts">
/* 原生 <dialog>：初始化确认 + 输出物权利说明（简中重写） */
import { onMounted, onUnmounted, ref } from 'vue';
import { bus } from '../../jizura/bus';
import { resetAll } from '../../jizura/look';

const resetOpen = ref(false);
const termsOpen = ref(false);
const busy = ref(false);
const offs: (() => void)[] = [];

function closeAll() { resetOpen.value = false; termsOpen.value = false; }

async function doReset() {
  busy.value = true;
  resetOpen.value = false;
  await resetAll();
  busy.value = false;
}

function onEsc(e: KeyboardEvent) {
  if (e.key === 'Escape' && (resetOpen.value || termsOpen.value)) closeAll();
}

onMounted(() => {
  offs.push(bus.on('open-reset', () => { termsOpen.value = false; resetOpen.value = true; }));
  offs.push(bus.on('open-terms', () => { resetOpen.value = false; termsOpen.value = true; }));
  window.addEventListener('keydown', onEsc);
});
onUnmounted(() => {
  window.removeEventListener('keydown', onEsc);
  offs.forEach(f => f());
});
</script>

<template>
  <!-- 模态用普通 fixed 层（原生 dialog 的 top layer 提升在本页有 ~470ms 的层树重建开销） -->
  <Teleport to="body">
    <Transition name="fade">
      <div v-if="resetOpen" class="jl-modal-open fixed inset-0 z-[3000] grid place-items-center p-6" style="background: rgba(6, 6, 8, 0.6)" @click.self="resetOpen = false">
        <div class="w-[min(540px,calc(100vw-32px))] max-h-[calc(100vh-48px)] overflow-auto bg-panel border border-line2 rounded-[14px] shadow-modal p-6 pb-4.5" role="dialog" aria-modal="true" aria-labelledby="resetTitle">
          <h2 class="text-[15px] tracking-wider font-semibold mb-3.5">初始化</h2>
          <p class="text-[13px] leading-relaxed mb-2.5">将以下内容全部清空，回到最初状态。<strong class="text-amber font-bold">此操作无法撤销。</strong></p>
          <ul class="grid gap-0.5 text-[13px] pl-[18px] mb-3 list-disc">
            <li>歌词・曲名・歌手</li>
            <li>载入的歌曲（浏览器里保留的副本也一并删除）</li>
            <li>时间・逐行指定・导出范围</li>
            <li>风格・演出・配色・字体等设置</li>
            <li>方案历史（◀ ▶）与歌词・时间的撤销历史</li>
          </ul>
          <p class="text-mut text-xs mb-3">预览音量会保留。已保存的项目文件（.json）不受影响。</p>
          <div class="flex justify-end items-center gap-1.5 border-t border-line pt-3">
            <button class="btn btn-ghost" @click="resetOpen = false">取消</button>
            <button class="btn btn-danger" :disabled="busy" @click="doReset">初始化</button>
          </div>
        </div>
      </div>
    </Transition>

    <Transition name="fade">
      <div v-if="termsOpen" class="jl-modal-open fixed inset-0 z-[3000] grid place-items-center p-6" style="background: rgba(6, 6, 8, 0.6)" @click.self="termsOpen = false">
        <div class="w-[min(540px,calc(100vw-32px))] max-h-[calc(100vh-48px)] overflow-auto bg-panel border border-line2 rounded-[14px] shadow-modal p-6 pb-4" role="dialog" aria-modal="true" aria-labelledby="termsTitle">
          <h2 class="text-[15px] tracking-wider font-semibold mb-3.5">使用说明</h2>
          <div class="border-l-[3px] border-amber bg-raised px-3.5 pt-3 pb-1 rounded-r-md mb-3.5">
            <p class="!text-[15px]">用本工具制作的<strong class="text-amber font-bold">视频与图片（输出物）的权利归制作者</strong>所有。</p>
            <p class="text-mut text-xs">※ 所使用的歌词与歌曲的权利归各自权利人所有。</p>
          </div>
          <p class="text-[13px] leading-relaxed mb-2.5">工具本体基于 852wa/JIZURA 定制：</p>
          <p class="mb-2.5"><a href="https://github.com/852wa/JIZURA" target="_blank" rel="noopener" class="text-cyan break-all">https://github.com/852wa/JIZURA</a></p>
          <p class="text-mut text-xs mb-3.5">输入的歌词与歌曲只在本地处理，不会上传到任何服务器。在线搜索功能经由本机服务代理访问音乐平台。</p>
          <details class="text-xs text-mut my-3.5">
            <summary class="cursor-pointer">使用的开源组件</summary>
            <p class="mt-1.5">MP4 导出：mp4-muxer（MIT License）<br>字体：Google Fonts 发布的各字体（SIL Open Font License 1.1）<br>界面：Vue 3（MIT License）</p>
          </details>
          <div class="flex justify-between items-center gap-3 border-t border-line pt-3">
            <span class="font-mono text-xs text-mut">字面 Live · MIT</span>
            <button class="btn btn-primary" @click="termsOpen = false">关闭</button>
          </div>
        </div>
      </div>
    </Transition>
  </Teleport>
</template>
