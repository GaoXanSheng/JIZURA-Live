/* 歌曲载入管线：搜索结果/歌单曲目 → 播放链接 → 音频代理下载 → 引擎装载 → 歌词拉取对齐 */
import { S } from '../jizura/state';
import { bus } from '../jizura/bus';
import { useCloud } from '../stores/cloud';
import { loadAudioFile, seek } from '../jizura/engine';
import { toast } from '../jizura/look';
import { api, fetchAudioBlob } from './api';
import { adoptKgLyric, adoptNeLyric } from './lyricStore';
import type { Song, Source } from './types';

const MIME: Record<string, string> = { mp3: 'audio/mpeg', flac: 'audio/flac', ogg: 'audio/ogg', m4a: 'audio/mp4' };

let loadSeq = 0;
export function loadingId(): number { return loadSeq; }

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
    try {
      const ly = await api.lyric(source, it);
      if (my !== loadSeq) return;
      if (ly.ok) {
        if (source === 'netease') {
          await adoptNeLyric(ly);
          lyricNote = (ly.trans || ly.roma) ? '，歌词已填入（含注音）' : '，歌词已填入（无音译/翻译）';
        } else {
          const fmt = await adoptKgLyric(ly, false);
          lyricNote = fmt === 'krc' ? '，歌词已填入' : '，歌词已填入（普通 LRC，无音译/翻译）';
        }
      } else lyricNote = '，没找到歌词';
    } catch { lyricNote = '，歌词加载失败'; }

    // 音频分析完成后再装载（歌词已先行就位），并把播放位置归零
    if (!S.project.title) S.project.title = it.name;
    if (!S.project.artist) S.project.artist = it.singer;
    const loaded = await loadAudioFile(file);
    if (my !== loadSeq) return;
    if (loaded) { bus.emit('sync-ui'); seek(0); }

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

/* 新歌载入后把项目里的曲名/歌手一并补上（若为空），体验与手动载入一致 */
export function syncSongMeta(it: Song) {
  if (!S.project.title) S.project.title = it.name;
  if (!S.project.artist) S.project.artist = it.singer;
  bus.emit('sync-ui');
}
