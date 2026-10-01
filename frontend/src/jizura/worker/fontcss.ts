/* @font-face 样式表解析（纯函数，供 worker 字体桩与单测共用）。
   worker 没有 <link> stylesheet，attachFamily 的字体清单改由这里从 CSS 文本提取：
   兼容本地 fetch-fonts.mjs 产物与 Google Fonts css2 两种格式。 */

export interface ParsedFontFace {
    family: string;
    weight: string;
    url: string;
    unicodeRange?: string;
}

export function parseFontFaces(css: string, baseUrl: string): ParsedFontFace[] {
    const out: ParsedFontFace[] = [];
    const blocks = String(css || '').match(/@font-face\s*\{[^{}]*\}/g) || [];
    for (const b of blocks) {
        const fam = /font-family\s*:\s*(?:"([^"]+)"|'([^']+)'|([^;}]+))/.exec(b);
        const src = /url\(\s*([^)]+?)\s*\)/.exec(b);
        if (!fam || !src) continue;
        const family = (fam[1] || fam[2] || fam[3] || '').trim();
        if (!family) continue;
        const wgt = /font-weight\s*:\s*([^;}]+)/.exec(b);
        const rng = /unicode-range\s*:\s*([^;}]+)/.exec(b);
        let url: string;
        try {
            url = new URL(src[1].replace(/^["']|["']$/g, ''), baseUrl).href;
        } catch {
            continue;
        }
        out.push({
            family,
            weight: (wgt ? wgt[1] : '400').trim(),
            url,
            unicodeRange: rng ? rng[1].trim() : undefined,
        });
    }
    return out;
}
