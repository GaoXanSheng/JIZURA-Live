// 新表现组的注册表；src/11p_*.js 各表现包都通过 J.register 往里注册
(() => {
    'use strict';

    J.TREAT = {
        none: {
            name: 'なし', apply() {
            }
        }
    };
    J.TREAT_ORDER = ['none'];
    J.BG = {
        none: {
            name: '無地', draw() {
            }
        }
    };
    J.BG_ORDER = ['none'];
    J.CAMERA = {
        push: {
            name: 'ゆっくり寄る', tags: ['calm', 'editorial', 'emotional', 'graphic', 'pop', 'glitch'], w: 5,
            get: (env) => ({s: 1 + 0.03 * (env.fx.motion ?? 0.7) * J.clamp(env.lt / Math.max(0.3, env.cut.dur))})
        },
    };
    J.CAMERA_ORDER = ['push'];
// 事件后期 / 转场效果；没写 draw() 的条目走渲染器内置分支
    J.FXE = {
        slice: {name: 'スライスグリッチ', builtin: true},
        block: {name: 'ブロックグリッチ', builtin: true},
        invert: {name: '反転', builtin: true},
        flash: {name: 'フラッシュ', builtin: true},
        zoom: {name: 'ズームブラー', builtin: true},
        mosaic: {name: 'モザイク', builtin: true},
        shake: {name: '揺れ', builtin: true},
        chroma: {name: '色ズレの跳ね', builtin: true},
    };
    J.FXE_ORDER = ['chroma', 'shake', 'slice', 'block', 'invert', 'flash', 'zoom', 'mosaic'];
// 切到切的转场：draw(ctx, A, B, p, info) 在设备像素下把上一刀画面 A 和本刀 B 合成
    J.TRANS = {};
    J.TRANS_ORDER = [];

    // group key → [注册表, 顺序数组]；layout~decor 每个切分取一项，bg 按行取整屏背景，treat 是文字加工，cam 是切分上的镜头运动
    const GROUPS = {
        layout: ['LAYOUTS', 'LAYOUT_ORDER'],
        enter: ['ENTER', 'ENTER_ORDER'],
        hold: ['HOLD', 'HOLD_ORDER'],
        exit: ['EXIT', 'EXIT_ORDER'],
        decor: ['DECOR', 'DECOR_ORDER'],
        treat: ['TREAT', 'TREAT_ORDER'],
        bg: ['BG', 'BG_ORDER'],
        cam: ['CAMERA', 'CAMERA_ORDER'],
        fx: ['FXE', 'FXE_ORDER'],
        trans: ['TRANS', 'TRANS_ORDER'],
    };
    J.GROUP_KEYS = Object.keys(GROUPS);
    J.registry = g => J[GROUPS[g][0]];
    J.order = g => J[GROUPS[g][1]];

    // J.register('layout', 'myKey', { name: '…', … }, 'packName')
    // 条目通用字段：name 日文标签必填；tags 是适配的氛围 key（glitch calm pop graphic editorial emotional）；w 抽取权重默认 1；pack 由这里写入
    J.register = (group, key, def, pack) => {
        const G = GROUPS[group];
        if (!G) throw new Error('unknown group ' + group);
        if (!def || !def.name) throw new Error(`${group}.${key}: name is required`);
        const reg = J[G[0]], order = J[G[1]];
        if (reg[key] && reg[key].pack !== pack) console.warn(`JIZURA: ${group}.${key} is being replaced`);
        def.pack = pack || def.pack || 'core';
        reg[key] = def;
        if (!def.special && !order.includes(key)) order.push(key);
        return def;
    };

    // tags 含该氛围 key 的条目，おまかせ 用
    J.taggedWith = (group, mood) => J.order(group).filter(k => {
        const d = J.registry(group)[k];
        return d && d.tags && d.tags.includes(mood);
    });
})();
