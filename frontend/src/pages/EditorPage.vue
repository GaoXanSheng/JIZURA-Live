<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue';
import { useEditor } from '../stores/editor';
import { bus } from '../jizura/bus';
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
import { boot } from '../jizura/boot';
import { bindEditorBroadcast } from '../jizura/stage-bridge';

const ed = useEditor();
const booted = ref(false);
const offs: (() => void)[] = [];

/* setup 阶段就启动：必须早于全部子组件的创建（它们同步读 S.project） */
boot();                    // 编辑页引导启动（渲染页不跑：不读 localStorage、不绑键盘）
bindEditorBroadcast();     // 向 /render 渲染页广播工程/时间/字幕（BroadcastChannel 直通）

onMounted(() => {
  offs.push(bus.on('playstate', () => ed.syncPlayState()));
  offs.push(bus.on('playhead', () => ed.syncTime()));
  offs.push(bus.on('frame', () => ed.syncTime()));
  offs.push(bus.on('time', () => ed.syncTime()));
  offs.push(bus.on('replan', () => ed.syncPlan()));
  offs.push(bus.on('replan-sync', () => ed.syncPlan()));
  offs.push(bus.on('curline', (li: number) => { ed.curLine = li; }));
  offs.push(bus.on('booted', () => { booted.value = true; ed.syncAll(); }));
  booted.value = true;
  ed.syncAll();
});
onUnmounted(() => offs.forEach(f => f()));
</script>

<template>
  <div class="app flex flex-col h-full">
    <TopBar />
    <main class="work flex-1 grid grid-cols-[minmax(280px,340px)_minmax(0,1fr)_minmax(300px,370px)] min-h-0 max-[1180px]:grid-cols-[minmax(0,1fr)_minmax(0,1fr)] max-[760px]:grid-cols-[minmax(0,1fr)]">
      <section class="flex flex-col min-w-0 min-h-0 overflow-y-auto overscroll-contain bg-panel border-r border-line max-[760px]:border-r-0 max-[760px]:border-t max-[760px]:border-line" aria-label="歌词与时间">
        <TapPanel />
        <LyricsPanel />
        <AudioPanel />
        <LineList />
      </section>
      <section class="col-stage flex flex-col gap-2.5 px-4 py-3 min-w-0 min-h-0 overflow-hidden max-[1180px]:overflow-y-auto max-[1180px]:[grid-column:1/-1] max-[1180px]:[order:-1]" aria-label="预览">
        <StageCanvas />
        <Transport />
        <TimelineCanvas />
        <CutInfo />
      </section>
      <section class="flex flex-col min-w-0 min-h-0 overflow-y-auto overscroll-contain bg-panel border-l border-line max-[1180px]:border-l-0 max-[760px]:border-l-0 max-[760px]:border-t max-[760px]:border-line" aria-label="风格与导出">
        <RightPanel />
      </section>
    </main>
    <CloudMusicModal />
    <Dialogs />
    <ToastHost />
  </div>
</template>
