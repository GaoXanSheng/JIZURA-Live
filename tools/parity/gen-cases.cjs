// 生成模糊对拍 case：确定性种子，覆盖边界形态（LRC 混排/间奏/手切/强调/各风格/画幅/BPM/行时刻）。
// 产物：tools/parity/cases/fuzzNN.json
const fs = require('fs');
const path = require('path');

// mulberry32（与引擎同源，保证 case 集可复现）
function rng32(seed) {
  let s = seed >>> 0;
  return () => {
    s = (s + 0x6d2b79f5) >>> 0;
    let t = s;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const r = rng32(0x51A3EA);
const pick = a => a[Math.floor(r() * a.length)];
const ri = (lo, hi) => lo + Math.floor(r() * (hi - lo + 1));

const LINES = [
  '夜明けの色を/覚えてる',
  'ほどけた声が遠くで鳴った',
  'ねえ、まだ間に合うかな',
  '*透明*なままじゃ終われない!',
  '[00:12.00]水平線まであと少し',
  'good night, see you tomorrow',
  '[00:18.50]三、二、一、発車!',
  'ラララ〜♪',
  '[間奏 2]',
  '「引用」はカギ括弧',
  'English mixed with 日本語',
  '[00:24.00]最後の*一行*!'
];
const STYLES = ['noir', 'crimson', 'caution', 'magenta', 'paper', 'hud', 'mint', 'specimen', 'transit', 'blueprint', 'rouge', 'mono'];
const ASPECTS = ['16:9', '9:16', '1:1', '4:5', '21:9', '4:3', '3:4'];

const N = 20;
for (let i = 0; i < N; i++) {
  const nLines = ri(1, 8);
  const rows = [];
  for (let k = 0; k < nLines; k++) rows.push(pick(LINES));
  const useBpm = r() < 0.5;
  const project = {
    lyrics: rows.join('\n'),
    title: r() < 0.7 ? pick(['テスト曲', '夜明け', 'SONG ' + i, '']) : '',
    artist: r() < 0.6 ? pick(['歌手', 'artist ' + i, '']) : '',
    seed: ri(1, 9999999),
    style: pick(STYLES),
    aspect: pick(ASPECTS),
    fps: pick([12, 24, 30]),
    unify: r() < 0.3,
    typeset: r() < 0.3,
    extra: r() < 0.5,
    horror: r() < 0.15,
    typo: r() < 0.7,
    kinetic: r() < 0.7,
    lang: pick(['auto', 'ja', 'en', 'zh-Hans']),
    seed2: undefined,
    timing: {
      bpm: useBpm ? pick([96, 128, 172]) : 0,
      offset: pick([0, 0.4, 1.2]),
      snap: useBpm && r() < 0.6,
      tail: pick([0.5, 0.9, 1.5]),
      lineScale: pick([1, 1.2, 0.8]),
      lineTimes: r() < 0.3 ? { [String(ri(0, Math.max(0, nLines - 1)))]: +(r() * 20).toFixed(2) } : {}
    },
    fx: {
      motion: +(r()).toFixed(2),
      glitch: +(r()).toFixed(2),
      chroma: +(r()).toFixed(2),
      decor: +(r()).toFixed(2),
      density: +(r()).toFixed(2),
      texture: +(r()).toFixed(2),
      koma: pick([0, 8, 12, 24]),
      bgSwitch: +(r()).toFixed(2)
    }
  };
  // 音频（部分 case 带合成节拍/能量，走 snap 与能量路径）
  let audio = null;
  if (useBpm && r() < 0.6) {
    const dur = ri(30, 90), bpm = project.timing.bpm;
    const p60 = 60 / bpm, beats = [];
    for (let t = project.timing.offset; t < dur + 0.01; t += p60) if (t >= 0) beats.push(+t.toFixed(4));
    const energy = Array.from({ length: dur * 50 }, (_, k) => Math.abs(Math.sin(k / 8 + i)));
    audio = { duration: dur, bpm, beats, energyRate: 50, energy, peaks: Array.from({ length: 400 }, (_, k) => Math.sin(k / 5)) };
  }
  const c = { project, audio, times: [0.5, 1.2, 2.5, 4.0] };
  fs.writeFileSync(path.join(__dirname, 'cases', 'fuzz' + String(i).padStart(2, '0') + '.json'), JSON.stringify(c));
}
console.log('generated', N, 'cases');
