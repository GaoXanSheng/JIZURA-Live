// 渲染调试面板，Ctrl+Alt+D 呼出，任意页面可用。
// 折叠时只有 FPS 图标条，展开是完整面板（帧与主线程）。
// 关闭时采集器停止，无常驻开销。
// 元素标框/CDP 精确模式已移除：启发式标框噪音大，要 CDP 直接用
// RemoteDebuggingPort + chrome://inspect 或 /json 原生工具。

import type {App} from 'vue';
import {reactive, watch} from 'vue';
import {startFrames, stopFrames} from './frames';
import {setEnginePerfOn} from './engineperf';

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
});

let appRef: App | null = null;

function applyCollectors() {
    if (devUi.open) startFrames();
    else stopFrames();
    setEnginePerfOn(devUi.open && devUi.expanded);   // 帧归因只在展开时采集（折叠条只要 fps）
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
    watch(() => devUi.expanded, () => {
        lsSet(LS + '.expanded', devUi.expanded ? '1' : '');
        if (devUi.open) applyCollectors();   // 展开/收起切换帧归因采集
    });
}
