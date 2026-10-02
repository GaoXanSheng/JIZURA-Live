import {beforeEach, describe, expect, it, vi} from 'vitest';
import {createPinia, setActivePinia} from 'pinia';
import {useEditor} from '../../src/stores/editor';
import {S} from '../../src/jizura/state';

// Mock engine modules
vi.mock('../../src/jizura/state', () => {
    return {
        S: {
            project: null,
            plan: null,
            audio: null,
            playing: false,
            loop: 'off',
            t: 0
        }
    };
});

describe('Editor Store', () => {
    beforeEach(() => {
        setActivePinia(createPinia());
        // Reset S state
        S.project = null;
        S.plan = null;
        S.audio = null;
        S.playing = false;
        S.loop = 'off';
        S.t = 0;
    });

    it('initializes with default state', () => {
        const editor = useEditor();
        expect(editor.booted).toBe(false);
        expect(editor.playing).toBe(false);
        expect((editor as any).t).toBeUndefined();
        expect(editor.duration).toBe(0);
        expect(editor.loopMode).toBe('all');
        expect(editor.hasAudio).toBe(false);
    });

    it('computes getters correctly when empty', () => {
        const editor = useEditor();
        expect(editor.project).toBeNull();
        expect(editor.plan).toBeNull();
        expect(editor.aspect).toBe('16:9');
    });

    it('computes getters correctly with data', () => {
        S.project = {aspect: '4:3'} as any;
        S.plan = {duration: 120} as any;
        const editor = useEditor();
        expect(editor.project).toEqual({aspect: '4:3'});
        expect(editor.plan).toEqual({duration: 120});
        expect(editor.aspect).toBe('4:3');
    });

    it('syncPlayState updates playing and loopMode', () => {
        S.playing = true;
        S.loop = 'line';
        const editor = useEditor();
        editor.syncPlayState();
        expect(editor.playing).toBe(true);
        expect(editor.loopMode).toBe('line');
    });

    it('syncTime updates duration only (播放时间走 bus/直读 S.t，不进响应式)', () => {
        S.t = 10.5;
        S.plan = {duration: 200} as any;
        const editor = useEditor();
        editor.syncTime();
        expect(editor.duration).toBe(200);
        expect((editor as any).t).toBeUndefined();
    });

    it('syncPlan updates plan related states', () => {
        S.plan = {duration: 150, lines: [1, 2], cuts: [1]} as any;
        S.audio = {bpm: 120} as any;
        const editor = useEditor();
        const prevVersion = editor.planVersion;
        editor.syncPlan();
        expect(editor.planVersion).toBe(prevVersion + 1);
        expect(editor.duration).toBe(150);
        expect(editor.linesCount).toBe(2);
        expect(editor.cutsCount).toBe(1);
        expect(editor.hasAudio).toBe(true);
        expect(editor.audioBpm).toBe(120);
    });

    it('syncAll calls all sync methods', () => {
        const editor = useEditor();
        vi.spyOn(editor, 'syncPlayState');
        vi.spyOn(editor, 'syncTime');
        vi.spyOn(editor, 'syncPlan');
        editor.syncAll();
        expect(editor.syncPlayState).toHaveBeenCalled();
        expect(editor.syncTime).toHaveBeenCalled();
        expect(editor.syncPlan).toHaveBeenCalled();
    });
});
