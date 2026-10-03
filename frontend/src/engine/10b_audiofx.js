/* 音频特效系统 v2：效果器架（多效果串联）+ 预设包 + 变速。
   每个「效果单元」= 自描述 schema（UI 自动出滑杆）+ build(ctx, p, t) 子图构建器；
   链路 input → [单元1 干/湿] → [单元2 …] → output。变速（speed）不走单元，
   由播放/导出两端按倍率缩放时长——歌词/节拍按歌曲原时刻存，天然同步。
   硬约束：同一构建器同时喂预览实时 AudioContext 和导出 OfflineAudioContext，
   全部只用原生节点 + 振荡器接 AudioParam 的调制（离线渲染确定）；需要噪声的
   （混响 IR）用种子随机——预览与导出逐样本一致。 */

(() => {
    'use strict';

    J.AUDIO_EQ_BANDS = [60, 150, 400, 1000, 2400, 6000, 15000];

    const clamp = (v, a, b, d) => (typeof v === 'number' && isFinite(v)) ? Math.min(b, Math.max(a, v)) : d;

    // 种子随机（mulberry32）：混响 IR 等需要噪声的地方保证逐次一致
    function seeded(seed) {
        let a = seed >>> 0;
        return () => {
            a |= 0;
            a = a + 0x6D2B79F5 | 0;
            let t = Math.imul(a ^ a >>> 15, 1 | a);
            t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t;
            return ((t ^ t >>> 14) >>> 0) / 4294967296;
        };
    }

    // 程序化脉冲响应：指数衰减白噪声 + 单极点低通阻尼。无外部文件，种子固定
    function makeIR(ctx, {size = 2.2, damp = 0.5, seed = 20261004} = {}) {
        const rate = ctx.sampleRate, len = Math.max(1, Math.floor(rate * size));
        const buf = ctx.createBuffer(2, len, rate);
        const rnd = seeded(seed), cut = 4000 + 9000 * (1 - clamp(damp, 0, 1, 0.5));
        const a = Math.exp(-2 * Math.PI * cut / rate), k = 1 - a;
        for (let ch = 0; ch < 2; ch++) {
            const d = buf.getChannelData(ch);
            let lp = 0;
            for (let i = 0; i < len; i++) {
                const n = rnd() * 2 - 1;
                lp += k * (n - lp);                       // 一极低通做阻尼
                d[i] = lp * Math.pow(1 - i / len, 1.8 + damp);  // 衰减包络
            }
        }
        return buf;
    }

    // LFO：三角波振荡器接 AudioParam 做调制，实时/离线同样成立，无每帧 JS 开销
    function lfo(ctx, rate, depth, param, anchor = 0, phase = 0) {
        const r = clamp(rate, 0.01, 20, 0.5);
        const o = ctx.createOscillator();
        o.type = 'triangle';
        o.frequency.value = r;
        const g = ctx.createGain();
        g.gain.value = depth;
        o.connect(g).connect(param);
        o.start(anchor, phase / r);
        return o;
    }

    /* ---------- 效果单元注册表 ---------- */
    J.AFX_UNITS = {
        eq: {
            label: 'EQ 均衡', note: '7 段 peaking，拖把手调音色',
            params: [{key: 'gains', type: 'eq'}],
            defaults: {gains: [0, 0, 0, 0, 0, 0, 0]},
            build(ctx, p) {
                const input = ctx.createGain(), output = ctx.createGain();
                let node = input;
                for (let i = 0; i < J.AUDIO_EQ_BANDS.length; i++) {
                    const f = ctx.createBiquadFilter();
                    f.type = 'peaking';
                    f.frequency.value = J.AUDIO_EQ_BANDS[i];
                    f.Q.value = 1;
                    f.gain.value = clamp(p.gains[i], -12, 12, 0);
                    node.connect(f);
                    node = f;
                }
                node.connect(output);
                return {input, output};
            },
        },
        pan: {
            label: '环绕音', note: '立体声左右摆动',
            params: [{key: 'rate', label: '速度', min: 0.02, max: 2, step: 0.005, def: 0.125},
                     {key: 'depth', label: '强度', min: 0, max: 1, step: 0.01, def: 0.7}],
            defaults: {rate: 0.125, depth: 0.7},
            build(ctx, p, t) {
                const input = ctx.createGain(), output = ctx.createGain();
                const pan = ctx.createStereoPanner();
                input.connect(pan).connect(output);
                lfo(ctx, p.rate, clamp(p.depth, 0, 1, 0.7), pan.pan, t);
                return {input, output};
            },
        },
        spatial: {
            label: '3D 丽音', note: '左右声道 HRTF 定位（∓40°）做空间展宽，建议戴耳机',
            params: [{key: 'depth', label: '强度', min: 0, max: 1, step: 0.01, def: 0.7}],
            defaults: {depth: 0.7},
            build(ctx, p) {
                const input = ctx.createGain(), output = ctx.createGain();
                const splitter = ctx.createChannelSplitter(2);
                input.connect(splitter);
                const r = 1.5 + clamp(p.depth, 0, 1, 0.7), deg = d => d * Math.PI / 180;
                [[0, -40], [1, 40]].forEach(([ch, az]) => {
                    const pan = ctx.createPanner();
                    pan.panningModel = 'HRTF';
                    pan.positionX.value = Math.sin(deg(az)) * r;
                    pan.positionZ.value = -Math.cos(deg(az)) * r;
                    splitter.connect(pan, ch, 0);
                    pan.connect(output);
                });
                return {input, output};
            },
        },
        orbit: {
            label: '8D 旋转', note: 'HRTF 声源绕听者转圈，建议戴耳机',
            params: [{key: 'rate', label: '速度', min: 0.02, max: 2, step: 0.005, def: 0.125},
                     {key: 'depth', label: '半径', min: 0, max: 1, step: 0.01, def: 0.7}],
            defaults: {rate: 0.125, depth: 0.7},
            build(ctx, p, t) {
                const input = ctx.createGain(), output = ctx.createGain();
                const pan = ctx.createPanner();
                pan.panningModel = 'HRTF';
                const r = 1.2 + clamp(p.depth, 0, 1, 0.7) * 1.8;
                const rate = clamp(p.rate, 0.02, 2, 0.125);
                input.connect(pan).connect(output);
                const sx = ctx.createOscillator(), sz = ctx.createOscillator();
                sx.type = sz.type = 'sine';
                sx.frequency.value = sz.frequency.value = rate;
                const gx = ctx.createGain(), gz = ctx.createGain();
                gx.gain.value = gz.gain.value = r;
                sx.connect(gx).connect(pan.positionX);
                sz.connect(gz).connect(pan.positionZ);
                sx.start(t);
                sz.start(t, 0.25 / rate);   // 相位差 90°（周期四分之一）
                return {input, output};
            },
        },
        reverb: {
            label: '混响', note: '程序化房间脉冲响应（无外部文件），种子固定保证导出一致',
            params: [{key: 'size', label: '空间', min: 0.3, max: 6, step: 0.1, def: 2.2, unit: '秒'},
                     {key: 'damp', label: '阻尼', min: 0, max: 1, step: 0.01, def: 0.5},
                     {key: 'wet', label: '湿度', min: 0, max: 1, step: 0.01, def: 0.25}],
            defaults: {size: 2.2, damp: 0.5, wet: 0.25},
            build(ctx, p) {
                const input = ctx.createGain(), output = ctx.createGain();
                const conv = ctx.createConvolver();
                conv.buffer = makeIR(ctx, {size: p.size, damp: p.damp});
                const wet = ctx.createGain();
                wet.gain.value = clamp(p.wet, 0, 1, 0.25);
                const tone = ctx.createBiquadFilter();
                tone.type = 'lowpass';
                tone.frequency.value = 800 + 10000 * (1 - clamp(p.damp, 0, 1, 0.5));
                const dry = ctx.createGain();
                dry.gain.value = 1;
                input.connect(dry).connect(output);
                input.connect(conv).connect(tone).connect(wet).connect(output);
                return {input, output};
            },
        },
        echo: {
            label: '回声', note: '延迟 + 反馈环，低通定回声音色',
            params: [{key: 'time', label: '延迟', min: 0.05, max: 1, step: 0.01, def: 0.28, unit: '秒'},
                     {key: 'feedback', label: '反馈', min: 0, max: 0.85, step: 0.01, def: 0.35},
                     {key: 'tone', label: '音色', min: 500, max: 12000, step: 50, def: 4500, unit: 'Hz'},
                     {key: 'wet', label: '湿度', min: 0, max: 1, step: 0.01, def: 0.35}],
            defaults: {time: 0.28, feedback: 0.35, tone: 4500, wet: 0.35},
            build(ctx, p) {
                const input = ctx.createGain(), output = ctx.createGain();
                const delay = ctx.createDelay(2);
                delay.delayTime.value = clamp(p.time, 0.05, 1, 0.28);
                const fb = ctx.createGain();
                fb.gain.value = clamp(p.feedback, 0, 0.85, 0.35);
                const tone = ctx.createBiquadFilter();
                tone.type = 'lowpass';
                tone.frequency.value = clamp(p.tone, 500, 12000, 4500);
                const wet = ctx.createGain();
                wet.gain.value = clamp(p.wet, 0, 1, 0.35);
                input.connect(output);
                input.connect(delay);
                delay.connect(tone).connect(fb).connect(delay);   // 反馈环
                tone.connect(wet).connect(output);
                return {input, output};
            },
        },
        vocalcut: {
            label: '伴唱消除', note: '中置声道抵消（居中人声衰减留伴奏），低频回补保住贝斯；对单声道音频无效',
            params: [{key: 'bass', label: '低频回补', min: 0, max: 1, step: 0.01, def: 0.6}],
            defaults: {bass: 0.6},
            build(ctx, p) {
                const input = ctx.createGain(), output = ctx.createGain();
                // L-R 相消：右路反相后与左路合并，居中成分抵消
                const splitter = ctx.createChannelSplitter(2);
                const inv = ctx.createGain();
                inv.gain.value = -1;
                const merger = ctx.createChannelMerger(2);
                input.connect(splitter);
                splitter.connect(merger, 0, 0);
                splitter.connect(inv, 1);
                inv.connect(merger, 0, 0);
                inv.connect(merger, 0, 1);
                merger.connect(output);
                // 低频回补：原信号低通后按比例混回，贝斯/底鼓不受相消影响
                const bl = clamp(p.bass, 0, 1, 0.6);
                if (bl > 0) {
                    const lowpass = ctx.createBiquadFilter();
                    lowpass.type = 'lowpass';
                    lowpass.frequency.value = 180;
                    const back = ctx.createGain();
                    back.gain.value = bl;
                    input.connect(lowpass).connect(back).connect(output);
                }
                return {input, output};
            },
        },
        chorus: {
            label: '合唱', note: '双路反相调制延迟，轻微音高晃动与立体声展宽',
            params: [{key: 'rate', label: '速度', min: 0.1, max: 5, step: 0.05, def: 1.2, unit: 'Hz'},
                     {key: 'depth', label: '深度', min: 0, max: 1, step: 0.01, def: 0.5},
                     {key: 'wet', label: '湿度', min: 0, max: 1, step: 0.01, def: 0.5}],
            defaults: {rate: 1.2, depth: 0.5, wet: 0.5},
            build(ctx, p, t) {
                const input = ctx.createGain(), output = ctx.createGain();
                const wet = ctx.createGain();
                wet.gain.value = clamp(p.wet, 0, 1, 0.5);
                const dry = ctx.createGain();
                dry.gain.value = 1;
                input.connect(dry).connect(output);
                for (const [phase, pan] of [[0, -0.6], [0.5, 0.6]]) {   // 左右反相调制
                    const delay = ctx.createDelay(0.05);
                    delay.delayTime.value = 0.015;
                    lfo(ctx, p.rate, 0.0005 + 0.004 * clamp(p.depth, 0, 1, 0.5), delay.delayTime, t, phase);
                    const w = ctx.createStereoPanner();
                    w.pan.value = pan;
                    input.connect(delay).connect(w).connect(wet);
                }
                wet.connect(output);
                return {input, output};
            },
        },
        bass: {
            label: '低音增强', note: '低频架式提升',
            params: [{key: 'gain', label: '提升', min: 0, max: 12, step: 0.5, def: 4, unit: 'dB'},
                     {key: 'freq', label: '频点', min: 60, max: 250, step: 5, def: 120, unit: 'Hz'}],
            defaults: {gain: 4, freq: 120},
            build(ctx, p) {
                const input = ctx.createGain(), output = ctx.createGain();
                const f = ctx.createBiquadFilter();
                f.type = 'lowshelf';
                f.frequency.value = clamp(p.freq, 60, 250, 120);
                f.gain.value = clamp(p.gain, 0, 12, 4);
                input.connect(f).connect(output);
                return {input, output};
            },
        },
        telephone: {
            label: '电话音', note: '窄带通 + 轻度失真，老电台/话筒质感',
            params: [{key: 'drive', label: '失真', min: 0, max: 1, step: 0.01, def: 0.3}],
            defaults: {drive: 0.3},
            build(ctx, p) {
                const input = ctx.createGain(), output = ctx.createGain();
                const hp = ctx.createBiquadFilter();
                hp.type = 'highpass';
                hp.frequency.value = 400;
                const bp = ctx.createBiquadFilter();
                bp.type = 'bandpass';
                bp.frequency.value = 1500;
                bp.Q.value = 0.8;
                const lp = ctx.createBiquadFilter();
                lp.type = 'lowpass';
                lp.frequency.value = 3200;
                const shaper = ctx.createWaveShaper();
                const k = 1 + clamp(p.drive, 0, 1, 0.3) * 24;
                const curve = new Float32Array(257);
                for (let i = 0; i < 257; i++) {
                    const x = i / 128 - 1;
                    curve[i] = Math.tanh(k * x) / Math.tanh(k);
                }
                shaper.curve = curve;
                input.connect(hp).connect(bp).connect(shaper).connect(lp).connect(output);
                return {input, output};
            },
        },
        mono: {
            label: '单声道', note: '左右合并；修相位问题/配人声消除用',
            params: [],
            defaults: {},
            build(ctx) {
                const input = ctx.createGain(), output = ctx.createGain();
                const merger = ctx.createChannelMerger(2);
                input.connect(merger, 0, 0);
                input.connect(merger, 0, 1);
                merger.connect(output);
                return {input, output};
            },
        },
        pitch: {
            label: '音调', note: '整体移调（不改速度，实验性）；双延迟粒度式，极端值有轻微金属感',
            params: [{key: 'semitones', label: '移调', min: -12, max: 12, step: 1, def: 0, unit: '半音', type: 'int'}],
            defaults: {semitones: 0},
            build(ctx, p, t) {
                const n = Math.round(clamp(p.semitones, -12, 12, 0));
                const input = ctx.createGain(), output = ctx.createGain();
                if (!n) {
                    input.connect(output);
                    return {input, output};
                }
                // 双路延迟 + 反相三角调制：读头周期性回绕做粒度重采样（经典 WebAudio 移调）
                const ratio = Math.pow(2, -n / 12);   // 延迟读速比 = 音高比
                const rate = 4, base = 0.06;
                const wet = ctx.createGain();
                wet.gain.value = 0.5;
                input.connect(output);   // 干声保底，移调声叠加
                for (const half of [0, 0.5]) {
                    const delay = ctx.createDelay(0.5);
                    delay.delayTime.value = base;
                    lfo(ctx, rate, base * (1 - ratio), delay.delayTime, t, half);
                    const w = ctx.createGain();
                    w.gain.value = 0.5;
                    input.connect(delay).connect(w).connect(wet);
                }
                wet.connect(output);
                return {input, output};
            },
        },
        limiter: {
            label: '限幅器', note: '推动电平 + 硬限幅，链尾防削波',
            params: [{key: 'gain', label: '推动', min: 0, max: 12, step: 0.5, def: 0, unit: 'dB'}],
            defaults: {gain: 0},
            build(ctx, p) {
                const input = ctx.createGain(), output = ctx.createGain();
                input.gain.value = Math.pow(10, clamp(p.gain, 0, 12, 0) / 20);
                const comp = ctx.createDynamicsCompressor();
                comp.threshold.value = -1;
                comp.knee.value = 0;
                comp.ratio.value = 20;
                comp.attack.value = 0.003;
                comp.release.value = 0.12;
                input.connect(comp).connect(output);
                return {input, output};
            },
        },
    };

    /* ---------- 预设包：一键写整条链 ---------- */
    const U = (id, params = {}, on = true) => ({id, on, params: Object.assign({}, J.AFX_UNITS[id].defaults, params)});
    J.AFX_PRESETS = [
        {id: 'clean', label: '原声', cfg: {speed: 1, units: []}},
        {id: 'hall', label: '音乐厅', cfg: {speed: 1, units: [U('reverb', {size: 2.8, wet: 0.3})]}},
        {id: 'room', label: '小房间', cfg: {speed: 1, units: [U('reverb', {size: 0.9, wet: 0.18})]}},
        {id: 'live', label: '现场演出', cfg: {speed: 1, units: [U('echo', {time: 0.22, feedback: 0.25, wet: 0.25}), U('reverb', {size: 1.8, wet: 0.22})]}},
        {id: 'wide', label: '宽阔声场', cfg: {speed: 1, units: [U('spatial'), U('chorus', {depth: 0.3, wet: 0.3})]}},
        {id: '8d', label: '8D 环绕', cfg: {speed: 1, units: [U('orbit')]}},
        {id: 'karaoke', label: '伴唱模式', cfg: {speed: 1, units: [U('vocalcut'), U('limiter', {gain: 3})]}},
        {id: 'nightcore', label: '夜核加速', cfg: {speed: 1.25, units: [U('bass', {gain: 3})]}},
        {id: 'slow', label: '慢速抒情', cfg: {speed: 0.85, units: [U('reverb', {size: 3.5, wet: 0.3})]}},
        {id: 'radio', label: '复古电台', cfg: {speed: 1, units: [U('telephone'), U('limiter', {gain: 6})]}},
    ];

    /* ---------- 配置清洗 + v1 旧版迁移 ---------- */
    // v2 模型：{speed, units:[{id,on,params}]}；v1 模型 {preset, rate, depth, eq}
    // 就地映射成等价链，老工程无缝升级
    J.sanitizeAudioFx = (fx) => {
        const o = fx && typeof fx === 'object' ? fx : {};
        const out = {speed: clamp(o.speed, 0.5, 2, 1), units: []};
        const seen = {};
        // EQ 增益三种来源：v2 单元 params.gains / 历史数组形态 params / v1 的 eq 字段
        const readEq = (raw) => {
            const arr = Array.isArray(raw) ? raw
                : Array.isArray(raw && raw.params) ? raw.params
                    : raw && raw.params && raw.params.gains ? raw.params.gains
                        : raw && Array.isArray(raw.eq) ? raw.eq : null;
            return J.AUDIO_EQ_BANDS.map((hz0, i) => clamp(arr ? arr[i] : 0, -12, 12, 0));
        };
        if (Array.isArray(o.units)) {
            for (const u of o.units) {
                const def = u && J.AFX_UNITS[u.id];
                if (!def || seen[u.id]) continue;   // 未知/重复单元丢弃
                seen[u.id] = true;
                const params = {};
                for (const schema of def.params) {
                    if (schema.type === 'eq') params.gains = readEq(u);
                    else params[schema.key] = clamp(u.params && u.params[schema.key], schema.min, schema.max, schema.def);
                }
                out.units.push({id: u.id, on: u.on !== false, params});
            }
        } else if (o.preset && o.preset !== 'none') {
            // v1 → v2：单预设搬进链
            if (o.preset === 'pan') out.units.push(U('pan', {rate: o.rate, depth: o.depth}));
            if (o.preset === 'spatial') out.units.push(U('spatial', {depth: o.depth}));
            if (o.preset === 'orbit') out.units.push(U('orbit', {rate: o.rate, depth: o.depth}));
            if (o.preset === 'eq') out.units.push({id: 'eq', on: true, params: {gains: readEq(o)}});
        }
        return out;
    };

    // 变速倍率：播放/导出两端的时长缩放（歌词/节拍仍按歌曲原时刻）
    J.fxSpeed = (project) => {
        const fx = project && project.audioFx;
        return clamp(fx && fx.speed, 0.5, 2, 1);
    };

    /* ---------- 链式构建：预览与导出共用 ---------- */
    // 与 v1 同签名：返回 {input, output, channels}；空链返回 null（不建图直通）
    J.buildAudioFx = (ctx, cfg, opts = {}) => {
        const fx = J.sanitizeAudioFx(cfg);
        if (!fx.units.length) return null;
        const anchor = opts.anchorT || 0;
        const input = ctx.createGain(), output = ctx.createGain();
        let node = input;
        for (const u of fx.units) {
            if (!u.on) continue;
            const unit = J.AFX_UNITS[u.id].build(ctx, u.params, anchor);
            node.connect(unit.input);
            node = unit.output;
        }
        node.connect(output);
        return {input, output, channels: 2};
    };
})();
