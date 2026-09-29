/* ============================================================
   JIZURA — 酷狗云端 / krc 歌词适配层（本地版追加）
   在 12_ui.js 之前加载：
     .krc → 浏览器内解密（固定 key 异或 + DecompressionStream inflate），
            转成带 [mm:ss.xx] 时间戳的 LRC 填进 #lyrics，派发 input 事件。
     酷狗音乐 → 直接通过云端 API 在线搜索与流式加载。
   ============================================================ */
(function () {
  'use strict';

  const KRC_KEY = new Uint8Array([0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47,
                                  0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69]);

  /* ---------- krc 解密 ---------- */

  async function inflate(buf) {
    const stream = new Blob([buf]).stream().pipeThrough(new DecompressionStream('deflate'));
    return new Uint8Array(await new Response(stream).arrayBuffer());
  }

  async function decryptKrc(buf) {
    const head = new Uint8Array(buf.subarray(0, 4));
    if (String.fromCharCode(...head) !== 'krc1') throw new Error('不是 .krc 文件（缺少 krc1 魔数）');
    const body = new Uint8Array(buf.subarray(4));
    for (let i = 0; i < body.length; i++) body[i] ^= KRC_KEY[i % 16];
    return new TextDecoder('utf-8').decode(await inflate(body));
  }

  /* ---------- krc → LRC ---------- */

  const RE_TIMED = /^\[(\d+),(\d+)\](.*)$/;
  const RE_WORD = /<(\d+),(\d+),\d+>/g;

  function fmtMs(ms) {
    ms = Math.max(0, Math.floor(ms));
    const cs = Math.floor((ms % 1000) / 10);
    return String(Math.floor(ms / 60000)).padStart(2, '0') + ':' +
           String(Math.floor(ms / 1000) % 60).padStart(2, '0') + '.' +
           String(cs).padStart(2, '0');
  }

  /* KRC 的 [language:] 块里 type 字段并不可靠（同一首歌里可能 type:1 是翻译、type:0 是音译），
     按字符脚本分类：拉丁字母为主 → 音译；假名/汉字/谚文为主 → 翻译 */
  function classifyLangRows(rows) {
    let latin = 0, cjk = 0;
    for (const r of rows) {
      for (const ch of r) {
        if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')) latin++;
        else if (/[\u3040-\u30ff\u3400-\u9fff\uac00-\ud7af]/.test(ch)) cjk++;  // 假名/汉字/谚文
      }
    }
    if (latin + cjk === 0) return 'none';
    return latin >= cjk ? 'roma' : 'trans';
  }

  function parseLangBlobs(text) {
    const raw = text.split('\n').find(l => l.startsWith('[language:'));
    if (!raw) return {};
    const line = raw.trim();          // KRC 是 CRLF：不先去 \r 的话结尾 ] 会被误当 base64 一部分
    try {
      const bin = Uint8Array.from(atob(line.slice('[language:'.length, -1)), c => c.charCodeAt(0));
      const j = JSON.parse(new TextDecoder('utf-8').decode(bin));
      let roma = null, trans = null;
      for (const item of j.content || []) {
        const rows = (item.lyricContent || []).map(blocks =>
          Array.isArray(blocks) ? blocks.join('').trim() : String(blocks).trim());
        if (!rows.some(r => r)) continue;
        const kind = classifyLangRows(rows);
        if (kind === 'roma') { if (!roma) roma = rows; }
        else if (kind === 'trans') { if (!trans) trans = rows; }
      }
      return { roma, trans };
    } catch (e) { return {}; }
  }

  function krcToLrc(text, opts, mapOut) {
    const o = opts || {};
    const wantRoma = o.roma !== false, wantTrans = o.trans !== false;
    const blobs = parseLangBlobs(text);
    const roma = wantRoma ? blobs.roma : null;
    const trans = wantTrans ? blobs.trans : null;
    const allRoma = blobs.roma, allTrans = blobs.trans;   // map 不受勾选影响：字幕始终拿得到两轨
    let offsetMs = 0;
    for (const tag of ['offset', 'manualoffset']) {
      const m = text.match(new RegExp('^\\[' + tag + ':(-?\\d+)\\]', 'm'));
      if (m) offsetMs += parseInt(m[1], 10);
    }

    const out = [];
    let idx = -1;                                    // 第几条带时间戳的行：音译/翻译按它对齐（间奏空行也占位）
    for (const raw of text.split('\n')) {
      const line = raw.replace(/\r$/, '');
      if (!line.trim()) continue;
      const m = RE_TIMED.exec(line);
      if (!m) continue;
      idx++;
      const start = parseInt(m[1], 10) - offsetMs;
      const words = [];
      let wm;
      RE_WORD.lastIndex = 0;
      const body = m[3];
      while ((wm = RE_WORD.exec(body)) !== null) {
        const after = body.slice(wm.index + wm[0].length);
        const next = after.search(/<\d+,\d+,\d+>/);
        words.push(next === -1 ? after : after.slice(0, next));
      }
      let textLine = words.join('').trim();
      if (!textLine) { out.push(''); continue; }        // 空行 = 停顿（JIZURA 语法）
      textLine = textLine.replace(/\//g, '／');          // `/` 是 JIZURA 的切镜头指令
      const notes = [];
      for (const rows of [roma, trans]) {
        let s = rows && rows[idx];
        if (s) {
          s = s.replace(/\//g, '／').trim();
          if (s && s !== textLine && !notes.includes(s)) notes.push(s);
        }
      }
      if (mapOut) {
        const pick = rows => { let s = rows && rows[idx]; return s ? s.replace(/\//g, '／').trim() : ''; };
        const r = pick(allRoma), tr = pick(allTrans);
        if (r || tr) mapOut.set(textLine, { roma: r, trans: tr });
      }
      out.push('[' + fmtMs(start) + ']' + textLine + (notes.length ? '|' + notes.join('　') : ''));
    }
    return out.join('\n').replace(/\n+$/, '') + '\n';
  }

  /* ---------- UI：toast ---------- */

  let toastEl = null;
  function toast(msg, sticky) {
    if (!toastEl) {
      toastEl = document.createElement('div');
      toastEl.style.cssText = 'position:fixed;right:14px;bottom:14px;z-index:9999;' +
        'background:#222;color:#fff;padding:8px 14px;border-radius:8px;font-size:13px;' +
        'opacity:.92;max-width:70vw;box-shadow:0 2px 10px rgba(0,0,0,.25)';
      (document.body || document.documentElement).appendChild(toastEl);
    }
    toastEl.textContent = msg;
    toastEl.style.display = 'block';
    if (!sticky) { clearTimeout(toast._t); toast._t = setTimeout(() => { toastEl.style.display = 'none'; }, 4000); }
  }
  const hideToast = () => { if (toastEl) toastEl.style.display = 'none'; };

  /* ---------- 入口 ---------- */

  async function handleKrc(file) {
    try {
      const text = await decryptKrc(new Uint8Array(await file.arrayBuffer()));
      const map = new Map();
      kgLastLyric = { fmt: 'krc', text, map };
      kgPersistMap();
      const lyrics = document.getElementById('lyrics');
      lyrics.value = krcToLrc(text, kgLyricOptions(), map);
      lyrics.dispatchEvent(new Event('input', { bubbles: true }));
      toast('歌词已载入：' + file.name);
    } catch (e) {
      alert('krc 解析失败：' + e.message);
    }
  }

  function intercept(e) {
    const f = e.target.files && e.target.files[0];
    if (!f) return;
    const n = f.name.toLowerCase();
    if (n.endsWith('.krc')) { e.stopImmediatePropagation(); handleKrc(f); }
  }

  /* ============================================================
     字幕（渲染时叠加在画面上，预览与 MP4 导出都生效）
       数据：KRC 载入时建的 音译/翻译 映射（kgLastLyric.map，按歌词原文查）；
             没有映射时退回行内注音（| 后的内容）按脚本分类。
       入口：包装 J.Renderer.prototype.frame —— 正式帧画完再叠加字幕；
             转场/ morph 的离屏采样（noTrans）与分层导出（layer）不画。
       设置：详细模式右栏「字幕」区，存 localStorage。
     ============================================================ */

  const KG_SUB_DEFAULT = {
    content: 'off', pos: 'bottom', size: 4.5, stroke: 4,
    color: '#ffffff', strokeColor: '#000000', font: '', bar: false,
    offX: 0, offY: 0,               // 自定义位置：画面宽/高的百分比偏移
  };
  let kgSub = null;
  let kgSubBox = null;

  function kgLoadSubCfg() {
    try { return Object.assign({}, KG_SUB_DEFAULT, JSON.parse(localStorage.getItem('jizura.kgsub') || '{}')); }
    catch (e) { return Object.assign({}, KG_SUB_DEFAULT); }
  }
  function kgSaveSubCfg() {
    try { localStorage.setItem('jizura.kgsub', JSON.stringify(kgSub)); } catch (e) { /* 忽略 */ }
  }

  function kgInjectSubPanel() {
    const col = document.querySelector('.col-right');
    if (!col || kgSubBox) return;
    const sec = document.createElement('div');
    sec.className = 'sec pro-only';
    sec.innerHTML =
      '<div class="sec-h"><h2>字幕</h2><span class="muted">渲染时叠加（预览与导出）</span></div>' +
      '<div class="fields">' +
      '<label class="field field-wide">显示<select data-k="content"><option value="off">不显示</option><option value="trans">翻译</option><option value="roma">音译</option><option value="both">音译 + 翻译</option></select></label>' +
      '<label class="field">位置<select data-k="pos"><option value="bottom">底部</option><option value="top">顶部</option></select></label>' +
      '<label class="field">字号 %<input data-k="size" type="number" min="2" max="15" step="0.5"></label>' +
      '<label class="field">描边<input data-k="stroke" type="number" min="0" max="12" step="0.5" title="描边宽度（按 1080p 像素计）"></label>' +
      '<label class="field">文字颜色<input data-k="color" type="color"></label>' +
      '<label class="field">描边颜色<input data-k="strokeColor" type="color"></label>' +
      '<label class="field">水平偏移 %<input data-k="offX" type="number" min="-50" max="50" step="1" title="字幕水平中心偏移（画面宽度百分比，正=右移）"></label>' +
      '<label class="field">垂直偏移 %<input data-k="offY" type="number" min="-50" max="50" step="1" title="字幕垂直偏移（画面高度百分比，正=下移）"></label>' +
      '<label class="field field-wide">字体<select data-k="font"><option value="">跟随画面</option><option value="sans">黑体（无衬线）</option><option value="serif">宋体（衬线）</option></select></label>' +
      '<label class="check field-wide"><input data-k="bar" type="checkbox"><span>半透明底条</span></label>' +
      '</div>';
    col.appendChild(sec);
    kgSubBox = sec;
    for (const el of sec.querySelectorAll('[data-k]')) {
      const k = el.dataset.k;
      if (el.type === 'checkbox') el.checked = !!kgSub[k];
      else el.value = kgSub[k];
      el.addEventListener('change', () => {
        kgSub[k] = el.type === 'checkbox' ? el.checked
          : el.type === 'number' ? (parseFloat(el.value) || 0)
          : el.value;
        kgSaveSubCfg();
        kgRedraw();
      });
    }
  }

  /* 暂停时 JIZURA 不主动重绘：按 HUD 显示的当前时间 seek 一下（原地）强制出一帧 */
  function kgRedraw() {
    try {
      const m = document.getElementById('timeNow').textContent.split(':');
      J.uiApi.seek(parseInt(m[0], 10) * 60 + parseFloat(m[1]));
    } catch (e) { /* 播放中或尚无方案时无需处理 */ }
  }

  function kgSubTextFor(line) {
    if (!line || line.interlude) return '';
    const cfg = kgSub;
    const map = kgLastLyric && kgLastLyric.map && kgLastLyric.map.get(line.text);
    const fromNote = () => {
      const note = String(line.note || '');
      if (!note) return { roma: '', trans: '' };
      const roma = [], trans = [];
      for (const p of note.split('　').map(s => s.trim()).filter(Boolean)) {
        (classifyLangRows([p]) === 'roma' ? roma : trans).push(p);
      }
      return { roma: roma.join(' '), trans: trans.join(' ') };
    };
    const parts = map || fromNote();
    if (cfg.content === 'roma') return parts.roma || '';
    if (cfg.content === 'trans') return parts.trans || '';
    return [parts.roma, parts.trans].filter(Boolean).join('　');
  }

  function kgSubFont(px) {
    if (kgSub.font === 'serif') return `700 ${px}px "Noto Serif JP", SimSun, serif`;
    if (kgSub.font === 'sans') return `700 ${px}px "Microsoft YaHei", "Noto Sans JP", sans-serif`;
    return `700 ${px}px "Noto Sans JP", "Microsoft YaHei", sans-serif`;
  }

  function kgWrapSubtitle(ctx, text, maxW) {
    // 先按全角空格分段（音译/翻译各占一段），超宽的段再按字符折行
    const out = [];
    for (const seg of text.split('　')) {
      if (!seg) continue;
      if (ctx.measureText(seg).width <= maxW) { out.push(seg); continue; }
      let cur = '';
      for (const ch of seg) {
        if (cur && ctx.measureText(cur + ch).width > maxW) { out.push(cur); cur = ch; }
        else cur += ch;
      }
      if (cur) out.push(cur);
    }
    return out;
  }

  function kgDrawSubtitle(ctx, plan, t, opt) {
    if (!kgSub || kgSub.content === 'off' || opt.noTrans || opt.layer) return;
    if (!plan || !plan.lines || !plan.W || !plan.H) return;
    const line = plan.lines.find(l => t >= l.start && t < (l.visEnd != null ? l.visEnd : l.end));
    const text = kgSubTextFor(line);
    if (!text) return;

    const W = plan.W, H = plan.H;
    const scale = opt.scale || (ctx.canvas.width ? ctx.canvas.width / W : 1);
    const fontPx = Math.max(8, H * kgSub.size / 100);
    const strokePx = H / 1080 * kgSub.stroke;
    const maxW = W * 0.86;
    const lh = fontPx * 1.32;

    ctx.save();
    ctx.setTransform(scale, 0, 0, scale, 0, 0);
    ctx.font = kgSubFont(fontPx);
    ctx.textAlign = 'center';
    ctx.textBaseline = 'alphabetic';
    const rows = kgWrapSubtitle(ctx, text, maxW);
    const blockH = rows.length * lh;
    // 自定义位置：offX/offY 为画面宽/高的百分比偏移（默认 0 = 原生底部/顶部居中）
    const clampPct = v => Math.max(-50, Math.min(50, parseFloat(v) || 0));
    const centerX = W * (0.5 + clampPct(kgSub.offX) / 100);
    let baseY = kgSub.pos === 'top' ? H * 0.055 + fontPx : H - H * 0.055 - blockH + fontPx;
    baseY += H * clampPct(kgSub.offY) / 100;

    if (kgSub.bar) {
      const wmax = Math.max(...rows.map(r => ctx.measureText(r).width));
      const padX = fontPx * 0.5, padY = fontPx * 0.3;
      ctx.fillStyle = 'rgba(0,0,0,0.45)';
      ctx.fillRect(centerX - wmax / 2 - padX, baseY - fontPx * 0.82 - padY,
                   wmax + padX * 2, blockH + padY * 2);
    }
    ctx.lineWidth = strokePx;
    ctx.strokeStyle = kgSub.strokeColor;
    ctx.lineJoin = 'round';
    ctx.fillStyle = kgSub.color;
    rows.forEach((r, i) => {
      const y = baseY + i * lh;
      if (strokePx > 0) ctx.strokeText(r, centerX, y);
      ctx.fillText(r, centerX, y);
    });
    ctx.restore();
  }

  /* 正式帧画完再叠加；离屏采样（noTrans）与分层导出（layer）不画 */
  const kgOrigFrame = J.Renderer.prototype.frame;
  J.Renderer.prototype.frame = function (ctx, plan, t, opt = {}) {
    kgOrigFrame.call(this, ctx, plan, t, opt);
    if (kgSub && kgSub.content !== 'off') {
      try { kgDrawSubtitle(ctx, plan, t, opt); } catch (e) { /* 字幕出错不影响渲染 */ }
    }
  };

  /* ============================================================
     酷狗在线搜索（借道本机 server.js 的 /kg/* 端点）
       搜索 → 列表（标出 VIP）→ 点选后音频走 /kg/audio 代理塞回 #audioFile，
       歌词拿 KRC/LRC，KRC 复用上面的 decryptKrc + krcToLrc 填进歌词框。
       VIP 歌要完整音质需点「登录」扫码（token 存在服务端 kg_login.json）。
     ============================================================ */

  const KG_MIME = { mp3: 'audio/mpeg', flac: 'audio/flac', ogg: 'audio/ogg', m4a: 'audio/mp4' };
  const KG_STYLE = [
    '.kgrow{display:flex;gap:6px;margin:8px 0 2px;flex-wrap:wrap}',
    '.kgrow input[type=search]{flex:1 1 120px;min-width:80px;background:var(--raised);border:1px solid var(--line);border-radius:var(--r);padding:6px 8px;min-width:0}',
    '.kg-results{list-style:none;margin:4px 0 2px;padding:0;max-height:190px;overflow-y:auto;border:1px solid var(--line);border-radius:var(--r)}',
    '.kg-results li{border-top:1px solid var(--line)}',
    '.kg-results li:first-child{border-top:0}',
    '.kg-results button{display:flex;width:100%;gap:8px;align-items:baseline;padding:5px 8px;background:none;border:0;border-radius:0;text-align:left;white-space:normal}',
    '.kg-results button:hover{background:rgba(127,127,127,.14)}',
    '.kg-name{flex:none;max-width:58%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}',
    '.kg-meta{color:var(--muted);font-size:11px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;flex:1;min-width:0}',
    '.kg-badge{flex:none;font-size:10px;line-height:1.5;border:1px solid #8a6d2f;color:#c8a24a;border-radius:4px;padding:0 4px}',
    '.kg-empty{padding:8px;color:var(--muted);font-size:12px}',
    '.kg-lyopts{display:inline-flex;gap:14px;margin-left:14px;font-size:12px;vertical-align:middle}',
    '.kg-lyopts label{display:inline-flex;gap:4px;align-items:center;cursor:pointer}',
    '.kg-lyopts .kg-ver select{max-width:180px;padding:2px 4px;font-size:11px}',
    '.kg-modal{position:fixed;inset:0;background:rgba(0,0,0,.5);display:flex;align-items:center;justify-content:center;z-index:9998}',
    '.kg-modal[hidden]{display:none}',
    '.kg-card{background:var(--panel);color:var(--text);border:1px solid var(--line2);border-radius:12px;padding:16px 20px;display:flex;flex-direction:column;gap:10px;align-items:center;box-shadow:0 8px 30px rgba(0,0,0,.4)}',
    '.kg-card img{background:#fff;border-radius:8px;display:block}',
    '.kg-card .kg-tip{color:var(--muted);font-size:12px;min-height:1.2em}',
    '.kg-card .kg-btns{display:flex;gap:8px}',
  ].join('\n');

  let kgEls = null;            // {box, results, query, quality, btnSearch, btnLogin, modal, qrImg, qrTip, qrRefresh, optRoma, optTrans}
  let kgPollTimer = null;
  let kgLoadSeq = 0;
  let kgLastLyric = null;      // 最近一次载入的歌词源 { fmt: 'krc'|'lrc', text }（音译/翻译勾选框靠它即时重渲染）

  // 镜像（渲染进程）浏览器拿不到主页面内存里的 kgLastLyric，
  // 映射持久化到 localStorage（两边共享），载入时还原、storage 事件实时跟随
  function kgPersistMap() {
    try {
      const m = kgLastLyric && kgLastLyric.map;
      if (m && m.size) localStorage.setItem('jizura.kgsubmap', JSON.stringify({ text: kgLastLyric.text || '', map: [...m] }));
      else localStorage.removeItem('jizura.kgsubmap');
    } catch (e) { /* 映射过大存不下就算了 */ }
  }
  function kgRestoreMap() {
    try {
      const saved = JSON.parse(localStorage.getItem('jizura.kgsubmap') || 'null');
      if (saved && saved.map && saved.map.length) kgLastLyric = { fmt: 'krc', text: saved.text || '', map: new Map(saved.map) };
    } catch (e) { }
  }
  kgRestoreMap();

  function kgApi(pathname, params) {
    const q = params ? '?' + new URLSearchParams(params) : '';
    return fetch('/kg/' + pathname + q).then(r => r.json()).catch(() => {
      throw new Error('连不上本机服务：请（重新）运行 启动服务.bat');
    });
  }

  function kgEsc(s) { return String(s == null ? '' : s); }
  function kgFmtDur(sec) {
    sec = Math.round(sec || 0);
    return Math.floor(sec / 60) + ':' + String(sec % 60).padStart(2, '0');
  }

  function kgInjectUI(input) {
    if (kgEls) return;
    const sec = input.closest('.sec');
    const anchor = sec && sec.querySelector('.row');
    if (!sec || !anchor) return;

    const style = document.createElement('style');
    style.textContent = KG_STYLE;
    document.head.appendChild(style);

    const box = document.createElement('div');
    box.className = 'kgrow';
    box.innerHTML =
      '<input type="search" aria-label="酷狗在线搜索" placeholder="酷狗在线搜索：歌名 / 歌手">' +
      '<select title="音质（320k / 无损需要扫码登录酷狗 VIP）" aria-label="音质">' +
      '<option value="128">128k</option><option value="320">320k</option><option value="flac">无损</option></select>' +
      '<button type="button" class="ghost small">搜索</button>' +
      '<button type="button" class="ghost small" title="扫码登录酷狗账号：VIP 歌曲与完整音质需要">扫码登录</button>';
    const results = document.createElement('ul');
    results.className = 'kg-results';
    results.hidden = true;
    anchor.after(box, results);

    const modal = document.createElement('div');
    modal.className = 'kg-modal';
    modal.hidden = true;
    modal.setAttribute('role', 'dialog');
    modal.setAttribute('aria-modal', 'true');
    modal.innerHTML =
      '<div class="kg-card"><strong>酷狗扫码登录</strong>' +
      '<img alt="酷狗登录二维码" width="188" height="188">' +
      '<div class="kg-tip">打开酷狗手机版扫一扫</div>' +
      '<div class="kg-btns"><button type="button" class="ghost small" hidden>刷新二维码</button>' +
      '<button type="button" class="ghost small">关闭</button></div></div>';
    document.body.appendChild(modal);

    kgEls = {
      box, results,
      query: box.querySelector('input'),
      quality: box.querySelector('select'),
      btnSearch: box.querySelectorAll('button')[0],
      btnLogin: box.querySelectorAll('button')[1],
      modal,
      qrImg: modal.querySelector('img'),
      qrTip: modal.querySelector('.kg-tip'),
      qrRefresh: modal.querySelectorAll('button')[0],
      qrClose: modal.querySelectorAll('button')[1],
    };

    kgEls.btnSearch.addEventListener('click', kgSearch);
    kgEls.query.addEventListener('keydown', e => {
      e.stopPropagation();
      if (e.key === 'Enter') { e.preventDefault(); kgSearch(); }
    });
    kgEls.btnLogin.addEventListener('click', kgLoginButton);
    kgEls.qrClose.addEventListener('click', kgCloseLogin);
    kgEls.qrRefresh.addEventListener('click', kgOpenLogin);
    modal.addEventListener('click', e => { if (e.target === modal) kgCloseLogin(); });

    kgInjectLyOpts();
  }

  /* 音译/翻译勾选框（注入在「歌词」区块的歌词语言一行后面） */

  function kgInjectLyOpts() {
    const lyrics = document.getElementById('lyrics');
    const sec = lyrics && lyrics.closest('.sec');
    const langRow = sec && sec.querySelector('.lang-row');
    if (!langRow) return;
    const row = document.createElement('span');
    row.className = 'kg-lyopts';
    row.innerHTML =
      '<label title="把歌词音译（罗马音/拼音）作为小字注音渲染。勾选变化会对刚载入的酷狗 KRC 歌词（含拖入的 .krc）立即重新生成"><input type="checkbox" checked>音译</label>' +
      '<label title="把歌词翻译作为小字注音渲染；与音译同时勾选时两者并列显示。勾选变化会对刚载入的酷狗 KRC 歌词（含拖入的 .krc）立即重新生成"><input type="checkbox" checked>翻译</label>' +
      '<label class="kg-ver" hidden title="同一首歌酷狗上往往有多个歌词版本（官方/用户上传），翻译和音译各版本不一样，可在此切换">' +
      '歌词版本<select></select></label>';
    langRow.after(row);
    const boxes = row.querySelectorAll('input');
    kgEls.optRoma = boxes[0];
    kgEls.optTrans = boxes[1];
    kgEls.verSel = row.querySelector('.kg-ver select');
    kgEls.verBox = row.querySelector('.kg-ver');
    kgEls.verSel.addEventListener('change', kgSwitchVersion);
    for (const b of boxes) b.addEventListener('change', kgApplyLyricOptions);
  }

  function kgLyricOptions() {
    if (!kgEls || !kgEls.optRoma) return {};
    return { roma: kgEls.optRoma.checked, trans: kgEls.optTrans.checked };
  }

  function kgApplyLyricOptions() {
    if (!kgLastLyric || kgLastLyric.fmt !== 'krc') return;
    const lyrics = document.getElementById('lyrics');
    lyrics.value = krcToLrc(kgLastLyric.text, kgLyricOptions(), kgLastLyric.map);
    lyrics.dispatchEvent(new Event('input', { bubbles: true }));
  }

  /* ---------- 歌词版本（酷狗同一首歌常有官方/用户上传的多个版本，翻译音译各异） ---------- */

  function kgVerLabel(c) {
    return (c.from || '其他版本') + (c.nick && c.nick !== '热心用户' ? ' · ' + c.nick : '');
  }

  function kgRefreshVerSel() {
    if (!kgEls || !kgEls.verSel) return;
    const cands = (kgLastLyric && kgLastLyric.cands) || [];
    kgEls.verSel.innerHTML = '';
    kgEls.verBox.hidden = cands.length < 2;
    cands.forEach((c, i) => {
      const o = document.createElement('option');
      o.value = String(i);
      o.textContent = kgVerLabel(c);
      kgEls.verSel.appendChild(o);
    });
  }

  /* 载入一份歌词源（ly = /kg/lyric 的返回），重建映射并按当前勾选生成 LRC */
  async function kgAdoptLyric(ly, keepCands) {
    const bin = Uint8Array.from(atob(ly.content), c => c.charCodeAt(0));
    const lyrics = document.getElementById('lyrics');
    const cands = ly.candidates || (keepCands && kgLastLyric && kgLastLyric.cands) || [];
    if (ly.fmt === 'krc') {
      const text = await decryptKrc(bin);
      const map = new Map();
      kgLastLyric = { fmt: 'krc', text, map, cands };
      kgPersistMap();
      lyrics.value = krcToLrc(text, kgLyricOptions(), map);
    } else {
      kgLastLyric = { fmt: 'lrc', text: new TextDecoder('utf-8').decode(bin), cands };
      kgPersistMap();
      lyrics.value = kgLastLyric.text;
    }
    lyrics.dispatchEvent(new Event('input', { bubbles: true }));
    kgRefreshVerSel();
    return ly.fmt;
  }

  async function kgSwitchVersion() {
    const cands = kgLastLyric && kgLastLyric.cands || [];
    const idx = +kgEls.verSel.value;
    const c = cands[idx];
    if (!c) return;
    toast('切换歌词版本…');
    try {
      const ly = await kgApi('lyric', { id: c.id, accesskey: c.accesskey });
      if (!ly.ok) throw new Error(ly.error || '下载失败');
      const fmt = await kgAdoptLyric(ly, true);
      kgEls.verSel.selectedIndex = idx;      // 重建选项后选回所点版本
      const bad = fmt === 'krc' && !/^\[\d\d:\d\d[.:]/m.test(document.getElementById('lyrics').value);
      toast(bad ? '该版本格式暂不支持，请换个版本试试'
                : '歌词版本：' + kgVerLabel(c) + (fmt === 'lrc' ? '（普通 LRC，没有音译/翻译）' : ''));
    } catch (e) {
      toast('切换失败：' + e.message, true);
      kgRefreshVerSel();
    }
  }

  async function kgSearch() {
    if (!kgEls) return;
    const kw = kgEls.query.value.trim();
    if (!kw) { kgEls.query.focus(); return; }
    kgEls.btnSearch.disabled = true;
    kgEls.btnSearch.textContent = '搜索中…';
    try {
      const j = await kgApi('search', { kw });
      if (!j.ok) throw new Error(j.error || '搜索失败');
      kgRenderResults(j.list || []);
    } catch (e) {
      kgEls.results.hidden = false;
      kgEls.results.innerHTML = '<li class="kg-empty"></li>';
      kgEls.results.firstElementChild.textContent = '搜索失败：' + e.message;
    } finally {
      kgEls.btnSearch.disabled = false;
      kgEls.btnSearch.textContent = '搜索';
    }
  }

  function kgRenderResults(list) {
    const ul = kgEls.results;
    ul.innerHTML = '';
    ul.hidden = false;
    if (!list.length) {
      const li = document.createElement('li');
      li.className = 'kg-empty';
      li.textContent = '没有找到歌曲';
      ul.appendChild(li);
      return;
    }
    for (const it of list) {
      const li = document.createElement('li');
      const btn = document.createElement('button');
      btn.type = 'button';
      const name = document.createElement('span');
      name.className = 'kg-name';
      name.textContent = kgEsc(it.name);
      const meta = document.createElement('span');
      meta.className = 'kg-meta';
      meta.textContent = kgEsc(it.singer) + (it.album ? ' · ' + kgEsc(it.album) : '') + ' · ' + kgFmtDur(it.duration);
      btn.append(name, meta);
      if (it.pay && it.pay !== 0) {
        const b = document.createElement('span');
        b.className = 'kg-badge';
        b.textContent = 'VIP';
        btn.appendChild(b);
      }
      btn.addEventListener('click', () => kgLoadSong(it, btn));
      li.appendChild(btn);
      ul.appendChild(li);
    }
  }

  async function kgLoadSong(it, btn) {
    const my = ++kgLoadSeq;
    const old = btn.textContent;
    btn.textContent = '载入中…';
    const input = document.getElementById('audioFile');
    try {
      const u = await kgApi('url', {
        hash: it.hash, album_id: it.albumId, album_audio_id: it.albumAudioId,
        quality: (kgEls && kgEls.quality.value) || '128',
      });
      if (my !== kgLoadSeq) return;
      if (!u.ok) throw new Error(u.error || '拿不到播放链接');
      if (u.needVip) { kgOpenLogin(); throw new Error('这首需要酷狗 VIP，扫码登录后再试'); }
      btn.textContent = '下载中…';
      const blob = await (await fetch('/kg/audio?u=' + encodeURIComponent(u.url))).blob();
      if (my !== kgLoadSeq) return;
      const ext = (u.ext || (blob.type.match(/audio\/(\w+)/) || [])[1] || 'mp3').toLowerCase();
      const file = new File([blob], `${it.singer || ''} - ${it.name}.${ext}`.replace(/\s+-\s+\./, '.'),
                            { type: KG_MIME[ext] || blob.type || 'application/octet-stream' });
      const dt = new DataTransfer();
      dt.items.add(file);
      input.files = dt.files;
      input.dispatchEvent(new Event('change'));

      let lyricNote = '';
      try {
        const ly = await kgApi('lyric', { hash: it.hash, album_id: it.albumId });
        if (my !== kgLoadSeq) return;
        if (ly.ok) {
          const fmt = await kgAdoptLyric(ly, false);
          lyricNote = fmt === 'krc' ? '，歌词已填入' : '，歌词已填入（普通 LRC，无音译/翻译）';
        } else {
          lyricNote = '，没找到歌词';
        }
      } catch (e) { lyricNote = '，歌词加载失败'; }

      toast(u.trial ? `只载入 60 秒试听（这首需要 VIP，可点「登录」）：${it.name}${lyricNote}`
                    : `已载入：${it.name}${lyricNote}`);
    } catch (e) {
      toast('载入失败：' + e.message, true);
    } finally {
      if (my === kgLoadSeq) btn.textContent = old;
    }
  }

  /* ---------- 扫码登录 ---------- */

  async function kgRefreshLoginButton() {
    if (!kgEls) return;
    try {
      const j = await kgApi('login');
      kgEls.btnLogin.textContent = j.logged ? '退出登录' : '扫码登录';
      kgEls.btnLogin.title = j.logged
        ? ('已登录酷狗（' + (j.nickname || '账号 ' + (j.userid || '?')) + '），点击退出')
        : '扫码登录酷狗账号：VIP 歌曲与完整音质需要';
    } catch (e) { /* 服务不在就保持默认 */ }
  }

  async function kgLoginButton() {
    try {
      const j = await kgApi('login');
      if (j.logged) {
        if (!window.confirm('确定退出酷狗登录？\n退出后 VIP 歌曲将只能试听，重新扫码即可恢复。')) return;
        await kgApi('logout');
        toast('已退出酷狗登录');
        return kgRefreshLoginButton();
      }
    } catch (e) { toast(e.message, true); return; }
    kgOpenLogin();
  }

  async function kgOpenLogin() {
    if (!kgEls) return;
    const { modal, qrImg, qrTip, qrRefresh } = kgEls;
    modal.hidden = false;
    qrImg.removeAttribute('src');
    qrTip.textContent = '正在获取二维码…';
    qrRefresh.hidden = true;
    clearInterval(kgPollTimer);
    let key = '';
    try {
      const j = await kgApi('qr');
      if (!j.ok) throw new Error(j.error || '二维码生成失败');
      key = j.key;
      if (j.img) qrImg.src = j.img;
      qrTip.textContent = '打开酷狗手机版扫一扫';
    } catch (e) {
      qrTip.textContent = '失败：' + e.message;
      return;
    }
    clearInterval(kgPollTimer);
    kgPollTimer = setInterval(async () => {
      try {
        const j = await kgApi('qr/check', { key });
        if (j.status === 1) qrTip.textContent = '等待扫码…';
        else if (j.status === 2) qrTip.textContent = '已扫码，请在手机上确认';
        else if (j.status === 0) {
          qrTip.textContent = '二维码已过期';
          qrRefresh.hidden = false;
          clearInterval(kgPollTimer);
        } else if (j.status === 4) {
          clearInterval(kgPollTimer);
          modal.hidden = true;
          toast('酷狗已登录，VIP 歌曲可以载入完整音质了');
          kgRefreshLoginButton();
        }
      } catch (e) { /* 网络抖动，下一轮再试 */ }
    }, 2000);
  }

  function kgCloseLogin() {
    if (!kgEls) return;
    clearInterval(kgPollTimer);
    kgEls.modal.hidden = true;
  }

  /* 载入新歌后把播放位置归零：上游 loadAudioFile 会保留上一首的位置，
     等音频分析完成（audioName 不再是「正在分析」/报错）就 seek 回 0 */
  function kgSeekZeroWhenLoaded() {
    const t0 = Date.now();
    const timer = setInterval(() => {
      const el = document.getElementById('audioName');
      const txt = el ? el.textContent : '';
      if (/正在分析/.test(txt)) { if (Date.now() - t0 > 60000) clearInterval(timer); return; }
      clearInterval(timer);
      if (/无法载入/.test(txt)) return;
      try { J.uiApi.seek(0); } catch (e) { /* 还没有方案时无需归零 */ }
    }, 200);
  }

  function init() {
    const input = document.getElementById('audioFile');
    if (!input) return;
    input.accept += ',.krc';
    // 本文件在 12_ui.js 之前加载：先注册者先执行，.krc 在这里被拦下
    input.addEventListener('change', intercept);
    // 新歌载入完成后播放位置归零（对话框/在线搜索两条路径都走 change）
    input.addEventListener('change', () => { if (input.files && input.files[0]) kgSeekZeroWhenLoaded(); });

    // 整页拖拽：.krc → 歌词
    window.addEventListener('dragover', e => {
      if (e.dataTransfer && e.dataTransfer.types.includes('Files')) e.preventDefault();
    });
    window.addEventListener('drop', e => {
      const f = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0];
      if (!f) return;
      const n = f.name.toLowerCase();
      if (n.endsWith('.krc')) { e.preventDefault(); handleKrc(f); }
    });

    // 酷狗在线搜索（在「音乐与时间」区块里注入 UI）
    kgInjectUI(input);
    kgRefreshLoginButton();

    // 字幕（详细模式右栏「字幕」区）
    kgSub = kgLoadSubCfg();
    kgInjectSubPanel();

    // 主窗口改了字幕设置/换了歌：镜像浏览器通过 storage 事件实时跟随。
    // kgSub 在页面载入时缓存，不跟随 storage 的话镜像会一直用
    // 旧的内容/位置设置（比如还在渲染音译、位置不动）
    window.addEventListener('storage', e => {
      if (e.key === 'jizura.kgsub') {
        kgSub = kgLoadSubCfg();
        if (kgSubBox) for (const el of kgSubBox.querySelectorAll('[data-k]')) {
          const k = el.dataset.k;
          if (el.type === 'checkbox') el.checked = !!kgSub[k];
          else el.value = kgSub[k];
        }
        return;
      }
      if (e.key !== 'jizura.kgsubmap') return;
      try {
        const saved = JSON.parse(e.newValue || 'null');
        kgLastLyric = saved && saved.map && saved.map.length
          ? { fmt: 'krc', text: saved.text || '', map: new Map(saved.map) }
          : null;
      } catch (err) { /* 忽略 */ }
    });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
