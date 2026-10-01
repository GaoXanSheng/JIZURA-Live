import {beforeEach, describe, expect, it, vi} from 'vitest';
import {createPinia, setActivePinia} from 'pinia';
import {useCloud} from '../../src/stores/cloud';
import {api} from '../../src/cloud/api';

vi.mock('../../src/cloud/api', () => ({
    api: {
        search: vi.fn(),
        login: vi.fn(),
        playlists: vi.fn(),
    }
}));

vi.mock('../../src/jizura/look', () => ({
    toast: vi.fn(),
}));

describe('Cloud Store', () => {
    beforeEach(() => {
        setActivePinia(createPinia());
        vi.clearAllMocks();
    });

    it('initializes with default state', () => {
        const cloud = useCloud();
        expect(cloud.source).toBe('kugou');
        expect(cloud.pane).toBe('search');
        expect(cloud.results).toEqual([]);
        expect(cloud.login.kugou.logged).toBe(false);
    });

    it('setSource updates source and clears data', () => {
        const cloud = useCloud();
        cloud.results = [{id: '1', name: 'song', source: 'kugou'}];
        cloud.resultsError = 'err';
        cloud.playlists = [{id: '1', name: 'pl'}];

        cloud.setSource('netease');

        expect(cloud.source).toBe('netease');
        expect(cloud.results).toEqual([]);
        expect(cloud.resultsError).toBe('');
        expect(cloud.playlists).toEqual([]);
    });

    it('doSearch performs search and updates results', async () => {
        const cloud = useCloud();
        cloud.query = 'test';

        vi.mocked(api.search).mockResolvedValueOnce({ok: true, list: [{id: '1', name: 'test song', source: 'kugou'}]});

        await cloud.doSearch();

        expect(cloud.searching).toBe(false);
        expect(cloud.results.length).toBe(1);
        expect(cloud.results[0].name).toBe('test song');
    });

    it('doSearch handles error', async () => {
        const cloud = useCloud();
        cloud.query = 'test';

        vi.mocked(api.search).mockResolvedValueOnce({ok: false, error: 'network error'});

        await cloud.doSearch();

        expect(cloud.searching).toBe(false);
        expect(cloud.results).toEqual([]);
        expect(cloud.resultsError).toBe('搜索失败：network error');
    });

    it('setLoading updates loading state', () => {
        const cloud = useCloud();
        cloud.setLoading('key1', 'downloading');
        expect(cloud.loadingKey).toBe('key1');
        expect(cloud.loadingState).toBe('downloading');
    });
});
