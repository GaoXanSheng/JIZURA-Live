import {describe, expect, it} from 'vitest';
import {boxerActive, elLabel, setBoxerModes, startBoxer, stopBoxer, vueComp} from '../../src/jizura/devui/boxer';

describe('boxer 元素描述', () => {
    it('elLabel：标签 + id + 首个 class', () => {
        const el = document.createElement('div');
        expect(elLabel(el)).toBe('div');
        el.id = 'tl';
        el.className = 'canvas wrap';
        expect(elLabel(el)).toBe('div#tl.canvas');
    });

    it('vueComp：沿父链找 __vueParentComponent 的组件名', () => {
        const outer = document.createElement('section');
        const inner = document.createElement('div');
        outer.appendChild(inner);
        (outer as any).__vueParentComponent = {type: {__name: 'LineRow'}};
        expect(vueComp(inner)).toBe('LineRow');
        expect(vueComp(outer)).toBe('LineRow');
        const free = document.createElement('span');
        expect(vueComp(free)).toBe('');
    });

    it('vueComp：无 __name 时回退 name，都没有为空', () => {
        const el = document.createElement('b');
        (el as any).__vueParentComponent = {type: {name: 'AnonComp'}};
        expect(vueComp(el)).toBe('AnonComp');
        const el2 = document.createElement('i');
        (el2 as any).__vueParentComponent = {type: {}};
        expect(vueComp(el2)).toBe('');
    });
});

describe('boxer 启停', () => {
    it('两个开关全关时不启动', () => {
        startBoxer(false, false);
        expect(boxerActive()).toBe(false);
    });

    it('开启后运行，全关经 setBoxerModes 停止', () => {
        startBoxer(true, false);
        expect(boxerActive()).toBe(true);
        setBoxerModes(false, false);
        expect(boxerActive()).toBe(false);
        startBoxer(false, true);
        expect(boxerActive()).toBe(true);
        stopBoxer();
        expect(boxerActive()).toBe(false);
    });

    it('stop 幂等，重复调用安全', () => {
        stopBoxer();
        stopBoxer();
        expect(boxerActive()).toBe(false);
    });
});
