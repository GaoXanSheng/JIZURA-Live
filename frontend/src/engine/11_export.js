/* ============================================================
   JIZURA — export: MP4 (WebCodecs + mp4-muxer), WebM with alpha
   (mediabunny), PNG sequence ZIP, file saving (artifact download
   capability or plain browser download)
   ============================================================ */
import {BufferTarget, canEncodeVideo, CanvasSource, Output, Quality, WebMOutputFormat} from 'mediabunny';

(() => {
    'use strict';

    /* ---------- saving ---------- */
    J.saveFile = async (filename, data) => {
        const blob = data instanceof Blob ? data : new Blob([data]);
        try {
            if (window.claude && typeof window.claude.use === 'function') {
                const dl = await window.claude.use('downloads');
                if (dl) {
                    await dl.save({filename, data: blob});
                    return 'saved';
                }
            }
        } catch (e) {
            if (e && e.code === 'declined') return 'declined';
            if (e && e.code && e.code !== 'unavailable' && e.code !== 'not_granted') throw e;
        }
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 30000);
        return 'saved';
    };

    /* ---------- codec negotiation ---------- */
    const VIDEO_CANDS = [
        {codec: 'avc1.640034', mux: 'avc', label: 'H.264 High'},
        {codec: 'avc1.640033', mux: 'avc', label: 'H.264 High'},
        {codec: 'avc1.4d0033', mux: 'avc', label: 'H.264 Main'},
        {codec: 'avc1.42003e', mux: 'avc', label: 'H.264 Baseline'},
        {codec: 'vp09.00.51.08', mux: 'vp9', label: 'VP9'},
        {codec: 'av01.0.12M.08', mux: 'av1', label: 'AV1'},
    ];
// hardware encoders often refuse very high bitrates (52 Mbps+ for 1080p60 at 最高) — keep them in a range they accept
    J.videoBitrate = (w, h, fps, quality) => {
        const want = w * h * fps * (quality === 'max' ? 0.42 : quality === 'high' ? 0.28 : 0.16);
        const px = w * h, cap = px <= 2.2e6 ? 40e6 : px <= 3.8e6 ? 60e6 : 90e6;
        return Math.round(Math.min(want, cap));
    };
    const vcfg = (c, w, h, fps, bitrate, hw) => {
        const cfg = {codec: c.codec, width: w, height: h, bitrate, framerate: fps};
        if (hw) cfg.hardwareAcceleration = hw;
        if (c.mux === 'avc') cfg.avc = {format: 'avc'};
        return cfg;
    };

    async function supported(cfg) {
        try {
            const s = await VideoEncoder.isConfigSupported(cfg);
            return !!(s && s.supported);
        } catch (e) {
            return false;
        }
    }

    const codecMemo = new Map();   // the answer never changes for a page load; asking the browser again costs ~100 ms
    J.pickVideoCodec = (w, h, fps, bitrate) => {
        const key = [w, h, fps, bitrate].join('/');
        if (!codecMemo.has(key)) codecMemo.set(key, (async () => {
            if (typeof VideoEncoder === 'undefined') return null;
            for (const c of VIDEO_CANDS) {
                const cfg = vcfg(c, w, h, fps, bitrate);
                if (await supported(cfg)) return Object.assign({}, c, {cfg});
            }
            return null;
        })());
        return codecMemo.get(key).then(vc => vc && Object.assign({}, vc, {cfg: Object.assign({}, vc.cfg)}));
    };
    /* the encoders to try, best first: the browser's choice, then the same codec in software (GPU encoders are the usual
       reason an export fails every time on one PC), then a simpler profile / lower bitrate in software, then VP9 */
    J.videoAttempts = async (w, h, fps, bitrate) => {
        if (typeof VideoEncoder === 'undefined') return [];
        const out = [], seen = new Set();
        const add = async (c, hw, br) => {
            const key = c.codec + '|' + (hw || '') + '|' + br;
            if (seen.has(key) || out.length >= 5) return;
            const cfg = vcfg(c, w, h, fps, br, hw);
            if (await supported(cfg)) {
                seen.add(key);
                out.push(Object.assign({}, c, {cfg, hw: hw || 'auto'}));
            }
        };
        let first = null;
        for (const c of VIDEO_CANDS) {
            const cfg = vcfg(c, w, h, fps, bitrate);
            if (await supported(cfg)) {
                first = c;
                break;
            }
        }
        if (first) {
            await add(first, null, bitrate);
            await add(first, 'prefer-software', bitrate);
        }
        const avc = VIDEO_CANDS.filter(c => c.mux === 'avc' && c !== first);
        for (const c of avc) {
            await add(c, 'prefer-software', Math.round(bitrate * 0.7));
            if (out.length >= 3) break;
        }
        for (const c of VIDEO_CANDS.filter(c => c.mux !== 'avc')) await add(c, 'prefer-software', Math.round(bitrate * 0.7));
        return out;
    };
    J.pickAudioCodec = async (chn) => {
        if (typeof AudioEncoder === 'undefined') return null;
        for (const c of [{codec: 'mp4a.40.2', mux: 'aac', sr: 48000}, {codec: 'opus', mux: 'opus', sr: 48000}]) {
            try {
                const s = await AudioEncoder.isConfigSupported({
                    codec: c.codec,
                    sampleRate: c.sr,
                    numberOfChannels: chn,
                    bitrate: chn >= 6 ? 448000 : 192000
                });
                if (s.supported) return c;
            } catch (e) {
            }
        }
        return null;
    };

    /* ---------- 音频特效（预览与导出共用一套图） ----------
       预览：AP 在实时 AudioContext 里按 project.audioFx 建图；
       导出：resample 在 OfflineAudioContext 里用同一构建器离线渲染。
       LFO 一律用 OscillatorNode 接 AudioParam（不用 rAF 推参数）：
       离线渲染同样成立 ⇒ 导出完全确定，预览零每帧 JS 开销。
       sanitize / 曲线采样 / 扬声器增益是纯函数，可脱离音频栈测试。 */
    /* EQ 频段（Hz）：7 段 peaking，增益 -12..+12 dB */
    J.AUDIO_EQ_BANDS = [60, 150, 400, 1000, 2400, 6000, 15000];

    J.sanitizeAudioFx = (fx) => {
        const PRESETS = ['none', 'pan', 'spatial', 'orbit', 'eq'];
        const o = fx && typeof fx === 'object' ? fx : {};
        const clamp = (v, a, b, d) => (typeof v === 'number' && isFinite(v)) ? Math.min(b, Math.max(a, v)) : d;
        const eq = J.AUDIO_EQ_BANDS.map((hz, i) => {
            const v = Array.isArray(o.eq) ? o.eq[i] : 0;
            return clamp(v, -12, 12, 0);
        });
        return {
            preset: PRESETS.includes(o.preset) ? o.preset : 'none',
            rate: clamp(o.rate, 0.02, 2, 0.125),    // 摆动/旋转速度 Hz
            depth: clamp(o.depth, 0, 1, 0.7),        // 强度 0..1
            eq                                        // 各频段 dB 增益
        };
    };

    /* 构建特效图（任意 BaseAudioContext：实时 AudioContext 或 OfflineAudioContext）。
       返回 { input, output, channels } 或 null（preset=none）。 */
    J.buildAudioFx = (ctx, cfg, opts = {}) => {
        const fx = J.sanitizeAudioFx(cfg);
        if (fx.preset === 'none') return null;
        const input = ctx.createGain(), output = ctx.createGain();
        const st = opts.anchorT || 0;
        const deg = d => d * Math.PI / 180;

        if (fx.preset === 'pan') {
            /* 环绕音：立体声左右摆动 */
            const panner = ctx.createStereoPanner();
            const lfo = ctx.createOscillator();
            lfo.frequency.value = fx.rate;
            const amt = ctx.createGain();
            amt.gain.value = fx.depth;
            lfo.connect(amt).connect(panner.pan);
            input.connect(panner).connect(output);
            lfo.start(st);
        } else if (fx.preset === 'spatial') {
            /* 3D 丽音：左右声道各自 HRTF 定位（∓40°）做空间展宽。
               两个 panner 的立体声输出直接汇入 output——节点相加按声道对齐，
               不能过 ChannelMerger（每路会被下混成单声道，抹掉双耳线索） */
            const splitter = ctx.createChannelSplitter(2);
            input.connect(splitter);
            const r = 1.5 + fx.depth;
            [[0, -40], [1, 40]].forEach(([ch, az]) => {
                const p = ctx.createPanner();
                p.panningModel = 'HRTF';
                p.positionX.value = Math.sin(deg(az)) * r;
                p.positionZ.value = -Math.cos(deg(az)) * r;
                splitter.connect(p, ch, 0);
                p.connect(output);
            });
        } else if (fx.preset === 'orbit') {
            /* 3D 旋转（8D）：HRTF 声源绕听者转圈，sin/cos 双 LFO（相位差 90°）驱动坐标 */
            const p = ctx.createPanner();
            p.panningModel = 'HRTF';
            const r = 1.2 + fx.depth * 1.8;
            const sx = ctx.createOscillator(), sz = ctx.createOscillator();
            sx.frequency.value = fx.rate;
            sz.frequency.value = fx.rate;
            const gx = ctx.createGain(), gz = ctx.createGain();
            gx.gain.value = r;
            gz.gain.value = r;
            sx.connect(gx).connect(p.positionX);
            sz.connect(gz).connect(p.positionZ);
            input.connect(p).connect(output);
            sx.start(st);
            sz.start(st + 1 / (4 * fx.rate));   // 相位差 90°
        } else if (fx.preset === 'eq') {
            /* 自定义 EQ：7 段 peaking 串联（60Hz〜15kHz，各 -12..+12dB）。
               串联 peaking 相邻频段影响轻微（Q=1），足够「自定义音色」用 */
            let node = input;
            for (let i = 0; i < J.AUDIO_EQ_BANDS.length; i++) {
                const f = ctx.createBiquadFilter();
                f.type = 'peaking';
                f.frequency.value = J.AUDIO_EQ_BANDS[i];
                f.Q.value = 1;
                f.gain.value = fx.eq[i];
                node.connect(f);
                node = f;
            }
            node.connect(output);
        }

        return {input, output, channels: 2};
    };

    async function resample(buffer, sr, duration, offset = 0, fx = null) {
        // 特效图与预览共用 J.buildAudioFx 构建（同一构建器 ⇒ 预览与导出听感一致）
        const chn = Math.min(2, buffer.numberOfChannels);
        const len = Math.ceil(duration * sr);
        const oc = new OfflineAudioContext(chn, len, sr);
        const src = oc.createBufferSource();
        src.buffer = buffer;
        const graph = fx ? J.buildAudioFx(oc, fx, {songDur: buffer.duration, fromT: offset, toT: offset + duration}) : null;
        if (graph) {
            src.connect(graph.input);
            graph.output.connect(oc.destination);
        } else {
            src.connect(oc.destination);
        }
        src.start(0, Math.max(0, offset));
        return oc.startRendering();
    }

    /* part of the song to export: range = { t0, t1 } in seconds (選んだ行だけ), default the whole plan */
    J.exportSpan = (plan, range) => {
        const t0 = range ? Math.max(0, range.t0) : 0, t1 = range ? Math.min(plan.duration, range.t1) : plan.duration;
        return {t0, dur: Math.max(1 / plan.fps, t1 - t0)};
    };

    /* ---------- LRC（带时间戳歌词） ----------
       每行一个开始时刻：手工设定 > LRC 标签 > 估算；歌词按书写的行输出（/ * ! | 与间奏行保留）。
       选了行范围时只输出那些行，时刻从导出视频的开头起算。 */
    J.lrcText = (project, lines, range) => {
        const parsed = J.parseLyrics(project.lyrics);
        const tm = J.computeTiming(project, parsed, null);
        const rows = String(project.lyrics || '').replace(/\r/g, '').split('\n');
        const stamp = t => {
            t = Math.max(0, t);
            const m = Math.floor(t / 60), s = t - m * 60;
            return `[${String(m).padStart(2, '0')}:${s.toFixed(2).padStart(5, '0')}]`;
        };
        const clean = r => String(r || '').trim().replace(/^(\[\d+:\d+(?:[.:]\d+)?\])+/, '').trim();
        const out = [];
        const ti = project.title || parsed.meta.ti, ar = project.artist || parsed.meta.ar;
        if (ti) out.push(`[ti:${ti}]`);
        if (ar) out.push(`[ar:${ar}]`);
        if (parsed.meta.al) out.push(`[al:${parsed.meta.al}]`);
        const from = range ? range.from : 0, to = range ? range.to : parsed.lines.length - 1, t0 = range ? range.t0 : 0;
        for (let i = from; i <= to && i < parsed.lines.length; i++) {
            const L = parsed.lines[i];
            let txt = clean(rows[L.src]);
            if (L.interlude && !/^\[/.test(txt)) txt = '[间奏]';
            out.push(stamp(tm.starts[i] - t0) + txt);
        }
        return out.join('\n') + '\n';
    };

    /* ---------- MP4 ---------- */

    /* The MP4 is written as it is encoded (mp4-muxer, moov at the end) instead of being assembled in one huge
       ArrayBuffer: into many small memory blocks (file: null), or straight into a file the user picked
       (file: a FileSystemWritableFileStream, the "large video" button). A long 1080p / 1440p song used to need one contiguous
       buffer of several hundred MB, doubled on finalize, which is what made those exports fail. */
    class BlockStore {                    // positioned writes into a list of blocks → Blob (no single giant buffer)
        constructor() {
            this.blocks = [];
            this.end = 0;
        }

        write(data, pos) {
            let u = data instanceof Uint8Array ? data : new Uint8Array(data);
            if (pos > this.end) {
                this.blocks.push({pos: this.end, u: new Uint8Array(pos - this.end)});
                this.end = pos;
            }
            // overwrite what already exists (mp4-muxer patches the mdat size at finalize)
            for (const b of this.blocks) {
                if (pos >= this.end || !u.length) break;
                const s0 = Math.max(pos, b.pos), s1 = Math.min(pos + u.length, b.pos + b.u.length);
                if (s1 > s0) b.u.set(u.subarray(s0 - pos, s1 - pos), s0 - b.pos);
            }
            if (pos + u.length > this.end) {
                const from = Math.max(0, this.end - pos);
                this.blocks.push({pos: this.end, u: u.slice(from)});
                this.end = pos + u.length;
            }
        }

        blob(type) {
            return new Blob(this.blocks.map(b => b.u), {type});
        }
    }

    J.exportMP4 = async (o) => {
        const {plan, project, audio, quality = 'high', onProgress, signal, range, file = null} = o;
        const [w, h] = J.outputSize(project);
        const fps = plan.fps, bitrate = J.videoBitrate(w, h, fps, quality);
        const attempts = await J.videoAttempts(w, h, fps, bitrate);
        if (!attempts.length) throw new Error('このブラウザは動画エンコード（WebCodecs）に対応していません。Chrome か Edge の最新版で開いてください。');
        const tried = [];
        let fileDone = false;   // encodeMP4 成功路径已 close；失败路径由这里 abort，别把用户选中的文件句柄挂死
        try {
            for (let k = 0; k < attempts.length; k++) {
                const vc = attempts[k];
                try {
                    if (file && k > 0) {
                        await file.seek(0);
                        await file.truncate(0);
                    }
                    const r = await encodeMP4(Object.assign({}, o, {
                        w,
                        h,
                        vc,
                        note: k > 0 ? `（${vc.label}・ソフトウェアで再試行 ${k}）` : ''
                    }));
                    fileDone = true;
                    r.tried = tried;
                    return r;
                } catch (e) {
                    if (signal && signal.aborted) throw new Error('キャンセルしました');
                    if (e && e.jzFatal) throw e;
                    tried.push(`${vc.label}/${vc.hw}: ${e && e.message ? e.message : e}`);
                    console.warn('MP4 export attempt failed', vc.codec, vc.hw, e);
                }
            }
            const err = new Error('MP4 を書き出せませんでした。' + (file ? '' : '「大きな動画用（ファイルに直接保存）」か、') + '解像度・fps・画質を下げて試してください。詳細：' + tried.join(' ／ '));
            err.detail = tried;
            throw err;
        } finally {
            if (file && !fileDone) {
                try {
                    await file.abort();
                } catch (e) {
                    try {
                        await file.close();
                    } catch {
                    }
                }
            }
        }
    };

    async function encodeMP4({plan, project, audio, onProgress, signal, range, file, w, h, vc, note}) {
        const span = J.exportSpan(plan, range);
        const fps = plan.fps;
        let ac = null, audioChn = 0;
        // 音频特效（project.audioFx）：特效图在导出时于 OfflineAudioContext 里渲染
        const fx = audio && audio.buffer && project.includeAudio !== false ? J.sanitizeAudioFx(project.audioFx) : null;
        if (fx) {
            audioChn = Math.min(2, audio.buffer.numberOfChannels);
            ac = await J.pickAudioCodec(audioChn);
        }
        const store = file ? null : new BlockStore();
        const target = file ? new Mp4Muxer.FileSystemWritableFileStreamTarget(file, {chunkSize: 8 * 1048576})
            : new Mp4Muxer.StreamTarget({
                onData: (data, pos) => store.write(data, pos),
                chunked: true,
                chunkSize: 8 * 1048576
            });
        const muxOpts = {
            target,
            video: {codec: vc.mux, width: w, height: h, frameRate: fps},
            fastStart: false,
            firstTimestampBehavior: 'offset'
        };
        if (ac) muxOpts.audio = {
            codec: ac.mux,
            numberOfChannels: audioChn,
            sampleRate: ac.sr
        };
        const muxer = new Mp4Muxer.Muxer(muxOpts);
        let err = null, outFrames = 0;
        const venc = new VideoEncoder({
            output: (chunk, meta) => {
                outFrames++;
                try {
                    muxer.addVideoChunk(chunk, meta);
                } catch (e) {
                    err = e;
                }
            }, error: e => {
                err = e;
            }
        });
        venc.configure(Object.assign({}, vc.cfg, {latencyMode: 'quality'}));
        const canvas = document.createElement('canvas');
        canvas.width = w;
        canvas.height = h;
        const ctx = canvas.getContext('2d', {alpha: false});
        const R = new J.Renderer();
        const total = Math.max(1, Math.round(span.dur * fps));
        const scale = w / plan.W;
        const prevRes = J.glyphs.maxRes;
        J.glyphs.maxRes = h >= 1000 ? 768 : 512;
        const closeEnc = () => {
            try {
                if (venc.state !== 'closed') venc.close();
            } catch (e) {
            }
        };
        try {
            for (let i = 0; i < total; i++) {
                if (signal && signal.aborted) {
                    closeEnc();
                    throw new Error('キャンセルしました');
                }
                if (err) throw err;
                if (venc.state === 'closed') throw new Error('エンコーダーが停止しました');
                R.frame(ctx, plan, span.t0 + i / fps, {scale});
                const vf = new VideoFrame(canvas, {
                    timestamp: Math.round(i * 1e6 / fps),
                    duration: Math.round(1e6 / fps)
                });
                try {
                    venc.encode(vf, {keyFrame: i % (fps * 2) === 0});
                } finally {
                    vf.close();
                }
                let spins = 0;
                // (time in the background doesn't count: a phone pauses the encoder while the page is hidden)
                while (venc.encodeQueueSize > 4 && !err) {
                    await new Promise(r => setTimeout(r, 2));
                    if (!document.hidden && ++spins > 15000) throw new Error('エンコーダーが応答しません');
                }
                // an encoder that accepts frames but never returns any has failed silently (seen with some GPU drivers)
                if (i === Math.min(total - 1, fps * 3) && outFrames === 0) {
                    await venc.flush();
                    if (!outFrames) throw new Error('エンコーダーが出力を返しません');
                }
                if (i % 3 === 0) {
                    onProgress && onProgress(i / total, `フレーム ${i + 1}/${total}${note || ''}`);
                    await new Promise(r => setTimeout(r, 0));
                }
            }
            await venc.flush();
            if (err) throw err;
        } catch (e) {
            closeEnc();
            throw e;
        } finally {
            J.glyphs.maxRes = prevRes;
        }
        closeEnc();
        if (outFrames < total * 0.98) throw new Error(`動画のフレームが足りません（${outFrames}/${total}）`);
        if (ac) {
            onProgress && onProgress(0.99, '音声をエンコード中');
            const rs = await resample(audio.buffer, ac.sr, span.dur, span.t0, fx && fx.preset !== 'none' ? fx : null);
            const chn = rs.numberOfChannels;
            let aChunks = 0, aEnd = 0, aErr = null;
            const aenc = new AudioEncoder({
                output: (chunk, meta) => {
                    aChunks++;
                    aEnd = Math.max(aEnd, chunk.timestamp + (chunk.duration || 0));
                    muxer.addAudioChunk(chunk, meta);
                }, error: e => {
                    aErr = e;
                }
            });
            aenc.configure({codec: ac.codec, sampleRate: ac.sr, numberOfChannels: chn, bitrate: 192000});
            const frames = rs.length, block = 4800;
            for (let off = 0; off < frames; off += block) {
                if (aErr) break;
                const n = Math.min(block, frames - off);
                const data = new Float32Array(n * chn);
                for (let c = 0; c < chn; c++) data.set(rs.getChannelData(c).subarray(off, off + n), c * n);
                const ad = new AudioData({
                    format: 'f32-planar',
                    sampleRate: ac.sr,
                    numberOfFrames: n,
                    numberOfChannels: chn,
                    timestamp: Math.round(off * 1e6 / ac.sr),
                    data
                });
                aenc.encode(ad);
                ad.close();
                if (aenc.encodeQueueSize > 16) await new Promise(r => setTimeout(r, 1));
            }
            await aenc.flush();
            aenc.close();
            const fatal = m => {
                const e = new Error(m);
                e.jzFatal = true;
                return e;
            };    // audio problems: another video encoder won't help
            if (aErr) throw fatal('音声のエンコードに失敗しました: ' + (aErr.message || aErr));
            // the encoder must have produced the whole soundtrack — otherwise report it instead of writing a silent file
            if (!aChunks || aEnd < (Math.min(span.dur, audio.buffer.duration - span.t0) - 0.5) * 1e6) throw fatal('音声のエンコードが途中で止まりました（' + aChunks + '）。もう一度書き出してください');
        }
        onProgress && onProgress(0.995, 'ファイルを仕上げ中');
        muxer.finalize();
        if (file) await file.close();
        onProgress && onProgress(1, '完了');
        const size = file ? null : store.end;
        return {
            blob: file ? null : store.blob('video/mp4'),
            size,
            codec: vc.label + (vc.hw === 'prefer-software' ? '（ソフトウェア）' : ''),
            audio: ac ? ac.mux : null,
            audioWanted: !!(audio && audio.buffer && project.includeAudio !== false),
            width: w,
            height: h,
            toFile: !!file
        };
    }

    /* ---------- PNG sequence as ZIP (store, no compression) ---------- */
    const ZIP_TOO_BIG = 'ZIP 会变得过大（上限 65,535 个文件・4GB）。请缩小导出范围或降低分辨率';
    const CRC = (() => {
        const t = new Uint32Array(256);
        for (let n = 0; n < 256; n++) {
            let c = n;
            for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
            t[n] = c >>> 0;
        }
        return t;
    })();
    const crc32 = (u8) => {
        let c = 0xffffffff;
        for (let i = 0; i < u8.length; i++) c = CRC[(c ^ u8[i]) & 255] ^ (c >>> 8);
        return (c ^ 0xffffffff) >>> 0;
    };

    class ZipWriter {
        constructor() {
            this.parts = [];
            this.central = [];
            this.offset = 0;
        }

        add(name, u8) {
            const nb = new TextEncoder().encode(name);
            // 普通 ZIP（无 ZIP64）：最多 65,535 个文件、4GB
            if (this.central.length / 2 >= 0xffff) throw new Error(ZIP_TOO_BIG);
            if (this.offset + 30 + nb.length + u8.length > 0xffffffff) throw new Error(ZIP_TOO_BIG);
            const crc = crc32(u8);
            const lh = new DataView(new ArrayBuffer(30));
            lh.setUint32(0, 0x04034b50, true);
            lh.setUint16(4, 20, true);
            lh.setUint16(6, 0x0800, true);
            lh.setUint16(8, 0, true);
            lh.setUint16(10, 0, true);
            lh.setUint16(12, 0x21, true);
            lh.setUint32(14, crc, true);
            lh.setUint32(18, u8.length, true);
            lh.setUint32(22, u8.length, true);
            lh.setUint16(26, nb.length, true);
            lh.setUint16(28, 0, true);
            this.parts.push(lh.buffer, nb, u8);
            const ch = new DataView(new ArrayBuffer(46));
            ch.setUint32(0, 0x02014b50, true);
            ch.setUint16(4, 20, true);
            ch.setUint16(6, 20, true);
            ch.setUint16(8, 0x0800, true);
            ch.setUint16(10, 0, true);
            ch.setUint16(12, 0, true);
            ch.setUint16(14, 0x21, true);
            ch.setUint32(16, crc, true);
            ch.setUint32(20, u8.length, true);
            ch.setUint32(24, u8.length, true);
            ch.setUint16(28, nb.length, true);
            ch.setUint32(42, this.offset, true);
            this.central.push(ch.buffer, nb);
            this.offset += 30 + nb.length + u8.length;
        }

        finish() {
            const cdSize = this.central.reduce((s, p) => s + (p.byteLength ?? p.length), 0);
            const n = this.central.length / 2;
            if (this.offset + cdSize > 0xffffffff) throw new Error(ZIP_TOO_BIG);
            const end = new DataView(new ArrayBuffer(22));
            end.setUint32(0, 0x06054b50, true);
            end.setUint16(8, n, true);
            end.setUint16(10, n, true);
            end.setUint32(12, cdSize, true);
            end.setUint32(16, this.offset, true);
            return new Blob([...this.parts, ...this.central, end.buffer], {type: 'application/zip'});
        }
    }

    /* layers: transparent PNGs in two folders — back/ (background graphic + decorations behind the lyrics) and front/
       (lyrics, their decorations, ghosts, HUD). Screen effects are applied to both, so stacking front over back matches.
       frontOnly: single front/ folder without HUD (foreground/lyrics-layer export; HUD is a composite element, not part of the lyrics). */
    J.exportPNGZip = async ({plan, project, transparent, layers, frontOnly, onProgress, signal, every = 1, range}) => {
        const span = J.exportSpan(plan, range);
        const [w, h] = J.outputSize(project);
        const canvas = document.createElement('canvas');
        canvas.width = w;
        canvas.height = h;
        const ctx = canvas.getContext('2d');
        const R = new J.Renderer();
        const fps = plan.fps, total = Math.max(1, Math.round(span.dur * fps));
        const nFiles = Math.ceil(total / every) * (layers ? 2 : 1);
        if (nFiles > 0xffff) throw new Error(ZIP_TOO_BIG);        // 渲染开始前就报，而不是一小时之后
        const zip = new ZipWriter();
        const scale = w / plan.W;
        // 与 MP4 路径同口径：高分辨率导出提升字形分解上限（否则 1080p+ 的 PNG 比 MP4 模糊）
        const prevRes = J.glyphs.maxRes;
        J.glyphs.maxRes = h >= 1000 ? 768 : 512;
        try {
            for (let i = 0; i < total; i += every) {
                if (signal && signal.aborted) throw new Error('キャンセルしました');
                const name = `jizura_${String(i).padStart(5, '0')}.png`;
                const modes = layers ? ['back', 'front'] : frontOnly ? ['front'] : [null];
                for (const layer of modes) {
                    R.frame(ctx, plan, span.t0 + i / fps, {scale, transparent: transparent || !!layers || !!frontOnly, layer, noHud: !!frontOnly});
                    const blob = await new Promise(r => canvas.toBlob(r, 'image/png'));
                    zip.add((layer ? layer + '/' : '') + name, new Uint8Array(await blob.arrayBuffer()));
                }
                onProgress && onProgress(i / total, `PNG ${i + 1}/${total}`);
            }
        } finally {
            J.glyphs.maxRes = prevRes;
        }
        onProgress && onProgress(1, '完了');
        return zip.finish();
    };

    /* ---------- transparent video (WebM, VP9/VP8 alpha side data via mediabunny) ---------- */
    /* frontOnly: lyrics + front decorations only, no HUD — the foreground layer for stacking over other footage.
       customDraw: (ctx, plan, t, opt) => void — replaces the standard frame render entirely
       (subtitle-layer export draws only the 字幕渲染 overlay through it: no lyrics/decor/background).
       No audio track: an overlay has nothing to carry. */
    J.exportWebM = async ({plan, project, onProgress, signal, range, frontOnly, customDraw}) => {
        const span = J.exportSpan(plan, range);
        const [w, h] = J.outputSize(project);
        const fps = plan.fps;
        const bitrate = J.videoBitrate(w, h, fps, project.quality || 'high');
        let codec = null;
        for (const c of ['vp9', 'vp8']) {
            try {
                if (await canEncodeVideo(c, {width: w, height: h, bitrate})) {
                    codec = c;
                    break;
                }
            } catch (e) {
            }
        }
        if (!codec) throw new Error('このブラウザはVP9/VP8エンコードに対応していません。Chrome か Edge の最新版で開くか、透明 PNG（ZIP）をご利用ください。');
        const output = new Output({format: new WebMOutputFormat(), target: new BufferTarget()});
        const canvas = document.createElement('canvas');
        canvas.width = w;
        canvas.height = h;
        const ctx = canvas.getContext('2d');   // 默认带 alpha：透明底画帧
        const source = new CanvasSource(canvas, {
            codec,
            alpha: 'keep',
            quality: new Quality({bitrate}),
            latencyMode: 'quality',
            keyFrameInterval: 2,
        });
        output.addVideoTrack(source, {frameRate: fps});
        await output.start();
        const R = new J.Renderer();
        const total = Math.max(1, Math.round(span.dur * fps));
        const scale = w / plan.W;
        const prevRes = J.glyphs.maxRes;
        J.glyphs.maxRes = h >= 1000 ? 768 : 512;
        let cancelled = false;
        try {
            for (let i = 0; i < total; i++) {
                if (signal && signal.aborted) {
                    cancelled = true;
                    throw new Error('キャンセルしました');
                }
                if (customDraw) customDraw(ctx, plan, span.t0 + i / fps, {scale});
                else R.frame(ctx, plan, span.t0 + i / fps, {scale, transparent: true, layer: frontOnly ? 'front' : null, noHud: !!frontOnly});
                await source.add(i / fps, 1 / fps);   // await 即背压：编码器过载时挂起
                if (i % 3 === 0) {
                    onProgress && onProgress(i / total, `WebM ${i + 1}/${total}`);
                    await new Promise(r => setTimeout(r, 0));
                }
            }
            onProgress && onProgress(0.995, 'ファイルを仕上げ中');
            await output.finalize();
        } catch (e) {
            if (cancelled) {
                try {
                    await output.cancel();
                } catch (e2) {
                }
            }
            throw e;
        } finally {
            J.glyphs.maxRes = prevRes;
        }
        onProgress && onProgress(1, '完了');
        return {
            blob: new Blob([output.target.buffer], {type: 'video/webm'}),
            codec: codec.toUpperCase() + ' alpha',
            width: w,
            height: h,
        };
    };

    /* ---------- plan JSON for the After Effects panel ---------- */
    /* The After Effects panel implements the original expression set. Newer pack entries are exported as their
       closest original counterpart (the browser key is kept in web* fields so nothing is lost). */
    J.AE_MAP = {
        layout: {
            lowerThird: 'center',
            corners: 'mixed',
            staircase: 'mixed',
            zigzag: 'wave',
            arcTop: 'ring',
            spiral: 'ring',
            gridCells: 'labels',
            dropCap: 'mixed',
            justified: 'tile',
            frameBox: 'center',
            bubble: 'pill',
            subtitleBar: 'center',
            ticker: 'marquee',
            splitScreen: 'diag',
            mirror: 'stack',
            sideways: 'vcols',
            edgeFrame: 'marquee',
            perspective: 'stack',
            hanko: 'vcols',
            genkou: 'vcols',
            panels: 'diag',
            filmstrip: 'labels',
            quote: 'center',
            ruler: 'gloss',
            searchBar: 'type',
            chat: 'labels',
            notification: 'pill',
            ticket: 'pill',
            rain: 'tile',
            hanging: 'scatter',
            orbit: 'ring',
            tunnel: 'tile',
            wordCloud: 'scatter',
            bounceLine: 'mixed',
            elastic: 'condensed',
            crossBands: 'diag',
            stickerBomb: 'labels',
            neon: 'center',
            keycaps: 'labels',
            bubbles: 'scatter',
            slotMachine: 'labels',
            flipBoard: 'labels',
            credits: 'type',
            zoomRepeat: 'stack',
            splitHalves: 'stack',
            columnsBig: 'vcols',
            circleWords: 'ring',
            dotMatrix: 'type',
            depthStack: 'stack',
            typeSpecimen: 'stack',
            kanjiFocus: 'huge',
            halfVertical: 'vcols',
            curtain: 'center',
            equalizer: 'mixed',
            tape: 'diag'
        },
        enter: {
            riseMask: 'drop',
            dropMask: 'drop',
            slideL: 'wipe',
            slideR: 'wipe',
            slideWhole: 'stretch',
            flipX: 'spin',
            flipY: 'spin',
            domino: 'spin',
            fold: 'pop',
            unroll: 'wipe',
            strokeDraw: 'assemble',
            outlineFill: 'blur',
            splitJoin: 'slice',
            vSlice: 'slice',
            shutter: 'wipe',
            iris: 'zoom',
            diagWipe: 'wipe',
            blinds: 'slice',
            checker: 'flicker',
            randomOrder: 'flicker',
            bounceBig: 'drop',
            squashDrop: 'drop',
            rubber: 'stretch',
            glitchIn: 'scramble',
            echoIn: 'zoom',
            whip: 'stretch',
            skewIn: 'stretch',
            trackIn: 'blur',
            trackOut: 'blur',
            blurStagger: 'blur',
            fadeStagger: 'blur',
            waveIn: 'pop',
            spiralIn: 'spin',
            zoomOut: 'zoom',
            resolve: 'scramble',
            magnet: 'assemble',
            inkBleed: 'blur',
            neonOn: 'flicker',
            cursorSweep: 'type',
            stamp: 'zoom'
        },
        exit: {
            sinkMask: 'fall',
            riseOut: 'drift',
            slideOutL: 'stretch',
            slideOutR: 'stretch',
            flipOutX: 'shrink',
            flipOutY: 'fall',
            foldOut: 'shrink',
            squash: 'shrink',
            trackOutWide: 'blur',
            collapse: 'shrink',
            zoomThrough: 'blur',
            zoomFar: 'shrink',
            spinOut: 'scatter',
            twist: 'shrink',
            waveOut: 'scatter',
            blurOutStagger: 'blur',
            undraw: 'blur',
            outlineOut: 'blur',
            irisClose: 'shrink',
            diagWipeOut: 'wipe',
            blindsClose: 'slice',
            checkerOut: 'glitch',
            splitApart: 'slice',
            vSliceDrop: 'fall',
            melt: 'fall',
            dissolve: 'drift',
            backspace: 'wipe',
            scrambleOut: 'glitch',
            glitchDissolve: 'glitch',
            echoOut: 'blur',
            whipOut: 'stretch',
            gravity: 'fall',
            popOut: 'scatter',
            burn: 'drift',
            sweepCover: 'wipe',
            shatterLite: 'explode'
        },
        hold: {
            float: 'drift',
            sway: 'wave',
            pulse: 'breathe',
            shimmer: 'still',
            colorRun: 'still',
            rotateSlow: 'drift',
            trackBreathe: 'breathe',
            skewWobble: 'wave',
            beatHop: 'wave',
            hWave: 'wave',
            heartbeat: 'breathe',
            orbitSmall: 'jitter',
            jelly: 'breathe',
            scanBand: 'glitchtick',
            noiseDrift: 'drift',
            tilt: 'drift',
            zoomSlow: 'drift',
            stretchPulse: 'breathe',
            glitchJump: 'glitchtick',
            echoTrail: 'drift'
        },
        decor: {
            crosshair: 'brackets',
            cropMarks: 'brackets',
            reticle: 'rings',
            radar: 'rings',
            progressRing: 'rings',
            timecodeBar: 'barcode',
            rulerEdge: 'grid',
            dimension: 'leaders',
            indexNum: 'counter',
            dateStamp: 'barcode',
            qrBlock: 'barcode',
            glitchRects: 'bars',
            concentricSquares: 'shapes',
            triangleSpin: 'shapes',
            lineBurst: 'sparks',
            plusGrid: 'grid',
            guides: 'grid',
            waveLine: 'waveform',
            spiralLine: 'rings',
            halftonePatch: 'shapes',
            checkerStrip: 'stripes',
            beatRing: 'rings',
            orbitDots: 'dots',
            constellation: 'sparks',
            confetti: 'shapes',
            petals: 'shapes',
            rainStreaks: 'slash',
            snow: 'dots',
            lightLeak: 'blobs',
            bokeh: 'blobs',
            speedCorner: 'slash',
            risingParticles: 'sparks',
            twinkle: 'sparks',
            brushStroke: 'bars',
            tapePieces: 'bars',
            scribbleCircle: 'rings',
            scribbleUnder: 'slash',
            crossOut: 'slash',
            highlightMark: 'bars',
            heartsStars: 'shapes',
            watermarkKanji: 'counter',
            verticalStrip: 'leaders',
            romajiLine: 'leaders',
            bracketsJP: 'brackets',
            seal: 'shapes'
        },
        fx: {
            rgbSplit: 'chroma',
            smear: 'slice',
            vhsRoll: 'slice',
            trackingNoise: 'slice',
            waveWarp: 'slice',
            pixelDrift: 'slice',
            tileShift: 'block',
            gridRepeat: 'block',
            mirrorFlash: 'block',
            strobe: 'invert',
            blackFrame: 'invert',
            whiteFrame: 'flash',
            filmBurn: 'flash',
            lightSweep: 'flash',
            panelWipe: 'flash',
            zoomPunch: 'zoom',
            whipBlur: 'zoom',
            posterize: 'mosaic',
            hueShift: 'chroma',
            irisTrans: 'zoom',
            doors: 'slice',
            blindsTrans: 'slice',
            splitSlide: 'slice',
            crtOff: 'flash'
        },
    };
// The plan goes to the After Effects panel as-is (version 2): the panel builds every key it implements and
// picks the closest counterpart itself (from the exported metadata / J.AE_MAP) for anything it lacks.
    J.planForAE = (plan, project, range) => {
        const clean = JSON.parse(JSON.stringify(plan, (k, v) => (k === 'energy' || k === 'buffer' || k === 'peaks' || k === 'pitch' ? undefined : v)));
        clean.version = 2;
        clean.aeMap = J.AE_MAP;   // 新增包条目 → 原版 counterpart 的对照表：面板对未实现的 key 按它取最近似条目
        clean.width = J.outputSize(project)[0];
        clean.height = J.outputSize(project)[1];
        clean.extra = project.extra === true;
        clean.wa = project.wa !== false;
        if (J.setOn) for (const s of J.SET_ORDER) clean[s] = J.setOn(project, s);
        clean.fonts = {};
        for (const [role, keys] of Object.entries(plan.style.fonts)) clean.fonts[role] = keys.map(k => J.FONTS[k] ? J.FONTS[k].label : k);
        clean.fontTable = Object.fromEntries(Object.entries(J.FONTS).map(([k, f]) => [k, {
            label: f.label,
            family: f.family.replace(/"/g, ''),
            weight: f.weight,
            kind: f.kind
        }]));
        // lyric language: the face each key is drawn with in the browser for this plan (the panel maps keys → AE fonts per language)
        clean.lang = plan.lang || 'ja';
        if (J.setLang && J.faceOf && clean.lang !== 'ja') {
            J.setLang(clean.lang);
            for (const k of Object.keys(clean.fontTable)) {
                const f = J.faceOf(k);
                clean.fontTable[k].langFamily = f.family.replace(/"/g, '');
                clean.fontTable[k].langWeight = f.weight;
            }
        }
        // モーフ has no After Effects counterpart yet: build it as the closest transition (an ink-blob dissolve)
        for (const c of clean.cuts || []) if (c.morph && !c.trans) {
            c.trans = 'inkBlob';
            c.transDur = c.morph.dur;
            c.transP = {};
            c.webMorph = true;
        }
        // 行の範囲だけ: keep the cuts / events inside [t0, t1] and move them to start at 0.
        // audioOffset tells the AE panel to slide the song layer left by t0 so it stays in sync.
        if (range) {
            const sp = J.exportSpan(plan, range), t0 = sp.t0, t1 = t0 + sp.dur, eps = 1e-3;
            const sh = o => {
                o.start -= t0;
                o.end -= t0;
                return o;
            };
            clean.cuts = clean.cuts.filter(c => c.end > t0 + eps && c.start < t1 - eps).map(c => {
                if (c.companion) sh(c.companion);
                return sh(c);
            });
            clean.events = (clean.events || []).filter(e => e.t >= t0 - 1 && e.t < t1).map(e => Object.assign(e, {t: e.t - t0}));
            for (const l of clean.lines || []) {
                sh(l);
                if (l.visEnd != null) l.visEnd -= t0;
            }   // all lines stay (cut.line indexes them)
            clean.duration = sp.dur;
            clean.audioOffset = t0;
            clean.range = {t0, t1};
        }
        return clean;
    };
})();
