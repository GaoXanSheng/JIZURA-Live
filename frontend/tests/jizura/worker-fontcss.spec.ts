import {describe, expect, it} from 'vitest';
import {parseFontFaces} from '../../src/jizura/worker/fontcss';

describe('parseFontFaces（worker 字体桩的 @font-face 解析）', () => {
    const BASE = 'http://127.0.0.1:8642/fonts/css/';

    it('解析本地 fetch-fonts 产物（带引号 family + unicode-range）', () => {
        const css = `/* comment */
@font-face {
  font-family: "Noto Sans JP";
  font-style: normal;
  font-weight: 100 900;
  font-display: swap;
  src: url(../files/noto-sans-jp.woff2) format("woff2");
  unicode-range: U+0-FF, U+3000-30FF;
}`;
        const faces = parseFontFaces(css, BASE);
        expect(faces).toHaveLength(1);
        expect(faces[0].family).toBe('Noto Sans JP');
        expect(faces[0].weight).toBe('100 900');
        expect(faces[0].url).toBe('http://127.0.0.1:8642/fonts/files/noto-sans-jp.woff2');
        expect(faces[0].unicodeRange).toBe('U+0-FF, U+3000-30FF');
    });

    it('解析 Google css2 格式（不带引号 family、百分号编码 URL）', () => {
        const css = `@font-face {
  font-family: 'Noto Serif TC';
  font-style: normal;
  font-weight: 500;
  src: url(https://fonts.gstatic.com/s/notoseriftc/v19/XLYgIZb5bJNDGYxLBibeHZ0BhncESXFtUsM.woff2) format('woff2');
}`;
        const faces = parseFontFaces(css, 'https://fonts.googleapis.com/css2?family=Noto+Serif+TC');
        expect(faces).toHaveLength(1);
        expect(faces[0].family).toBe('Noto Serif TC');
        expect(faces[0].weight).toBe('500');
        expect(faces[0].url).toContain('https://fonts.gstatic.com/');
        expect(faces[0].unicodeRange).toBeUndefined();
    });

    it('一个 CSS 里多个 face 全部解析；URL 带引号也剥掉', () => {
        const css = `
@font-face { font-family: A; font-weight: 400; src: url("/f/a.woff2"); }
@font-face { font-family: 'B C'; font-weight: 700; src: url('/f/b.woff2'); unicode-range: U+4E00-9FFF; }
`;
        const faces = parseFontFaces(css, BASE);
        expect(faces.map(f => f.family)).toEqual(['A', 'B C']);
        expect(faces[0].url).toBe('http://127.0.0.1:8642/f/a.woff2');
    });

    it('没有 src 或 family 的坏块跳过；空输入返回空数组', () => {
        const css = `
@font-face { font-weight: 400; src: url(/x.woff2); }
@font-face { font-family: NoSrc; }
not a font face at all
`;
        expect(parseFontFaces(css, BASE)).toHaveLength(0);
        expect(parseFontFaces('', BASE)).toHaveLength(0);
    });
});
