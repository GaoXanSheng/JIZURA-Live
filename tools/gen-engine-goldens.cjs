// 生成 C# 引擎移植的跨语言黄金值（对拍 01_util 的 rng/h/sid、10_audio 的 beatGrid、
// 08_planner 的 parseLyrics/computeTiming）。产物：obj/engine-goldens.json
global.window = {};
const fs = require('fs');
const read = f => fs.readFileSync(f, 'utf8');
eval(read('frontend/src/engine/01_util.js'));
const J = window.J;

function extractFn(src, name) {
  const key = 'J.' + name + ' =';
  const i = src.indexOf(key);
  if (i < 0) throw new Error('not found: ' + name);
  let j = src.indexOf('{', i), depth = 0, k = j;
  for (; k < src.length; k++) {
    if (src[k] === '{') depth++;
    else if (src[k] === '}') { depth--; if (depth === 0) break; }
  }
  return src.slice(i + key.length, k + 1).trim();
}

eval('window.J.beatGrid = ' + extractFn(read('frontend/src/engine/10_audio.js'), 'beatGrid'));
eval('window.J.computeTiming = ' + extractFn(read('frontend/src/engine/08_planner.js'), 'computeTiming'));
eval('window.J.parseLyrics = ' + extractFn(read('frontend/src/engine/08_planner.js'), 'parseLyrics'));

const out = {};
const r = J.rng(42);
out.rng42 = [r(), r(), r(), r(), r(), r()];
out.h123 = J.h(1, 2, 3);
out.hSeed = J.h(20260922, 999);
out.hFive = J.h(0x7fffffff, -5, 123456789, -987654321, 42);
out.sid = J.sid('layout:center');
out.noise1 = [J.noise1(3.7, 11), J.noise1(-1.2, 99)];

const bg = J.beatGrid(120, 0, 60);
out.beatLen = bg.length;
out.beatFirst3 = bg.slice(0, 3);
out.beatLast = bg[bg.length - 1];
const bg2 = J.beatGrid(120, 0.25, 30);
out.beatOffsetFirst3 = bg2.slice(0, 3);
const bg3 = J.beatGrid(0, 0, 30);
out.beatZeroBpm = bg3.length;

const LYRICS = [
  '# 测试歌词',
  '[00:02.00]こんにちは世界',
  '[00:06.50]ふたつめの*ライン*!',
  '[00:10.00]三行目 / 二段目',
  '[00:13.00][間奏 3]',
  '[00:16.00]ラスト',
].join('\n');
out.parsed = J.parseLyrics(LYRICS);

const proj = { timing: { bpm: 0, offset: 0.4, snap: false, tail: 0.9, lineTimes: {}, lineScale: 1 } };
const tm = J.computeTiming(proj, out.parsed, { duration: 120 });
out.tmStarts = tm.starts;
out.tmEnds = tm.ends;
out.tmDuration = tm.duration;

// computeTiming：带 BPM + LRC 戳 + 手工行时刻（走整条推挤逻辑）
const LYRICS2 = [
  '[00:01.00]一行目',
  '二行目ここから',
  '[00:09.00]三行目',
  '[間奏 2]',
  '四行目',
].join('\n');
out.parsed2 = J.parseLyrics(LYRICS2);
const tm2 = J.computeTiming({ timing: { bpm: 128, offset: 0.4, snap: true, tail: 0.9, lineTimes: { '2': 12.5 }, lineScale: 1 } }, out.parsed2, null);
out.tm2Starts = tm2.starts;
out.tm2Ends = tm2.ends;
out.tm2Duration = tm2.duration;

fs.mkdirSync('obj', { recursive: true });
fs.writeFileSync('obj/engine-goldens.json', JSON.stringify(out, null, 1));
console.log('goldens written:', Object.keys(out).join(','));
