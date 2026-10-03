<script lang="ts" setup>
import {onMounted, onUnmounted, ref} from 'vue';
import {useEditor} from '../stores/editor';
import {bus} from '../jizura/bus';
import TopBar from '../components/layout/TopBar.vue';
import LyricsPanel from '../components/lyrics/LyricsPanel.vue';
import AudioPanel from '../components/lyrics/AudioPanel.vue';
import LineList from '../components/lyrics/LineList.vue';
import TapPanel from '../components/lyrics/TapPanel.vue';
import StageCanvas from '../components/stage/StageCanvas.vue';
import Transport from '../components/stage/Transport.vue';
import TimelineCanvas from '../components/stage/TimelineCanvas.vue';
import CutInfo from '../components/stage/CutInfo.vue';
import RightPanel from '../components/panels/RightPanel.vue';
import CloudMusicModal from '../components/cloud/CloudMusicModal.vue';
import ToastHost from '../components/common/ToastHost.vue';
import Dialogs from '../components/common/Dialogs.vue';
import DevPerfPanel from '../components/common/DevPerfPanel.vue';
import {boot} from '../jizura/boot';
import {bindEditorBroadcast} from '../jizura/stage-bridge';

const ed = useEditor();
const booted = ref(false);
const offs: (() => void)[] = [];

// setup 阶段就得跑，早于全部子组件创建，它们同步读 S.project
boot(); // 只有编辑页跑，渲染页不读 localStorage、不绑键盘
bindEditorBroadcast(); // 向 /render 广播工程/时间/字幕

onMounted(() => {
  offs.push(bus.on('playstate', () => ed.syncPlayState()));
  // 时间码/进度条由 Transport 自己在 frame 里命令式更新（12.5Hz）
  // 别在这挂 playhead/frame → syncTime：响应式链路会带着整页重绘，
  // 实测 Paint 事件以 12.5Hz 落满 document/html 根层，连右栏 nav 都整层重画
  offs.push(bus.on('replan', () => ed.syncPlan()));
  offs.push(bus.on('replan-sync', () => ed.syncPlan()));
  offs.push(bus.on('curline', (li: number) => {
    ed.curLine = li;
  }));
  offs.push(bus.on('booted', () => {
    booted.value = true;
    ed.syncAll();
  }));
  booted.value = true;
  ed.syncAll();
});
onUnmounted(() => offs.forEach(f => f()));
</script>

<template>
  <div class="app flex flex-col h-full">
    <TopBar/>
    <main
        class="work flex-1 grid grid-cols-[minmax(280px,340px)_minmax(0,1fr)_minmax(300px,370px)] min-h-0 max-[1180px]:grid-cols-[minmax(0,1fr)_minmax(0,1fr)] max-[760px]:grid-cols-[minmax(0,1fr)]">
      <section
          aria-label="歌词与时间"
          class="flex flex-col min-w-0 min-h-0 overflow-y-auto overscroll-contain bg-panel border-r border-line max-[760px]:border-r-0 max-[760px]:border-t max-[760px]:border-line">
        <TapPanel/>
        <LyricsPanel/>
        <AudioPanel/>
        <LineList/>
      </section>
      <section
          aria-label="预览"
          class="col-stage flex flex-col gap-2.5 px-4 py-3 min-w-0 min-h-0 overflow-hidden max-[1180px]:overflow-y-auto max-[1180px]:[grid-column:1/-1] max-[1180px]:[order:-1]">
        <StageCanvas/>
        <!-- flex-none：中间列里只允许舞台画布伸缩，播放条/时间轴被压瘪会坏 -->
        <Transport class="flex-none"/>
        <TimelineCanvas class="flex-none"/>
        <CutInfo/>
      </section>
      <section
          aria-label="风格与导出"
          class="flex flex-col min-w-0 min-h-0 overflow-y-auto overscroll-contain bg-panel border-l border-line max-[1180px]:border-l-0 max-[760px]:border-l-0 max-[760px]:border-t max-[760px]:border-line">
        <RightPanel/>
      </section>
    </main>
    <CloudMusicModal/>
    <Dialogs/>
    <DevPerfPanel/>
    <ToastHost/>
  </div>
</template>
