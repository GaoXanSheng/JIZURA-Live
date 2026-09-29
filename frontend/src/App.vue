<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue';
import { useEditor } from './stores/editor';
import { bus } from './jizura/bus';
import TopBar from './components/layout/TopBar.vue';
import LyricsPanel from './components/lyrics/LyricsPanel.vue';
import AudioPanel from './components/lyrics/AudioPanel.vue';
import LineList from './components/lyrics/LineList.vue';
import TapPanel from './components/lyrics/TapPanel.vue';
import StageCanvas from './components/stage/StageCanvas.vue';
import Transport from './components/stage/Transport.vue';
import TimelineCanvas from './components/stage/TimelineCanvas.vue';
import CutInfo from './components/stage/CutInfo.vue';
import RightPanel from './components/panels/RightPanel.vue';
import CloudMusicModal from './components/cloud/CloudMusicModal.vue';
import ToastHost from './components/common/ToastHost.vue';

const ed = useEditor();
const booted = ref(false);
const offs: (() => void)[] = [];

onMounted(() => {
  offs.push(bus.on('playstate', () => ed.syncPlayState()));
  offs.push(bus.on('playhead', () => ed.syncTime()));
  offs.push(bus.on('frame', () => ed.syncTime()));
  offs.push(bus.on('time', () => ed.syncTime()));
  offs.push(bus.on('replan', () => ed.syncPlan()));
  offs.push(bus.on('replan-sync', () => ed.syncPlan()));
  offs.push(bus.on('mode', (m: any) => { ed.mode = m; ed.syncPlan(); }));
  offs.push(bus.on('curline', (li: number) => { ed.curLine = li; }));
  offs.push(bus.on('booted', () => { booted.value = true; ed.syncAll(); }));
  booted.value = true;
  ed.syncAll();
});
onUnmounted(() => offs.forEach(f => f()));
</script>

<template>
  <div class="app" :class="['is-' + ed.mode]">
    <TopBar />
    <main class="work">
      <section class="col col-left" aria-label="歌词与时间">
        <TapPanel />
        <LyricsPanel />
        <AudioPanel />
        <LineList />
      </section>
      <section class="col col-stage" aria-label="预览">
        <StageCanvas />
        <Transport />
        <TimelineCanvas />
        <CutInfo />
      </section>
      <section class="col col-right" aria-label="风格与导出">
        <RightPanel />
      </section>
    </main>
    <CloudMusicModal />
    <ToastHost />
  </div>
</template>
