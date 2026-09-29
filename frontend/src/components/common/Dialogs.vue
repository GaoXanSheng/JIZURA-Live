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
      <h2 id="resetTitle">初始化</h2>
      <p>将以下内容全部清空，回到最初状态。<strong>此操作无法撤销。</strong></p>
      <ul>
        <li>歌词・曲名・歌手</li>
        <li>载入的歌曲（浏览器里保留的副本也一并删除）</li>
        <li>时间・逐行指定・导出范围</li>
        <li>风格・演出・配色・字体等设置</li>
        <li>方案历史（◀ ▶）与歌词・时间的撤销历史</li>
      </ul>
      <p class="sub">显示模式（简单/详细）、预览音量会保留。已保存的项目文件（.json）不受影响。</p>
      <div class="foot">
        <span></span>
        <span class="row">
          <button value="cancel" class="ghost" formnovalidate>取消</button>
          <button value="reset" class="danger" :disabled="busy" @click.prevent="doReset">初始化</button>
        </span>
      </div>
    </form>
  </dialog>

  <dialog ref="termsDlg" class="dlg" aria-labelledby="termsTitle">
    <form method="dialog">
      <h2 id="termsTitle">使用说明</h2>
      <div class="main">
        <p class="big">用本工具制作的<strong>视频与图片（输出物）的权利归制作者</strong>所有。</p>
        <p class="sub">※ 所使用的歌词与歌曲的权利归各自权利人所有。</p>
      </div>
      <p>工具本体以 <strong>MIT 许可证</strong>开源。基于 852wa/JIZURA 定制：</p>
      <p><a href="https://github.com/852wa/JIZURA" target="_blank" rel="noopener">https://github.com/852wa/JIZURA</a></p>
      <p class="sub">输入的歌词与歌曲只在本地处理，不会上传到任何服务器。在线搜索功能经由本机服务代理访问音乐平台。</p>
      <details>
        <summary>使用的开源组件</summary>
        <p>MP4 导出：mp4-muxer（MIT License）<br>字体：Google Fonts 发布的各字体（SIL Open Font License 1.1）<br>界面：Vue 3（MIT License）</p>
      </details>
      <div class="foot">
        <span class="mono muted">字面 Live · MIT</span>
        <button value="close" class="primary" autofocus>关闭</button>
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
  background: var(--panel);
  color: var(--text);
  border: 1px solid var(--line2);
  border-radius: var(--r-lg);
  box-shadow: var(--shadow-3);
}
dialog.dlg::backdrop { background: var(--backdrop); backdrop-filter: blur(3px); }
dialog.dlg h2 { font-size: 15px; letter-spacing: 0.08em; color: var(--text); margin: 0 0 14px; }
dialog.dlg p { margin: 0 0 10px; line-height: 1.75; font-size: 13px; }
dialog.dlg ul { margin: 0 0 12px; padding-left: 18px; display: grid; gap: 3px; font-size: 13px; }
dialog.dlg .sub { color: var(--muted); font-size: 12px; }
dialog.dlg .main { border-left: 3px solid var(--amber); background: var(--raised); padding: 12px 14px 4px; border-radius: 0 var(--r-sm) var(--r-sm) 0; margin-bottom: 14px; }
dialog.dlg .main .big { font-size: 15px; }
dialog.dlg .main strong { color: var(--amber); font-weight: 700; }
dialog.dlg a { color: var(--cyan); word-break: break-all; }
dialog.dlg details { font-size: 12px; color: var(--muted); margin: 4px 0 14px; }
dialog.dlg summary { cursor: pointer; }
dialog.dlg .foot { display: flex; justify-content: space-between; align-items: center; gap: 12px; border-top: 1px solid var(--line); padding-top: 12px; }
dialog.dlg .row { display: inline-flex; gap: 6px; }
</style>
