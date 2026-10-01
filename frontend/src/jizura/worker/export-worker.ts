/* 导出渲染线程：MP4（内存块）/ PNG ZIP 的逐帧渲染 + WebCodecs 编码整体搬进 Worker，
   主线程导出期间不再假死，也不再和预览/引擎抢主线程预算。
   契约（与 export.ts 的消息协议）：
     → {cmd:'start', kind:'mp4'|'png'|'pnga'|'pngl', project, beats, audio?, quality?, range?}
     → {cmd:'abort'}
     ← {type:'progress', p, msg?} | {type:'done', result} | {type:'error', message}
   project 即主线程的 S.project（纯 JSON）；plan 由本线程用同一引擎重建——
   同 seed 确定性输出与主线程一致，且规避 style 定义里不可克隆的成员。 */

import './export-worker-shims'; // 必须先于引擎：shims 提供 window/document/font 桩
import {J} from '../../engine';

interface StartMsg {
    cmd: 'start';
    kind: 'mp4' | 'png' | 'pnga' | 'pngl';
    project: any;
    beats: number[];
    quality?: string;
    range?: { t0: number; t1: number } | null;
    audio?: { channels: Float32Array[]; sampleRate: number } | null;
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
        const like = m.beats && m.beats.length ? {beats: m.beats} : null;
        const plan = J.plan(project, like);

        // 字体：用户字体（IDB 同源可读）→ 目录注册 → 按需加载（attachFamily 走 shims 的 CSS 管线）
        if (J.restoreUserFonts && Array.isArray(project.userFonts) && project.userFonts.length) {
            await J.restoreUserFonts(project.userFonts);
        }
        const keys = J.fontsOfPlan(plan);
        await J.ensureFonts((project.lyrics || '') + (project.title || '') + (project.artist || ''), keys);
        const lost = J.missingUserFonts(keys.concat(Object.values(project.fonts || {})));
        if (lost.length) {
            throw new Error(`导入的字体（${[...new Set(lost as string[])].join('・')}）不在此浏览器中，已停止导出。请在「字体」里重新载入同一文件，或改用其它字体`);
        }

        let result: any;
        if (m.kind === 'mp4') {
            const audio = m.audio ? await rebuildAudio(m.audio) : null;
            result = await J.exportMP4({
                plan, project, audio,
                quality: m.quality || 'high',
                onProgress, signal, range: m.range,
            });
        } else {
            const blob = await J.exportPNGZip({
                plan, project,
                transparent: m.kind === 'pnga',
                layers: m.kind === 'pngl',
                onProgress, signal, range: m.range,
            });
            result = {blob};
        }
        post({type: 'done', result});
    } catch (e: any) {
        post({type: 'error', message: (e && e.message) ? e.message : String(e)});
    }
};
