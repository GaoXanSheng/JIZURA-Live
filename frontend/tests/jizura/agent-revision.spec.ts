/* MCP revision 契约（jizura/agent-api）单元测试：
   提示词与 inputSchema 声明「写操作必须带 get_state 返回的最新 revision」，
   execTool 负责执行该契约：revision 必填工具缺参即拒（stale_revision）、
   带了但过期即拒、写操作返回值携带最新 revision。 */
import {beforeEach, describe, expect, it, vi} from 'vitest';
import {mcpRpc} from '../../src/jizura/agent-api';
import {S} from '../../src/jizura/state';

vi.hoisted(() => {
    // agent-api 的 get_state 读 document.fonts.status（jsdom 可能没有 FontFaceSet）
    if (!(document as any).fonts) {
        Object.defineProperty(document, 'fonts', {value: {status: 'loaded'}, configurable: true});
    }
});

/* 引擎本体（58k 行 JS）在纯契约测试里没必要加载——桩掉；
   replan 用真 bus 发事件，让 agent-api 的 revision 计数真实自增 */
vi.mock('../../src/engine', () => ({
    J: {
        ui: {},
        Renderer: class {},
        cutAt: () => null,
        GROUP_KEYS: [] as string[],
        STYLE_ORDER: [] as string[],
        STYLES: {},
        FONTS: {},
        registry: () => ({}),
    },
}));

vi.mock('../../src/jizura/engine', async () => {
    const {bus} = await import('../../src/jizura/bus');
    return {
        invalidateFontKey: vi.fn(),
        pause: vi.fn(),
        play: vi.fn(),
        replan: () => bus.emit('replan'),
        replanSoon: vi.fn(),
        seek: vi.fn(),
    };
});

vi.mock('../../src/jizura/look', () => ({
    clearLyrics: vi.fn(),
    commit: vi.fn(),
    edGo: vi.fn(),
    histGo: vi.fn(),
    histInfo: vi.fn(() => ({pos: '0/0', canBack: false, canFwd: false})),
    locksOf: vi.fn(() => ({tech: {}, params: {}})),
    markCutQuiet: vi.fn(),
    omakase: vi.fn(),
    rerollCurrentCut: vi.fn(),
    setCutTech: vi.fn(),
    setExportRange: vi.fn(),
    setOv: vi.fn(),
    startTap: vi.fn(),
    stopTap: vi.fn(),
    tapBack: vi.fn(),
    tapNow: vi.fn(),
}));

vi.mock('../../src/jizura/project', () => ({flushSave: vi.fn()}));
vi.mock('../../src/jizura/export', () => ({runExport: vi.fn()}));

function call(name: string, args: any): any {
    const res: any = mcpRpc({jsonrpc: '2.0', id: 1, method: 'tools/call', params: {name, arguments: args}});
    if (res.error) return {error: String(res.error.message ?? res.error)};
    return {ok: JSON.parse(res.result.content[0].text)};
}

function revisionOf(): number {
    const r = call('get_state', {detail: false});
    return r.ok.revision;
}

beforeEach(() => {
    S.project = {lyrics: '旧词', title: '', artist: '', overrides: {}, timing: {}};
    S.plan = {lines: [{src: 0}], cuts: [], duration: 10} as any;
    S.t = 0;
    S.playing = false;
    S.audio = null;
});

describe('MCP revision 契约', () => {
    it('get_state 返回当前 revision', () => {
        expect(revisionOf()).toBeGreaterThanOrEqual(0);
    });

    it('revision 必填的写工具缺参即拒（stale_revision）', () => {
        const r = call('edit_line', {line: 1, text: '新词'});
        expect(r.error).toBeDefined();
        expect(JSON.stringify(r.error)).toContain('revision');
    });

    it('过期的 revision 即拒', () => {
        const cur = revisionOf();
        const r = call('edit_line', {revision: cur + 5, line: 1, text: '新词'});
        expect(r.error).toBeDefined();
        expect(JSON.stringify(r.error)).toContain('过期');
    });

    it('携带最新 revision 的写操作成功，返回值带自增后的 revision', () => {
        const cur = revisionOf();
        const r = call('edit_line', {revision: cur, line: 1, text: '新词'});
        expect(r.error).toBeUndefined();
        expect(r.ok.line).toBe(1);
        expect(r.ok.revision).toBe(cur + 1);
        // 紧接着的旧 revision 写入会被拒（乱序写不再静默覆盖）
        const stale = call('edit_line', {revision: cur, line: 1, text: '又改'});
        expect(stale.error).toBeDefined();
    });

    it('可选 revision 的工具：缺省放行、带值则校验', () => {
        const cur = revisionOf();
        const noRev = call('tap_sync', {action: 'start', line: 1});
        expect(noRev.error).toBeUndefined();
        const badRev = call('tap_sync', {action: 'start', line: 1, revision: cur + 99});
        expect(badRev.error).toBeDefined();
    });

    it('只读工具不受 revision 契约影响', () => {
        const r = call('get_state', {detail: false});
        expect(r.error).toBeUndefined();
    });
});
