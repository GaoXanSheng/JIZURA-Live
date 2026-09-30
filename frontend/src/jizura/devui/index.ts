/* 渲染调试面板（DevTools 式）：Ctrl+Alt+D 呼出，任意页面可用。
   折叠图标条：FPS / CDP 状态点；展开为完整面板
   （帧与主线程、CDP 精确模式、元素标框）。
   关闭时全部采集器停止，零常驻开销。 */

import type {App} from 'vue';
import {reactive, watch} from 'vue';
import {startFrames, stopFrames} from './frames';
import {setBoxerModes, stopBoxer} from './boxer';
import {cdpSetWant, setPaintFlashing} from './cdp';

const LS = 'jizura.devui';

function lsGet(k: string): string {
    try {
        return localStorage.getItem(k) || '';
    } catch {
        return '';
    }
}

function lsSet(k: string, v: string) {
    try {
        v ? localStorage.setItem(k, v) : localStorage.removeItem(k);
    } catch {
    }
}

export const devUi = reactive({
    open: new URLSearchParams(window.location.search).get('devui') === '1' || lsGet(LS) === '1',
    expanded: lsGet(LS + '.expanded') === '1',
    flash: lsGet(LS + '.flash') === '1',
    hover: lsGet(LS + '.hover') === '1',
    cdp: lsGet(LS + '.cdp') === '1',            // 是否维持 CDP 连接
    paintFlash: lsGet(LS + '.paintflash') === '1',
});

let appRef: App | null = null;

function applyCollectors() {
    if (devUi.open) {
        startFrames();
        if (devUi.flash || devUi.hover) setBoxerModes(devUi.flash, devUi.hover);
        else stopBoxer();
        cdpSetWant(devUi.cdp);
        if (devUi.cdp && devUi.paintFlash) setPaintFlashing(true);
    } else {
        stopFrames();
        stopBoxer();
        cdpSetWant(false);
    }
}

export function toggleDevUi(v?: boolean) {
    devUi.open = v ?? !devUi.open;
    lsSet(LS, devUi.open ? '1' : '');
    if (appRef) appRef.config.performance = devUi.open;   // 开启时记 Vue 组件渲染耗时
    applyCollectors();
}

export function installDevUi(app: App) {
    appRef = app;
    app.config.performance = devUi.open;
    window.addEventListener('keydown', e => {
        if (e.ctrlKey && e.altKey && e.code === 'KeyD') {
            e.preventDefault();
            toggleDevUi();
        }
    });
    if (devUi.open) applyCollectors();
    watch(() => [devUi.flash, devUi.hover], () => {
        lsSet(LS + '.flash', devUi.flash ? '1' : '');
        lsSet(LS + '.hover', devUi.hover ? '1' : '');
        if (devUi.open) {
            if (devUi.flash || devUi.hover) setBoxerModes(devUi.flash, devUi.hover);
            else stopBoxer();
        }
    });
    watch(() => devUi.cdp, () => {
        lsSet(LS + '.cdp', devUi.cdp ? '1' : '');
        cdpSetWant(devUi.cdp);
    });
    watch(() => devUi.paintFlash, () => {
        lsSet(LS + '.paintflash', devUi.paintFlash ? '1' : '');
        setPaintFlashing(devUi.paintFlash);
    });
    watch(() => devUi.expanded, () => lsSet(LS + '.expanded', devUi.expanded ? '1' : ''));
}
