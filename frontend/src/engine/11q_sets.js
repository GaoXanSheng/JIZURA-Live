// 随机抽取（planner / おまかせ / シャッフル）只从首发版集合里挑。
// 追加分 = 首发版 356 parts / 12 styles 之后新增的条目，project.extra !== true 就不参与随机；
// 和風条目在 project.wa === false 时也不参与随机。单行手动指定不受这些限制。
(() => {
    'use strict';
    // 首发版自带的 pack / 风格 / 追加字体。pack 作者注意：不在这里登记的 pack 一律算追加分
    J.BASE_PACKS = ['core', undefined, 'layoutsA', 'layoutsB', 'enter', 'exitHold', 'decor', 'looks'];
    J.BASE_STYLES = ['noir', 'crimson', 'caution', 'magenta', 'paper', 'hud', 'mint', 'specimen', 'transit', 'blueprint', 'rouge', 'mono'];
    J.EXTRA_FONTS = ['reggae', 'rampart', 'potta', 'kiwi', 'klee', 'shippori'];
    // 和風条目按组登记 key，也可以直接在条目 def 上写 wa: true
    J.WA = {
        layout: ['ema', 'chochin', 'noren', 'tanzaku', 'omikuji', 'kakejiku', 'shoji', 'karuta', 'origami', 'postcard', 'letterPaper', 'genkou', 'hanko'],
        enter: ['fanOpen', 'brushReveal'],
        exit: ['fanClose'],
        decor: ['seal', 'kamon', 'seigaiha', 'asanoha', 'chochin', 'shimenawa', 'sensu', 'tsukiKumo', 'momiji', 'namiGashira', 'kasumi', 'brushStroke', 'petals'],
        bg: ['seigaiha', 'asanoha'],
        treat: ['monoGrid'],
        style: ['sakura', 'sumi'],
    };
    J.SETS = {horror: {on: false}, typo: {on: true}, kinetic: {on: true}};   // on = 默认开关，UI 文案在 12_ui.js
    J.SET_ORDER = Object.keys(J.SETS);
    // 套装在这个工程里是否开着（老工程没这个字段时用默认值）
    J.setOn = (project, set) => {
        const v = project && project[set];
        return typeof v === 'boolean' ? v : !!(J.SETS[set] && J.SETS[set].on);
    };
    const def = (g, k) => g === 'style' ? J.STYLES[k] : g === 'font' ? J.FONTS[k] : (J.registry(g) || {})[k];
// 先标套装归属；带 set 的条目不算追加分，它们有自己的开关
    for (const g of J.GROUP_KEYS) for (const k of J.order(g)) {
        const d = def(g, k);
        if (d && !d.set && J.SETS[d.pack]) d.set = d.pack;
    }
    for (const g of J.GROUP_KEYS) for (const k of J.order(g)) {
        const d = def(g, k);
        if (d && !d.set && !J.BASE_PACKS.includes(d.pack)) d.extra = true;
    }
    for (const k of J.STYLE_ORDER) if (!J.BASE_STYLES.includes(k) && !J.STYLES[k].set) J.STYLES[k].extra = true;
    for (const k of J.EXTRA_FONTS) if (J.FONTS[k]) J.FONTS[k].extra = true;
    for (const [g, keys] of Object.entries(J.WA)) for (const k of keys) {
        const d = def(g, k);
        if (d) d.wa = true;
    }

    // 随机能不能用这个条目（g 是组 key、'style' 或 'font'）；先看追加分，再看和風
    J.randomOk = (project, g, k) => {
        const d = def(g, k);
        if (!d) return false;
        if (d.extra && !(project && project.extra === true)) return false;
        if (d.wa && project && project.wa === false) return false;
        if (d.set && !J.setOn(project, d.set)) return false;
        return true;
    };
})();
