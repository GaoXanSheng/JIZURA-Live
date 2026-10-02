/* 选段 LRC 导出（jizura/look.saveLrc）单元测试：
   导出范围按切分存，端点/范围内的标题卡与间奏切分 line=-1（无词行），
   直接取会以 lines[-1] 炸掉 J.lrcText——修复后取范围内有词行的最小/最大行号。 */
import {beforeEach, describe, expect, it, vi} from 'vitest';
import {J} from '../../src/engine';
import {bus} from '../../src/jizura/bus';
import {S} from '../../src/jizura/state';
import {saveLrc} from '../../src/jizura/look';

const saveFile = vi.spyOn(J, 'saveFile').mockImplementation(() => {});
const busEmit = vi.spyOn(bus, 'emit');

beforeEach(() => {
    saveFile.mockClear();
    busEmit.mockClear();
    S.project = Object.assign(J.defaultProject(), {
        lyrics: '第一行\n第二行',
        title: '测试曲',
        artist: '歌手',
        exportRange: null,
    });
    S.plan = {lines: [], cuts: [], duration: 10} as any;
});

describe('saveLrc 切分范围换行号', () => {
    it('端点是标题卡/间奏切分（line=-1）时不再崩溃', () => {
        S.plan.cuts = [{line: -1}, {line: 0}, {line: 1}] as any;   // 范围 [0..1]：首切分无词
        S.project.exportRange = {from: 0, to: 1};
        saveLrc();
        expect(saveFile).toHaveBeenCalledTimes(1);
        const [filename, blob] = saveFile.mock.calls[0];
        expect(String(filename)).toContain('.lrc');
        expect(blob).toBeInstanceOf(Blob);
    });

    it('范围内有词行号取最小/最大（间奏夹在中间不影响）', () => {
        S.plan.cuts = [{line: 0}, {line: -1}, {line: 1}] as any;
        S.project.exportRange = {from: 0, to: 2};
        saveLrc();
        expect(saveFile).toHaveBeenCalledTimes(1);
    });

    it('范围内全部是无词行：提示并放弃导出', () => {
        S.plan.cuts = [{line: -1}, {line: -1}] as any;
        S.project.exportRange = {from: 0, to: 1};
        saveLrc();
        expect(saveFile).not.toHaveBeenCalled();
        expect(busEmit).toHaveBeenCalledWith('toast', '所选范围内没有可导出的歌词行', undefined);
    });

    it('未选导出范围时导出整首', () => {
        saveLrc();
        expect(saveFile).toHaveBeenCalledTimes(1);
    });
});
