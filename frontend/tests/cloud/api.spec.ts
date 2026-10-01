import {beforeEach, describe, expect, it, vi} from 'vitest';
import {api, fetchAudioBlob, fmtDur, parsePlaylistId} from '../../src/cloud/api';

describe('Cloud API', () => {
    beforeEach(() => {
        vi.restoreAllMocks();
    });

    it('fmtDur formats duration correctly', () => {
        expect(fmtDur(0)).toBe('0:00');
        expect(fmtDur(65)).toBe('1:05');
        expect(fmtDur(120)).toBe('2:00');
        expect(fmtDur(undefined)).toBe('0:00');
    });

    it('parsePlaylistId parses valid IDs', () => {
        expect(parsePlaylistId('https://music.163.com/#/playlist?id=123456')).toBe('123456');
        expect(parsePlaylistId('?global_collection_id=abc-123')).toBe('abc-123');
        expect(parsePlaylistId('12345')).toBe('12345');
        expect(parsePlaylistId('invalid_id_format')).toBe('invalid_id_format');
        expect(parsePlaylistId('shrt')).toBe('');
    });

    describe('API Calls', () => {
        it('search calls correct endpoint and parses JSON', async () => {
            const mockResponse = {ok: true, list: []};
            const fetchStub = vi.fn().mockResolvedValue({
                headers: new Headers({'content-type': 'application/json'}),
                json: vi.fn().mockResolvedValue(mockResponse)
            });
            vi.stubGlobal('fetch', fetchStub);

            const res = await api.search('kugou', 'test');
            expect(fetchStub).toHaveBeenCalledWith('/api/kg/search?kw=test');
            expect(res).toEqual(mockResponse);
        });

        it('url calls correct endpoint with params', async () => {
            const mockResponse = {ok: true, url: 'http://test'};
            const fetchStub = vi.fn().mockResolvedValue({
                headers: new Headers({'content-type': 'application/json'}),
                json: vi.fn().mockResolvedValue(mockResponse)
            });
            vi.stubGlobal('fetch', fetchStub);

            await api.url('netease', {id: '123', name: 'test', source: 'netease'}, '320');
            expect(fetchStub).toHaveBeenCalledWith('/api/ne/url?id=123&quality=320');
        });

        it('throws offline error on fetch failure', async () => {
            vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('Network error')));

            await expect(api.search('kugou', 'test')).rejects.toThrow('连不上本机服务：请（重新）运行字面 Live');
        });

        it('throws error if response is not JSON', async () => {
            const fetchStub = vi.fn().mockResolvedValue({
                headers: new Headers({'content-type': 'text/html'}),
            });
            vi.stubGlobal('fetch', fetchStub);

            await expect(api.search('kugou', 'test')).rejects.toThrow('本机服务响应异常（未返回 JSON，可能需要更新字面 Live）');
        });
    });

    describe('fetchAudioBlob', () => {
        it('fetches blob properly', async () => {
            const mockBlob = new Blob(['test'], {type: 'audio/mpeg'});
            const fetchStub = vi.fn().mockResolvedValue({
                blob: vi.fn().mockResolvedValue(mockBlob)
            });
            vi.stubGlobal('fetch', fetchStub);

            const res = await fetchAudioBlob('kugou', 'http://example.com/audio.mp3');
            expect(fetchStub).toHaveBeenCalledWith('/api/kg/audio?u=http%3A%2F%2Fexample.com%2Faudio.mp3');
            expect(res).toBe(mockBlob);
        });
    });
});
