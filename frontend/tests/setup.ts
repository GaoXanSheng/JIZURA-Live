/* 测试环境桩：必须在任何 engine 模块导入前生效。
   - canvas 2d：jsdom 不实现 getContext（返回 null），而 02_fonts.js 在模块加载期就创建
     2d context 做字体度量。这里给一个确定性的假 context——measureText 按字符类给固定宽度
     （CJK=1em，字母数字=0.55em），排版/planner 测试因此完全可复现。
   - requestAnimationFrame：jsdom 默认没有，用 setTimeout(16) 兜底（帧统计等模块的 rAF 循环）。 */

type AnyObj = Record<string | symbol, any>;

function makeMeasure(font: string) {
  const px = parseFloat((/([\d.]+)px/.exec(font) || [])[1] || '10') || 10;
  return (raw: string) => {
    const text = String(raw ?? '');
    let em = 0;
    for (const ch of text) {
      if (/[\u2e80-\u9fff\u3000-\u303f\uff00-\uffef\uac00-\ud7af]/.test(ch)) em += 1;
      else if (/[A-Za-z0-9@%&]/.test(ch)) em += 0.55;
      else if (/[ \t]/.test(ch)) em += 0.3;
      else if (ch === '\n') em += 0;
      else em += 0.7;
    }
    const width = em * px;
    return {
      width,
      actualBoundingBoxAscent: px * 0.8,
      actualBoundingBoxDescent: px * 0.2,
      actualBoundingBoxLeft: 0,
      actualBoundingBoxRight: width,
    };
  };
}

function makeCtx2d(canvas: AnyObj): AnyObj {
  const gradient = { addColorStop() {} };
  const noop = () => {};
  const base: AnyObj = {
    canvas,
    filter: 'none',
    font: '10px sans-serif',
    globalAlpha: 1,
    globalCompositeOperation: 'source-over',
    fillStyle: '#000',
    strokeStyle: '#000',
    lineWidth: 1,
    lineCap: 'butt',
    lineJoin: 'miter',
    miterLimit: 10,
    lineDashOffset: 0,
    shadowBlur: 0,
    shadowColor: 'transparent',
    shadowOffsetX: 0,
    shadowOffsetY: 0,
    textAlign: 'left',
    textBaseline: 'alphabetic',
    direction: 'ltr',
    imageSmoothingEnabled: true,
    imageSmoothingQuality: 'low',
  };
  return new Proxy(base, {
    get(t, prop) {
      if (prop in t) return t[prop];
      switch (prop) {
        case 'measureText': {
          let m = makeMeasure(t.font);
          return (s: string) => m(s);
        }
        case 'createLinearGradient':
        case 'createRadialGradient':
        case 'createConicGradient':
          return () => gradient;
        case 'createPattern':
          return () => ({});
        case 'getImageData':
          return (_x: number, _y: number, w: number, h: number) => ({ data: new Uint8ClampedArray(Math.max(0, w | 0) * Math.max(0, h | 0) * 4), width: w | 0, height: h | 0 });
        case 'createImageData':
          return (w: number, h: number) => ({ data: new Uint8ClampedArray(Math.max(0, w | 0) * Math.max(0, h | 0) * 4), width: w | 0, height: h | 0 });
        case 'getTransform':
          return () => ({ a: 1, b: 0, c: 0, d: 1, e: 0, f: 0 });
        case 'isPointInPath':
          return () => false;
        default:
          return noop;
      }
    },
    set(t, prop, v) { t[prop] = v; return true; },
  });
}

const origGetContext = (globalThis as AnyObj).HTMLCanvasElement?.prototype?.getContext;
if (origGetContext) {
  (globalThis as AnyObj).HTMLCanvasElement.prototype.getContext = function (type: string, ...rest: any[]) {
    if (type === '2d') {
      const self: AnyObj = this;
      if (!self.__fakeCtx) self.__fakeCtx = makeCtx2d(self);
      return self.__fakeCtx;
    }
    return origGetContext.call(this, type, ...(rest as []));
  } as any;
}

if (typeof window.requestAnimationFrame !== 'function') {
  (window as AnyObj).requestAnimationFrame = (cb: (t: number) => void) => setTimeout(() => cb(performance.now()), 16) as unknown as number;
  (window as AnyObj).cancelAnimationFrame = (id: number) => clearTimeout(id);
}

/* jsdom 的 Blob 没有 .stream()（krc 解密的 DecompressionStream 管线需要）——换成 Node 原生 Blob */
import {Blob as NodeBlob} from 'node:buffer';

(globalThis as AnyObj).Blob = NodeBlob;
