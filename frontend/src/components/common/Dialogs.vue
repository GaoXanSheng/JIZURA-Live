<script setup lang="ts">
/* 原生 <dialog>：初始化确认 + 输出物权利说明（简中重写） */
import { onMounted, onUnmounted, ref } from 'vue';
import { bus } from '../../jizura/bus';
import { resetAll } from '../../jizura/look';

const resetDlg = ref<HTMLDialogElement | null>(null);
const termsDlg = ref<HTMLDialogElement | null>(null);
const busy = ref(false);
const offs: (() => void)[] = [];

async function doReset() {
  busy.value = true;
  resetDlg.value?.close('reset');
  await resetAll();
  busy.value = false;
}

onMounted(() => {
  offs.push(bus.on('open-reset', () => { resetDlg.value?.showModal(); }));
  offs.push(bus.on('open-terms', () => { termsDlg.value?.showModal(); }));
  // 点遮罩关闭
  [resetDlg, termsDlg].forEach(d => {
    d.value?.addEventListener('click', (e: any) => {
      if (e.target === d.value) d.value!.close();
    });
  });
});
onUnmounted(() => offs.forEach(f => f()));
</script>

<template>
  <dialog ref="resetDlg" class="dlg" aria-labelledby="resetTitle">
    <form method="dialog">
      <h2>初始化</h2>
      <p>将以下内容全部清空，回到最初状态。<strong>此操作无法撤销。</strong></p>
      <ul>
        <li>歌词・曲名・歌手</li>
        <li>载入的歌曲（浏览器里保留的副本也一并删除）</li>
        <li>时间・逐行指定・导出范围</li>
        <li>风格・演出・配色・字体等设置</li>
        <li>方案历史（◀ ▶）与歌词・时间的撤销历史</li>
      </ul>
      <p class="text-mut text-xs">预览音量会保留。已保存的项目文件（.json）不受影响。</p>
      <div class="flex justify-between items-center gap-3 border-t border-line pt-3 mt-3">
        <span></span>
        <span class="inline-flex gap-1.5">
          <button value="cancel" class="btn btn-ghost" formnovalidate>取消</button>
          <button value="reset" class="btn btn-danger" :disabled="busy" @click.prevent="doReset">初始化</button>
        </span>
      </div>
    </form>
  </dialog>

  <dialog ref="termsDlg" class="dlg" aria-labelledby="termsTitle">
    <form method="dialog">
      <h2>使用说明</h2>
      <div class="border-l-[3px] border-amber bg-raised px-3.5 pt-3 pb-1 rounded-r-md mb-3.5">
        <p class="!text-[15px]">用本工具制作的<strong class="text-amber font-bold">视频与图片（输出物）的权利归制作者</strong>所有。</p>
        <p class="text-mut text-xs">※ 所使用的歌词与歌曲的权利归各自权利人所有。</p>
      </div>
      <p>工具本体以 <strong>MIT 许可证</strong>开源。基于 852wa/JIZURA 定制：</p>
      <p><a href="https://github.com/852wa/JIZURA" target="_blank" rel="noopener" class="text-cyan break-all">https://github.com/852wa/JIZURA</a></p>
      <p class="text-mut text-xs">输入的歌词与歌曲只在本地处理，不会上传到任何服务器。在线搜索功能经由本机服务代理访问音乐平台。</p>
      <details class="text-xs text-mut my-3.5">
        <summary class="cursor-pointer">使用的开源组件</summary>
        <p class="mt-1.5">MP4 导出：mp4-muxer（MIT License）<br>字体：Google Fonts 发布的各字体（SIL Open Font License 1.1）<br>界面：Vue 3（MIT License）</p>
      </details>
      <div class="flex justify-between items-center gap-3 border-t border-line pt-3">
        <span class="font-mono text-xs text-mut">字面 Live · MIT</span>
        <button value="close" class="btn btn-primary" autofocus>关闭</button>
      </div>
    </form>
  </dialog>
</template>

<style scoped>
dialog.dlg {
  width: min(540px, calc(100vw - 32px));
  max-height: calc(100vh - 48px);
  overflow: auto;
  padding: 22px 24px 18px;
  background: var(--color-panel);
  color: var(--color-fg);
  border: 1px solid var(--color-line2);
  border-radius: 14px;
  box-shadow: var(--shadow-modal);
}
dialog.dlg::backdrop { background: rgba(6, 6, 8, 0.6); backdrop-filter: blur(3px); }
dialog.dlg h2 { font-size: 15px; letter-spacing: 0.08em; margin: 0 0 14px; font-weight: 600; }
dialog.dlg p { margin: 0 0 10px; line-height: 1.75; font-size: 13px; }
dialog.dlg ul { margin: 0 0 12px; padding-left: 18px; display: grid; gap: 3px; font-size: 13px; }
</style>
