/* scrub 拖动降噪（jizura/engine beginScrub/endScrub）单元测试：
   拖动期间 seek 只挪时间不重启 AudioBufferSource（每次重启 = 一次可闻爆音
   + 一个废弃节点），pointerup 时若拖动前在播放则从当前位置恢复出声。 */
import {beforeEach, describe, expect, it, vi} from 'vitest';
import {J} from '../../src/engine';
import {AP, S} from '../../src/jizura/state';
import {beginScrub, endScrub, seek} from '../../src/jizura/engine';

/* cutAt 在 plan 桩上不可用（无 cuts 结构），桩掉——updateCutInfo 只需要 null */
vi.spyOn(J, 'cutAt').mockReturnValue(null);

const playSpy = vi.spyOn(AP, 'play').mockImplementation(() => {});
const stopSpy = vi.spyOn(AP, 'stop').mockImplementation(() => {});

const buffer = {duration: 100} as unknown as AudioBuffer;

beforeEach(() => {
    playSpy.mockClear();
    stopSpy.mockClear();
    S.plan = {duration: 100, cuts: []} as any;
    S.audio = {buffer, duration: 100, beats: []} as any;
    S.playing = false;
    S.scrubbing = false;
    S.loop = 'all';
    S.tap = null;
    S.t = 0;
});

describe('scrub 拖动降噪', () => {
    it('播放中拖动：beginScrub 停声，期间 seek 不重启音频，endScrub 恢复', () => {
        S.playing = true;
        seek(5);
        expect(playSpy).toHaveBeenCalledTimes(1);

        beginScrub();
        expect(stopSpy).toHaveBeenCalledTimes(1);
        seek(6);
        seek(7);
        seek(8);
        expect(playSpy).toHaveBeenCalledTimes(1);   // 不再每次重启
        expect(S.t).toBe(8);

        endScrub();
        expect(playSpy).toHaveBeenCalledTimes(2);
        expect(playSpy).toHaveBeenLastCalledWith(buffer, 8, 1);
        expect(S.scrubbing).toBe(false);
    });

    it('暂停中拖动：全程不触碰音频', () => {
        beginScrub();
        seek(20);
        endScrub();
        expect(playSpy).not.toHaveBeenCalled();
        expect(stopSpy).not.toHaveBeenCalled();
        expect(S.t).toBe(20);
    });

    it('未 begin 直接 endScrub 是安全空操作', () => {
        endScrub();
        expect(S.scrubbing).toBe(false);
        expect(playSpy).not.toHaveBeenCalled();
    });

    it('重复 beginScrub 幂等', () => {
        S.playing = true;
        beginScrub();
        beginScrub();
        expect(stopSpy).toHaveBeenCalledTimes(1);
        endScrub();
        expect(S.scrubbing).toBe(false);
    });
});
