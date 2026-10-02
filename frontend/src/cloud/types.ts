// 对应服务端 ApiModels.cs 里的 DTO，字段名转成小驼峰
export interface Song {
    source: 'kugou' | 'netease' | string;
    hash?: string;
    id?: string;
    albumId?: string;
    albumAudioId?: string;
    name: string;
    singer: string;
    album?: string;
    duration?: number;
    pay?: number;
    cover?: string;
}

export interface ApiBase {
    ok: boolean;
    error?: string
}

export interface SearchResponse extends ApiBase {
    total?: number;
    list?: Song[]
}

export interface UrlResponse extends ApiBase {
    url?: string;
    ext?: string;
    quality?: string;
    logged?: boolean;
    trial?: boolean;
    needVip?: boolean;
    stale?: boolean
}

export interface LyricResponse extends ApiBase {
    fmt?: 'krc' | 'lrc';
    content?: string;
    trans?: string;
    roma?: string;
    candidates?: LyricCandidate[]
}

export interface LyricCandidate {
    id?: string;
    accesskey?: string;
    from?: string;
    nick?: string
}

export interface QrResponse extends ApiBase {
    key?: string;
    img?: string
}

export interface QrCheckResponse extends ApiBase {
    status?: string | number;
    nickname?: string
}

export interface LoginResponse extends ApiBase {
    logged?: boolean;
    nickname?: string;
    userid?: string
}

export interface PlaylistInfo {
    id: string;
    name: string;
    count: number
}

export interface PlaylistsResponse extends ApiBase {
    list?: PlaylistInfo[]
}

export type Source = 'kugou' | 'netease';
