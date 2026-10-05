// 预览画布的清晰度档位，存 localStorage（和 previewVolume 一样的即时偏好）。
// 短边分辨率上限，把预览渲染尺寸和字幕设计尺寸（plan.W/H）解绑。
// 'auto' = 跟随窗口物理像素，上限取字幕设计尺寸，历史行为。
// 只影响预览清晰度/性能：导出分辨率由导出页 project.res 决定，Spout 渲染窗口也不读这里。
export type PreviewResTier = 'auto' | 720 | 1080 | 1440 | 2160;

const KEY = 'jizura.previewres';
const NUM_TIERS = [720, 1080, 1440, 2160];

export function getPreviewRes(): PreviewResTier {
    let v: unknown = null;
    try {
        v = JSON.parse(localStorage.getItem(KEY) || 'null');
    } catch { // 坏值回退 auto
    }
    if (v === 'auto') return v;
    if (typeof v === 'number' && NUM_TIERS.includes(v)) return v as PreviewResTier;
    return 'auto';
}

export function setPreviewRes(v: PreviewResTier) {
    try {
        localStorage.setItem(KEY, JSON.stringify(v));
    } catch { // 私隐模式等写不进，保持原值
    }
}

// 档位 → 预览画布宽度上限：短边取档位像素，另一边按画幅比例放大，比例恒跟随画幅不变形
export function tierWidthCap(tier: PreviewResTier, W: number, H: number): number {
    if (tier === 'auto') return W;
    return W >= H ? Math.round(tier * W / H) : tier;
}

export function previewWidthCap(W: number, H: number): number {
    return tierWidthCap(getPreviewRes(), W, H);
}

// 播放掉帧时的自动降质：S.slow 置位（单帧渲染超时）且播放中时，在关滤镜/辉光之外
// 再跳过色差重影层（ghost pass）。即时生效的应用偏好，默认开。
// 只影响主编辑器播放中的预览：暂停即恢复，导出与 Spout 渲染页不读这里。
const DG_KEY = 'jizura.autodegrade';

// 播放中降质生效期间每帧都会读，走内存缓存，localStorage 只在设置变更时碰
let dgCache: boolean | null = null;

export function getAutoDegrade(): boolean {
    if (dgCache == null) {
        try {
            dgCache = localStorage.getItem(DG_KEY) !== '0';
        } catch { // 读不到按默认开
            dgCache = true;
        }
    }
    return dgCache;
}

export function setAutoDegrade(v: boolean) {
    dgCache = v;
    try {
        localStorage.setItem(DG_KEY, v ? '1' : '0');
    } catch { // 私隐模式等写不进，保持本次会话内存值即可
    }
}
