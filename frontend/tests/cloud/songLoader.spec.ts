import {beforeEach, describe, expect, it, vi} from 'vitest';
import {createPinia, setActivePinia} from 'pinia';
import {loadSong, songKey} from '../../src/cloud/songLoader';
import {api, fetchAudioBlob} from '../../src/cloud/api';
import {useCloud} from '../../src/stores/cloud';
import {loadAudioFile, seek} from '../../src/jizura/engine';
import {toast} from '../../src/jizura/look';

vi.mock('../../src/cloud/api', () => ({
    api: {
        url: vi.fn(),
        lyric: vi.fn(),
    },
    fetchAudioBlob: vi.fn(),
}));

vi.mock('../../src/cloud/lyricStore', () => ({
    adoptKgLyric: vi.fn().mockResolvedValue('krc'),
    adoptNeLyric: vi.fn().mockResolvedValue(undefined),
}));

vi.mock('../../src/jizura/engine', () => ({
    loadAudioFile: vi.fn().mockResolvedValue(true),
    replan: vi.fn(),
    seek: vi.fn(),
}));

vi.mock('../../src/jizura/look', () => ({
    resetLyricBinding: vi.fn(),
    toast: vi.fn(),
}));

vi.mock('../../src/jizura/bus', () => ({
    bus: {
        emit: vi.fn(),
    }
}));

describe('songLoader', () => {
    beforeEach(() => {
        setActivePinia(createPinia());
        vi.clearAllMocks();
    });

    it('songKey generates correct key', () => {
        expect(songKey({source: 'kugou', hash: '123', name: 'test'} as any)).toBe('kugou|123|test');
        expect(songKey({source: 'netease', id: '456', name: 'test2'} as any)).toBe('netease|456|test2');
    });

    it('loadSong success flow', async () => {
        const cloud = useCloud();
        vi.mocked(api.url).mockResolvedValueOnce({ok: true, url: 'http://test'});
        vi.mocked(fetchAudioBlob).mockResolvedValueOnce(new Blob(['test'], {type: 'audio/mpeg'}));
        vi.mocked(api.lyric).mockResolvedValueOnce({ok: true});

        const song = {source: 'kugou', hash: '123', name: 'test song'} as any;

        await loadSong('kugou', song, '320');

        expect(cloud.loadingKey).toBe(songKey(song));
        expect(cloud.loadingState).toBe('');
        expect(api.url).toHaveBeenCalled();
        expect(fetchAudioBlob).toHaveBeenCalled();
        expect(api.lyric).toHaveBeenCalled();
        expect(loadAudioFile).toHaveBeenCalled();
        expect(seek).toHaveBeenCalledWith(0);
        expect(toast).toHaveBeenCalledWith(expect.stringContaining('已载入：test song'));
    });

    it('loadSong handles missing url', async () => {
        const cloud = useCloud();
        vi.mocked(api.url).mockResolvedValueOnce({ok: false, error: 'no url'});

        const song = {source: 'kugou', hash: '123', name: 'test song'} as any;

        await loadSong('kugou', song, '320');

        expect(cloud.loadingState).toBe('error');
        expect(toast).toHaveBeenCalledWith('载入失败：no url');
    });

    it('loadSong handles VIP requirement', async () => {
        const cloud = useCloud();
        vi.mocked(api.url).mockResolvedValueOnce({ok: true, needVip: true});

        const song = {source: 'kugou', hash: '123', name: 'test song'} as any;

        await loadSong('kugou', song, '320');

        expect(cloud.loadingState).toBe('error');
        expect(toast).toHaveBeenCalledWith(expect.stringContaining('需要酷狗 VIP'));
    });
});
