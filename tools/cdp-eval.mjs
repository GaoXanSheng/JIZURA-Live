// CDP 调试辅助，对运行中的字面 Live（127.0.0.1:9222）
// node tools/cdp-eval.mjs "<js>" 求值；--shot out.png 截图；--list 列页；--url <sub> 选页
import {writeFileSync} from 'node:fs';

const BASE = 'http://127.0.0.1:9222';

async function targets() {
    return (await (await fetch(BASE + '/json/list')).json()).filter(t => t.type === 'page');
}

function pick(list, urlSub) {
    if (urlSub) return list.find(t => t.url.includes(urlSub));
    return list.find(t => !t.url.includes('/render')) || list[0];
}

async function main() {
    const argv = process.argv.slice(2);
    if (argv[0] === '--list') {
        console.log(JSON.stringify(await targets(), null, 2));
        return;
    }
    const urlSub = argv.includes('--url') ? argv[argv.indexOf('--url') + 1] : undefined;
    const expr = urlSub !== undefined ? argv.filter((a, i) => i !== 0 && a !== '--url' && a !== urlSub).join(' ') : argv.join(' ');
    const list = await targets();
    const tgt = pick(list, urlSub);
    if (!tgt) {
        console.error('no target');
        process.exit(1);
    }
    const ws = new WebSocket(tgt.webSocketDebuggerUrl);
    await new Promise((res, rej) => {
        ws.onopen = res;
        ws.onerror = rej;
    });
    let id = 0;
    const pending = new Map();
    ws.onmessage = ev => {
        const m = JSON.parse(ev.data);
        if (m.id && pending.has(m.id)) {
            pending.get(m.id)(m);
            pending.delete(m.id);
        }
    };
    const send = (method, params = {}) => new Promise((res, rej) => {
        const i = ++id;
        pending.set(i, m => m.error ? rej(new Error(JSON.stringify(m.error))) : res(m.result));
        ws.send(JSON.stringify({id: i, method, params}));
    });
    const evalJs = async (expr) => {
        const r = await send('Runtime.evaluate', {
            expression: expr,
            returnByValue: true,
            awaitPromise: true,
            userGesture: true
        });
        if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || JSON.stringify(r.exceptionDetails));
        return r.result.value;
    };
    if (argv[0] === '--mouse') {
        // --mouse <x1> <y1> [x2 y2]  按下→移动→松开；给 x2 y2 才有位移
        const [x1, y1] = [+argv[1], +argv[2]];
        const hasTo = argv.length >= 5 && !isNaN(+argv[3]);
        const [x2, y2] = hasTo ? [+argv[3], +argv[4]] : [x1, y1];
        const steps = 6;
        await send('Input.dispatchMouseEvent', {
            type: 'mousePressed',
            x: x1,
            y: y1,
            button: 'left',
            buttons: 1,
            clickCount: 1,
            pointerType: 'mouse'
        });
        await new Promise(r => setTimeout(r, 80));
        for (let i = 1; i <= steps; i++) {
            await send('Input.dispatchMouseEvent', {
                type: 'mouseMoved',
                x: Math.round(x1 + (x2 - x1) * i / steps),
                y: Math.round(y1 + (y2 - y1) * i / steps),
                button: 'left',
                buttons: 1,
                pointerType: 'mouse'
            });
            await new Promise(r => setTimeout(r, 40));
        }
        await send('Input.dispatchMouseEvent', {
            type: 'mouseReleased',
            x: x2,
            y: y2,
            button: 'left',
            buttons: 0,
            clickCount: 0,
            pointerType: 'mouse'
        });
        console.log('mouse drag', x1, y1, '->', x2, y2);
    } else if (argv[0] === '--shot') {
        const out = argv[1] || 'shot.png';
        await send('Page.enable');
        const r = await send('Page.captureScreenshot', {format: 'png'});
        writeFileSync(out, Buffer.from(r.data, 'base64'));
        console.log('saved', out);
    } else if (argv[0] === '--reload') {
        await send('Page.enable');
        await send('Page.reload');
        // 等页面重新可用
        for (let i = 0; i < 40; i++) {
            await new Promise(r => setTimeout(r, 250));
            try {
                await evalJs('1+1');
                console.log('reloaded ok');
                ws.close();
                return;
            } catch {
            }
        }
        console.error('reload wait timeout');
    } else {
      console.log(JSON.stringify(await evalJs(expr), null, 2));
    }
    ws.close();
}

main().catch(e => {
    console.error(e.message || e);
    process.exit(1);
});
