/* 项目持久化：mergeProject（不信任外部输入的合并清洗）+ localStorage 自动保存。
   键与旧版完全一致（jizura.project.v1），老项目无缝兼容。 */
import { J } from '../engine';
import { S } from './state';

export const LS_KEY = 'jizura.project.v1';

export function mergeProject(p: any) {
  const d = J.defaultProject();
  const o = Object.assign(d, p || {});
  o.fx = Object.assign(J.defaultProject().fx, (p && p.fx) || {});
  o.timing = Object.assign(J.defaultProject().timing, (p && p.timing) || {});
  const en = J.defaultProject().enabled;
  for (const g of Object.keys(en)) en[g] = Object.assign(en[g], ((p && p.enabled) || {})[g] || {});
  o.enabled = en;
  o.overrides = (p && p.overrides) || {};
  o.locks = { tech: {}, params: {} };
  // 项目文件不可信：只允许锁 plain key，且只能开（不能是我们没写过的值）
  for (const [g, on] of Object.entries((p && p.locks && p.locks.tech) || {})) if (on === true && /^[\w-]+$/.test(g)) o.locks.tech[g] = true;
  for (const [k, on] of Object.entries((p && p.locks && p.locks.params) || {})) if (on === true && /^[\w-]+$/.test(k)) o.locks.params[k] = true;
  delete o.appVersion;
  // 颜色必须是颜色，字体 key 是 plain key（会进页面的 HTML/CSS）
  o.colors = { enabled: !!(p && p.colors && p.colors.enabled) };
  for (const [k, v] of Object.entries((p && p.colors) || {})) {
    if (k === 'enabled') continue;
    if (typeof v === 'boolean') o.colors[k] = v;
    else if (typeof v === 'string' && /^#[0-9a-f]{3,8}$/i.test(v)) o.colors[k] = v;
  }
  o.userFonts = (Array.isArray(p && p.userFonts) ? p.userFonts : []).filter((uf: any) => uf && J.SAFE_FONT_KEY.test(uf.key))
    .map((uf: any) => ({ key: uf.key, label: String(uf.label || uf.key).slice(0, 80), family: J.safeFamily(uf.family || uf.key.slice(5)), weight: J.clamp(parseInt(uf.weight, 10) || 400, 100, 900) }));
  for (const uf of o.userFonts) if (!J.FONTS[uf.key]) J.addUserFont(uf.key, uf.label, uf.family, uf.weight);
  o.fonts = {};
  for (const [role, k] of Object.entries((p && p.fonts) || {})) if (typeof k === 'string' && J.FONTS[k] && /^[\w-]+$/.test(role)) o.fonts[role] = k;
  return o;
}

export function loadLocal() {
  try { const s = localStorage.getItem(LS_KEY); if (s) return mergeProject(JSON.parse(s)); } catch {}
  return mergeProject(null);
}

let saveTimer = 0;
export function autosave() { clearTimeout(saveTimer); saveTimer = setTimeout(flushSave, 700); }
export function flushSave() { clearTimeout(saveTimer); try { localStorage.setItem(LS_KEY, JSON.stringify(S.project)); } catch {} }
window.addEventListener('pagehide', () => { if (S.project) flushSave(); });

export async function resetAllProject() {
  S.project = mergeProject(null);
  S.project.lyrics = '';
  S.audio = null;
  if (J.forgetSong) await J.forgetSong();
  try { localStorage.removeItem('jizura.kgsubmap'); } catch {}
}
