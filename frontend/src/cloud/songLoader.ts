/* 歌曲载入管线：搜索结果/歌单曲目 → 播放链接 → 音频代理下载 → 引擎装载 → 歌词拉取对齐 */
import {bus} from '../jizura/bus';
import {useCloud} from '../stores/cloud';
import {loadAudioFile, replan, seek} from '../jizura/engine';
import {resetLyricBinding, toast} from '../jizura/look';
import {api, fetchAudioBlob} from './api';
import {adoptKgLyric, adoptNeLyric} from './lyricStore';
import type {Song, Source} from './types';

const MIME: Record<string, string> = { mp3: 'audio/mpeg', flac: 'audio/flac', ogg: 'audio/ogg', m4a: 'audio/mp4' };

let loadSeq = 0;

export async function loadSong(source: Source, it: Song, quality: string): Promise<void> {
  const my = ++loadSeq;
  const cloud = useCloud();
  cloud.setLoading(songKey(it), 'loading');
  try {
    const u = await api.url(source, it, quality);
    if (my !== loadSeq) return;
    if (!u.ok) throw new Error(u.error || '拿不到播放链接');
    if (u.needVip) {
      bus.emit('cloud-vip-needed', source);
      throw new Error(`这首需要${source === 'netease' ? '网易云' : '酷狗'} VIP，扫码登录后再试`);
    }
    cloud.setLoading(songKey(it), 'downloading');
    const blob = await fetchAudioBlob(source, u.url!);
    if (my !== loadSeq) return;
    const ext = (u.ext || (blob.type.match(/audio\/(\w+)/) || [])[1] || 'mp3').toLowerCase();
    const file = new File([blob], `${it.singer || ''} - ${it.name}.${ext}`.replace(/\s+-\s+\./, '.'),
                          { type: MIME[ext] || blob.type || 'application/octet-stream' });

    let lyricNote = '';
      let lyricDeferred = false;
    try {
      const ly = await api.lyric(source, it);
      if (my !== loadSeq) return;
      if (ly.ok) {
          resetLyricBinding();   // 换歌：旧歌词的逐行时间/覆盖/导出范围绑定的是旧行号，先清空再填新词
          // deferReplan：跳过这里的同步重排，loadAudioFile 末尾的 replan 一次到位——
          // 载歌窗口里连做两轮全曲 plan + 行列表/时间轴级联，是可感知的一瞬卡顿
        if (source === 'netease') {
            await adoptNeLyric(ly, true);
          lyricNote = (ly.trans || ly.roma) ? '，歌词已填入（含注音）' : '，歌词已填入（无音译/翻译）';
        } else {
            const fmt = await adoptKgLyric(ly, false, true);
          lyricNote = fmt === 'krc' ? '，歌词已填入' : '，歌词已填入（普通 LRC，无音译/翻译）';
        }
          lyricDeferred = true;
      } else lyricNote = '，没找到歌词';
    } catch { lyricNote = '，歌词加载失败'; }

    // 音频分析完成后再装载（歌词已先行就位），并把播放位置归零。
    // 曲名/歌手不自动填：只能用户手动输入
    const loaded = await loadAudioFile(file);
    if (my !== loadSeq) return;
    if (loaded) { bus.emit('sync-ui'); seek(0); } else if (lyricDeferred) replan();   // 音频装载失败：已写入的歌词仍要排进 plan

    toast(u.trial ? `只载入 60 秒试听（这首需要 VIP，可在「账号」页登录）：${it.name}${lyricNote}`
                  : `已载入：${it.name}${lyricNote}`);
    cloud.setLoading(songKey(it), '');
  } catch (e: any) {
    if (my === loadSeq) {
      toast('载入失败：' + (e.message || e));
      cloud.setLoading(songKey(it), 'error');
    }
  }
}

export function songKey(it: Song): string {
  return `${it.source}|${it.id || it.hash || ''}|${it.name}`;
}
