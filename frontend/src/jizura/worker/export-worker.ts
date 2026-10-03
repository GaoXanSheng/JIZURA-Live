// 导出渲染线程：逐帧渲染 + WebCodecs 编码整体搬进 Worker
// （MP4 内存块 / WebM alpha / PNG ZIP），主线程导出期间不再假死，
// 也不再和预览/引擎抢主线程预算。
// 消息协议（与 export.ts 对应）：
//   start {spec, project, beats, audio?, quality?, range?, sub?} / abort
//   回 {type:'progress', p, msg?} | {type:'done', result} | {type:'error', message}
// spec 即 export-spec.ts 的 ExportSpec（纯 JSON 可克隆），kind 只会是 mp4/webm/png——
// mp4file 需要主线程文件句柄、纯字幕层（customDrawOnly）依赖主线程字幕模块，都不进 Worker。
// project 即主线程的 S.project（纯 JSON）；plan 在这里用同一引擎重建，
// 同 seed 输出与主线程一致，也绕开 style 定义里不可克隆的成员。

import './export-worker-shims'; // 必须先于引擎：shims 提供 window/document/font 桩
import {J} from '../../engine';
import {makeSubtitleOverlay, type SubPayload} from '../../cloud/subtitle-draw';
import type {ExportSpec} from '../export-spec';

interface StartMsg {
    cmd: 'start';
    spec: ExportSpec;
    project: any;
    beats: number[];
    quality?: string;
    range?: { t0: number; t1: number } | null;
    audio?: { channels: Float32Array[]; sampleRate: number } | null;
    // 烤入字幕：主线程预解析好的每行文本 + 排版配置（见 cloud/subtitle.ts）
    sub?: SubPayload | null;
}

const post = (m: any) => (self as any).postMessage(m);

let signal = {aborted: false};

async function rebuildAudio(a: { channels: Float32Array[]; sampleRate: number }): Promise<{ buffer: AudioBuffer }> {
    // AudioBuffer 不可跨线程克隆：主线程发来各声道 Float32Array（已转移所有权），这里重建
    const buffer = new AudioBuffer({
        numberOfChannels: a.channels.length,
        length: a.channels[0].length,
        sampleRate: a.sampleRate,
    });
    for (let c = 0; c < a.channels.length; c++) buffer.copyToChannel(a.channels[c], c);
    return {buffer};
}

(self as any).onmessage = async (ev: MessageEvent) => {
    const m: any = ev.data;
    if (m.cmd === 'abort') {
        signal.aborted = true;
        return;
    }
    if (m.cmd !== 'start') return;

    signal = {aborted: false};
    const onProgress = (p: number, msg?: string) => post({type: 'progress', p, msg});
    try {
        const project = m.project;
        const spec: ExportSpec = m.spec;
        if (!spec || spec.customDrawOnly) throw new Error('この種類の書き出しは Worker に対応していません');
        const like = m.beats && m.beats.length ? {beats: m.beats} : null;
        const plan = J.plan(project, like);

        // 字体：用户字体（IDB 同源可读）→ 目录注册 → 按需加载（attachFamily 走 shims 的 CSS 管线）
        if (J.restoreUserFonts && Array.isArray(project.userFonts) && project.userFonts.length) {
            await J.restoreUserFonts(project.userFonts);
        }
        const keys = J.fontsOfPlan(plan);
        // 字幕文本（翻译/音译）常含歌词里没有的字符，一并纳入子集加载范围
        await J.ensureFonts((project.lyrics || '') + (project.title || '') + (project.artist || '')
            + (m.sub ? (m.sub.texts || []).join('') : ''), keys);
        const lost = J.missingUserFonts(keys.concat(Object.values(project.fonts || {})));
        if (lost.length) {
            throw new Error(`导入的字体（${[...new Set(lost as string[])].join('・')}）不在此浏览器中，已停止导出。请在「字体」里重新载入同一文件，或改用其它字体`);
        }

        // plan 由同一引擎按同 seed 重建，行序与主线程一致，texts 按下标对得上
        const overlay = m.sub && spec.bakeSubs ? makeSubtitleOverlay(m.sub) : null;
        let result: any;
        if (spec.kind === 'mp4' || spec.kind === 'mp4file') {
            const audio = m.audio ? await rebuildAudio(m.audio) : null;
            result = await J.exportMP4({
                plan, project, audio: spec.embedAudio ? audio : null,
                quality: m.quality || 'high',
                onProgress, signal, range: m.range,
                overlay,
            });
        } else if (spec.kind === 'webm') {
            result = await J.exportWebM({
                plan, project,
                frontOnly: spec.layer === 'front',
                transparent: spec.transparent,
                overlay,
                onProgress, signal, range: m.range,
            });
        } else {
            const blob = await J.exportPNGZip({
                plan, project,
                transparent: spec.transparent,
                layers: spec.layers,
                frontOnly: spec.layer === 'front' && !spec.layers,
                overlay,
                onProgress, signal, range: m.range,
            });
            result = {blob};
        }
        post({type: 'done', result});
    } catch (e: any) {
        post({type: 'error', message: (e && e.message) ? e.message : String(e)});
    }
};
