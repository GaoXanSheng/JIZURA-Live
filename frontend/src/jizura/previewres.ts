/* 预览渲染尺寸档位（应用偏好）：主编辑器预览画布的短边分辨率上限，
   用于把预览渲染尺寸与字幕设计尺寸（plan.W/H）解绑。
   'auto' = 跟随窗口物理像素、上限=字幕设计尺寸（历史行为）。
   只影响预览的清晰度/性能：导出分辨率由导出页 project.res 独立决定，
   Spout 渲染窗口也不读这里。存 localStorage，同 previewVolume 的即时偏好模式。 */
export type PreviewResTier = 'auto' | 720 | 1080 | 1440 | 2160;

const KEY = 'jizura.previewres';
const NUM_TIERS = [720, 1080, 1440, 2160];

export function getPreviewRes(): PreviewResTier {
    let v: unknown = null;
    try {
        v = JSON.parse(localStorage.getItem(KEY) || 'null');
    } catch { /* 坏值回退 auto */
    }
    if (v === 'auto') return v;
    if (typeof v === 'number' && NUM_TIERS.includes(v)) return v as PreviewResTier;
    return 'auto';
}

export function setPreviewRes(v: PreviewResTier) {
    try {
        localStorage.setItem(KEY, JSON.stringify(v));
    } catch { /* 写不进（私隐模式等）保持原值 */
    }
}

/* 档位 → 预览画布宽度上限：短边=档位像素，另一边按画幅比例放大，比例恒跟随画幅不变形 */
export function tierWidthCap(tier: PreviewResTier, W: number, H: number): number {
    if (tier === 'auto') return W;
    return W >= H ? Math.round(tier * W / H) : tier;
}

export function previewWidthCap(W: number, H: number): number {
    return tierWidthCap(getPreviewRes(), W, H);
}
