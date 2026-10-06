// 上限语义：短边=档位像素，另一边按画幅比例放大，比例恒跟随画幅不变形
import {beforeEach, describe, expect, it, vi} from 'vitest';
import {
    getNoPreview,
    getPreviewRes,
    previewWidthCap,
    setNoPreview,
    setPreviewRes,
    tierWidthCap
} from '../../src/jizura/previewres';

describe('tierWidthCap 档位换算', () => {
    it('auto → 字幕设计宽（历史行为）', () => {
        expect(tierWidthCap('auto', 1920, 1080)).toBe(1920);
        expect(tierWidthCap('auto', 1080, 1920)).toBe(1080);
    });

    it('横屏：短边=档位，宽按画幅放大', () => {
        expect(tierWidthCap(720, 1920, 1080)).toBe(1280);    // 16:9
        expect(tierWidthCap(1440, 2520, 1080)).toBe(3360);   // 21:9
        expect(tierWidthCap(1080, 1440, 1080)).toBe(1440);   // 4:3
    });

    it('竖屏/方形：短边是宽，上限=档位', () => {
        expect(tierWidthCap(1080, 1080, 1920)).toBe(1080);   // 9:16
        expect(tierWidthCap(2160, 1440, 1800)).toBe(2160);   // 4:5
        expect(tierWidthCap(2160, 1440, 1440)).toBe(2160);   // 1:1
    });
});

describe('previewres 偏好读写', () => {
    beforeEach(() => localStorage.clear());

    it('未设置时默认 auto，上限=设计宽', () => {
        expect(getPreviewRes()).toBe('auto');
        expect(previewWidthCap(1920, 1080)).toBe(1920);
    });

    it('档位往返 + previewWidthCap 联动', () => {
        setPreviewRes(720);
        expect(getPreviewRes()).toBe(720);
        expect(previewWidthCap(1920, 1080)).toBe(1280);
        setPreviewRes('auto');
        expect(getPreviewRes()).toBe('auto');
    });

    it('坏值回退 auto', () => {
        localStorage.setItem('jizura.previewres', JSON.stringify(999));
        expect(getPreviewRes()).toBe('auto');
        localStorage.setItem('jizura.previewres', JSON.stringify(null));
        expect(getPreviewRes()).toBe('auto');
        localStorage.setItem('jizura.previewres', '{oops');
        expect(getPreviewRes()).toBe('auto');
    });
});

describe('noPreview 偏好读写', () => {
    beforeEach(() => localStorage.clear());

    it('开关往返（内存缓存即时生效）', () => {
        setNoPreview(true);
        expect(getNoPreview()).toBe(true);
        setNoPreview(false);
        expect(getNoPreview()).toBe(false);
    });

    // getNoPreview 有内存缓存，存储回退路径要用全新模块副本验证
    it('存储坏值回退关', async () => {
        localStorage.setItem('jizura.nopreview', '{oops');
        vi.resetModules();
        const fresh = await import('../../src/jizura/previewres');
        expect(fresh.getNoPreview()).toBe(false);
        localStorage.setItem('jizura.nopreview', '0');
        vi.resetModules();
        const fresh2 = await import('../../src/jizura/previewres');
        expect(fresh2.getNoPreview()).toBe(false);
    });

    it('存储值 1 → 开（新会话恢复上次选择）', async () => {
        localStorage.setItem('jizura.nopreview', '1');
        vi.resetModules();
        const fresh = await import('../../src/jizura/previewres');
        expect(fresh.getNoPreview()).toBe(true);
    });
});
