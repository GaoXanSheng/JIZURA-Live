<script lang="ts" setup>
// 跟随外部播放器：开关/状态/会话选择/手动对齐。逻辑全在 cloud/follow.ts，这里只展示
import {computed} from 'vue';
import {follow, followNudge, followRematch, followSelectSession, followToggle} from '../../cloud/follow';
import {useCloud} from '../../stores/cloud';

const cloud = useCloud();

const STATUS: Record<string, { text: string; cls: string }> = {
  off: {text: '未开启', cls: 'text-mut'},
  'no-session': {text: '未检测到播放器', cls: 'text-amber'},
  'no-meta': {text: '播放器未提供歌曲信息', cls: 'text-amber'},
  matching: {text: '正在匹配歌词…', cls: 'text-mut'},
  ok: {text: '已同步', cls: 'text-cyan'},
  'no-match': {text: '没匹配到歌词', cls: 'text-amber'},
  error: {text: '出错', cls: 'text-danger'},
};

const status = computed(() => STATUS[follow.status] || STATUS.off);
const fmtName = computed(() =>
    follow.fmt === 'krc' ? 'KRC 逐字' : follow.fmt === 'ne' ? '网易 LRC' : follow.fmt === 'lrc' ? 'LRC' : '');
</script>

<template>
  <div class="flex flex-col gap-3 flex-1 min-h-0 overflow-y-auto">
    <div class="flex items-center gap-3 bg-raised border border-line rounded-[10px] p-3.5">
      <div class="flex-1 min-w-0 flex flex-col gap-0.5">
        <b>跟随外部播放器</b>
        <small class="text-mut text-[11px]">酷狗 / 网易云 / QQ音乐 等播放器出声，这里只同步进度渲染歌词</small>
      </div>
      <button :class="follow.on ? 'btn btn-ghost' : 'btn btn-primary'" @click="followToggle()">
        {{ follow.on ? '停止跟随' : '开始跟随' }}
      </button>
    </div>

    <div v-if="follow.on" class="flex flex-col gap-2.5 bg-raised border border-line rounded-[10px] p-3.5">
      <div class="flex items-baseline gap-2 min-w-0">
        <span class="text-mut text-[11px] flex-none">{{ follow.appName || '…' }}</span>
        <span class="text-sm truncate">{{ follow.title || '…' }}<span v-if="follow.artist"
                                                                       class="text-mut"> - {{ follow.artist }}</span></span>
      </div>
      <div class="flex items-center gap-2 text-xs min-w-0">
        <span :class="status.cls" class="flex-none">{{ status.text }}</span>
        <span v-if="follow.matchedAs" class="text-mut truncate" :title="follow.matchedAs">{{ follow.matchedAs }}</span>
        <span v-if="fmtName" class="flex-none px-1.5 py-px rounded bg-amber/10 text-amber text-[10px]">{{ fmtName }}</span>
        <button v-if="follow.status === 'no-match' || follow.status === 'error'"
                class="flex-none border-0 bg-transparent text-cyan text-[11px] cursor-pointer underline p-0"
                @click="followRematch()">重新匹配</button>
      </div>
      <div v-if="follow.detail" class="text-[11px] text-amber leading-snug">{{ follow.detail }}</div>
      <div v-if="follow.estimated"
           class="text-[11px] text-amber leading-snug border-t border-line pt-2">
        这个播放器不上报播放进度（酷狗部分版本如此）：进度按「切歌/恢复播放=从头」估算。
        歌放中途才开始跟、或在播放器里拖了进度条会有偏差——把这首歌切走再切回来（从头播）即自动对准，
        或用下面的对齐微调。
      </div>

      <div v-if="follow.sessions.length > 1" class="flex items-center gap-2 pt-1 border-t border-line">
        <span class="text-mut text-[11px] flex-none">跟随的播放器</span>
        <select class="flex-1 min-w-0 bg-raised border border-line2 rounded-lg px-2 py-1 text-xs"
                :value="follow.selectedAppId"
                @change="followSelectSession(($event.target as HTMLSelectElement).value)">
          <option value="">自动选择</option>
          <option v-for="s in follow.sessions" :key="s.appId" :value="s.appId">
            {{ s.appName }}（{{ s.title || '无歌曲信息' }}）
          </option>
        </select>
      </div>

      <div class="flex items-center gap-2 pt-1 border-t border-line">
        <span class="text-mut text-[11px] flex-none">对齐微调</span>
        <button class="btn btn-ghost btn-sm" @click="followNudge(-0.5)">-0.5s</button>
        <button class="btn btn-ghost btn-sm" @click="followNudge(0.5)">+0.5s</button>
        <template v-if="follow.estimated">
          <button class="btn btn-ghost btn-sm" @click="followNudge(-5)">-5s</button>
          <button class="btn btn-ghost btn-sm" @click="followNudge(5)">+5s</button>
        </template>
        <button class="btn btn-ghost btn-sm" :disabled="!follow.offset" @click="followNudge(-follow.offset)">重置
        </button>
        <span class="text-mut text-[11px]">{{ follow.offset > 0 ? '+' : '' }}{{ follow.offset.toFixed(1) }}s（正数=歌词提前）</span>
      </div>
    </div>

    <p class="hint border-l-2 border-line2 pl-2.5">
      音频由播放器自己出声，本工具只读系统媒体进度；网易云需在 设置→播放和下载
      打开「允许在任务栏显示和控制播放」。切歌后自动按 酷狗（逐字）→网易云（逐行）匹配歌词；
      没匹配到可去「搜索」页手动找，跟随模式下不要点载入歌曲（会带入音频抢时间轴）。
      打点 / 切分 / 字幕等工具照常可用。
    </p>
    <p class="hint border-l-2 border-line2 pl-2.5">
      <button class="border-0 bg-transparent text-cyan text-[11px] cursor-pointer underline p-0"
              @click="cloud.open = true; cloud.pane = 'search'">去搜索页手动找歌
      </button>
      ｜跟随开启期间本地循环自动停用，停止跟随后恢复。
    </p>
  </div>
</template>
