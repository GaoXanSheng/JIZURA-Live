/* /kg/* 与 /ne/* 本机服务 API 客户端（对应 KgController / NeController） */
import type { Source, ApiBase, LoginResponse, LyricResponse, PlaylistsResponse, QrCheckResponse, QrResponse, SearchResponse, Song, UrlResponse } from './types';

const OFFLINE = '连不上本机服务：请（重新）运行字面 Live';

async function call<T extends ApiBase>(source: Source, path: string, params?: Record<string, string | number | undefined>): Promise<T> {
  const src = source === 'netease' ? '/api/ne/' : '/api/kg/';
  const q = params ? '?' + new URLSearchParams(
    Object.entries(params).filter(([, v]) => v !== undefined).map(([k, v]) => [k, String(v)]),
  ) : '';
  let r: Response;
  try {
    r = await fetch(src + path + q);
  } catch {
    throw new Error(OFFLINE);
  }
  if (!(r.headers.get('content-type') || '').includes('json')) {
    throw new Error('本机服务响应异常（未返回 JSON，可能需要更新字面 Live）');
  }
  return r.json() as Promise<T>;
}

export const api = {
  search: (source: Source, kw: string) => call<SearchResponse>(source, 'search', { kw }),
  url: (source: Source, song: Song, quality: string) => call<UrlResponse>(source, 'url', source === 'netease'
    ? { id: song.id, quality }
    : { hash: song.hash, album_id: song.albumId, album_audio_id: song.albumAudioId, quality }),
  lyric: (source: Source, song: Song) => call<LyricResponse>(source, 'lyric', source === 'netease'
    ? { id: song.id }
    : { hash: song.hash, album_id: song.albumId }),
  lyricById: (source: Source, id?: string, accesskey?: string) => call<LyricResponse>(source, 'lyric', { id, accesskey }),
  login: (source: Source) => call<LoginResponse>(source, 'login'),
  logout: (source: Source) => call<LoginResponse>(source, 'logout'),
  qr: (source: Source) => call<QrResponse>(source, 'qr'),
  qrCheck: (source: Source, key: string) => call<QrCheckResponse>(source, 'qr/check', { key }),
  playlists: (source: Source) => call<PlaylistsResponse>(source, 'playlists'),
  playlist: (source: Source, id: string) => call<SearchResponse>(source, 'playlist', { id }),
  audioProxyUrl: (source: Source, url: string) => `/kg/audio?u=${encodeURIComponent(url)}`.replace('/kg/audio', source === 'netease' ? '/ne/audio' : '/kg/audio'),
};

export async function fetchAudioBlob(source: Source, url: string): Promise<Blob> {
  const base = source === 'netease' ? '/api/ne/audio?u=' : '/api/kg/audio?u=';
  return (await fetch(base + encodeURIComponent(url))).blob();
}

export const fmtDur = (sec?: number) => {
  const s = Math.round(sec || 0);
  return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
};

/* 粘贴歌单链接 / ID 的解析（酷狗 global_collection_id / 网易云 id / 纯 ID） */
export function parsePlaylistId(s: string): string {
  s = String(s || '').trim();
  const m = s.match(/[?&]global_collection_id=([\w-]+)/) || s.match(/[?&]id=([\w-]+)/) || s.match(/^([\w-]{5,})$/);
  return m ? m[1] : '';
}
