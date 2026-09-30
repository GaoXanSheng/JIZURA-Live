#!/usr/bin/env node
/* 把 Google Fonts（JIZURA 字体表用到的全部家族）抓到本地自托管，彻底去掉字体 CDN 网络依赖。
 *
 * 用法：  node scripts/fetch-fonts.mjs [--force]
 * 产物：  public/fonts/css/<spec>.css   每个字体规格一份切片 CSS（unicode-range 保留，
 *                                       url() 已改写为本地相对路径）
 *         public/fonts/files/*.woff2    全部切片字体文件（按 URL 文件名去重）
 *
 * - spec 列表直接从 src/engine/02_fonts.js 的 `gf: '...'` 解析（自维护，加字体不用改这里），
 *   另补 Noto Sans/Serif JP 的可变字重规格（太さ @var 用）。
 * - 双源：fonts.googleapis.com → fonts.loli.net（大陆镜像），文件源 gstatic.com → gstatic.loli.net。
 * - 已存在的文件跳过（断点续传）；--force 全部重抓。
 * - 字体均为 OFL 开源协议，自托管合规；目录已 gitignore（约 40-80MB，构建时随 public/ 打进应用）。
 */
import {mkdir, readFile, writeFile} from 'node:fs/promises';
import {existsSync} from 'node:fs';
import {dirname, join, resolve} from 'node:path';
import {fileURLToPath} from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const outDir = join(root, 'public', 'fonts');
const cssDir = join(outDir, 'css');
const fileDir = join(outDir, 'files');
const FORCE = process.argv.includes('--force');

const CSS_HOSTS = ['https://fonts.googleapis.com', 'https://fonts.loli.net'];
const FILE_HOSTS = ['https://fonts.gstatic.com', 'https://gstatic.loli.net'];
const UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36';

async function fetchWithHosts(path, hosts, as = 'text') {
  let lastErr;
  for (const h of hosts) {
    for (let attempt = 0; attempt < 2; attempt++) {
      try {
        const r = await fetch(h + path, { headers: { 'User-Agent': UA }, signal: AbortSignal.timeout(30000) });
        if (!r.ok) throw new Error(`HTTP ${r.status}`);
        return { body: as === 'text' ? await r.text() : Buffer.from(await r.arrayBuffer()), host: h };
      } catch (e) { lastErr = e; }
    }
  }
  throw lastErr || new Error('all hosts failed');
}

async function main() {
  await mkdir(cssDir, { recursive: true });
  await mkdir(fileDir, { recursive: true });

  // 1. 解析引擎源码里的全部字体规格（自维护，加字体不用改这里）
  const fontsSrc = await readFile(join(root, 'src', 'engine', '02_fonts.js'), 'utf8');
  const langSrc = await readFile(join(root, 'src', 'engine', '02b_lang.js'), 'utf8');
  // 02b_lang.js 的 LANG_FACES：const 常量（含一行多声明）+ F(family, weight, 'spec'|常量) 调用
  const consts = new Map([...langSrc.matchAll(/(\w+)\s*=\s*'([A-Za-z0-9+:@.;]+)'/g)].map(m => [m[1], m[2]]));
  const langSpecs = [];
  for (const m of langSrc.matchAll(/F\('[^']+',\s*\d+,\s*'([^']+)'\)/g)) langSpecs.push(m[1]);
  for (const m of langSrc.matchAll(/F\('[^']+',\s*\d+,\s*(\w+)\)/g)) { if (consts.has(m[1])) langSpecs.push(consts.get(m[1])); }
  const specs = [...new Set([
    ...[...fontsSrc.matchAll(/gf:\s*'([^']+)'/g)].map(m => m[1]),
    ...langSpecs,
    'Noto+Sans+JP:wght@100..900',       // 太さ @var：可变字重
    'Noto+Serif+JP:wght@200..900',
  ])];
  console.log(`specs: ${specs.length}`);

  let files = 0, bytes = 0, cssOk = 0;
  const failed = [];
  const startedAt = new Date().toISOString();

  for (const spec of specs) {
    const safe = spec.replace(/[^\w.-]+/g, '_');
    const cssPath = `/css2?family=${spec}&display=swap`;
    const cssFile = join(cssDir, `${safe}.css`);
    if (!FORCE && existsSync(cssFile)) { cssOk++; console.log(`skip css  ${spec}`); continue; }

    let css;
    try { ({ body: css } = await fetchWithHosts(cssPath, CSS_HOSTS)); }
    catch (e) { failed.push(`${spec} (css: ${e.message})`); continue; }

    // 2. 改写 url() 到本地并下载 woff2（按文件名去重，多规格共享同一切片）
    //    路径必须写成 ../files/：css 落在 fonts/css/ 下，浏览器按 css 所在目录解析相对路径，
    //    写 ./files/ 会解析成 /fonts/css/files/（404），文件实际在 /fonts/files/
    const urls = [...new Set([...css.matchAll(/url\((https:[^)]+)\)/g)].map(m => m[1]))];
    let i = 0;
    for (const u of urls) {
      const name = u.split('/').pop().split('?')[0];
      const local = join(fileDir, name);
      if (!FORCE && existsSync(local)) {
        css = css.split(u).join('../files/' + name);
        continue;
      }
      const path = new URL(u).pathname + new URL(u).search;
      try {
        const { body } = await fetchWithHosts(path, FILE_HOSTS, 'bin');
        await writeFile(local, body);
        files++; bytes += body.length;
        process.stdout.write(`\r${files} files, ${(bytes / 1048576).toFixed(1)} MB`);
      } catch (e) { failed.push(`${name} (${e.message})`); }
      css = css.split(u).join('../files/' + name);
      i++;
    }
    await writeFile(cssFile, css);
    cssOk++;
    console.log(`\nok  css   ${spec}  (${urls.length} refs)`);
  }

  await writeFile(join(outDir, 'manifest.json'), JSON.stringify({
    generatedAt: startedAt, specs, cssFiles: cssOk, fontFiles: files, bytes,
    source: 'Google Fonts (OFL), hosts: ' + CSS_HOSTS.join(', '),
    usage: 'engine 02_fonts.js attachFamily() 优先加载 /fonts/css/<spec>.css，缺失时回退 CDN',
  }, null, 1));

  console.log(`\ndone: ${cssOk}/${specs.length} css, ${files} new files, ${(bytes / 1048576).toFixed(1)} MB`);
  if (failed.length) { console.log('FAILED:'); for (const f of failed) console.log('  ' + f); process.exit(1); }
}

main();
