// 项目持久化：mergeProject（不信任外部输入的合并清洗）+ localStorage 自动保存。
// 键保持 jizura.project.v1，老项目无缝兼容。
import {J} from '../engine';
import {S} from './state';

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
  // 音频特效（预览+导出共用）：不信任外部输入，统一清洗
  o.audioFx = J.sanitizeAudioFx((p && p.audioFx) || {});
  o.locks = {tech: {}, params: {}};
  // 项目文件不可信：只允许锁 plain key，且只能开（不能是我们没写过的值）
  for (const [g, on] of Object.entries((p && p.locks && p.locks.tech) || {})) if (on === true && /^[\w-]+$/.test(g)) o.locks.tech[g] = true;
  for (const [k, on] of Object.entries((p && p.locks && p.locks.params) || {})) if (on === true && /^[\w-]+$/.test(k)) o.locks.params[k] = true;
  delete o.appVersion;
  // 颜色必须是颜色，字体 key 是 plain key（会进页面的 HTML/CSS）
  o.colors = {enabled: !!(p && p.colors && p.colors.enabled)};
  for (const [k, v] of Object.entries((p && p.colors) || {})) {
    if (k === 'enabled') continue;
    if (typeof v === 'boolean') o.colors[k] = v;
    else if (typeof v === 'string' && /^#[0-9a-f]{3,8}$/i.test(v)) o.colors[k] = v;
  }
  o.userFonts = (Array.isArray(p && p.userFonts) ? p.userFonts : []).filter((uf: any) => uf && J.SAFE_FONT_KEY.test(uf.key))
      .map((uf: any) => ({
        key: uf.key,
        label: String(uf.label || uf.key).slice(0, 80),
        family: J.safeFamily(uf.family || uf.key.slice(5)),
        weight: J.clamp(parseInt(uf.weight, 10) || 400, 100, 900)
      }));
  for (const uf of o.userFonts) if (!J.FONTS[uf.key]) J.addUserFont(uf.key, uf.label, uf.family, uf.weight);
  o.themeId = p && typeof p.themeId === 'string' && J.THEMES && J.THEMES[p.themeId] ? p.themeId : null;
  migrateOrder(o, p);
  o.fonts = {};
  for (const [role, k] of Object.entries((p && p.fonts) || {})) if (typeof k === 'string' && J.FONTS[k] && /^[\w-]+$/.test(role)) o.fonts[role] = k;
  return o;
}

// v0.10 起混合 LRC 的无戳行不再排到最后，老工程里按行号存的逐行设置（时刻、指定）
// 要搬到新行序上。只搬一次，用 timingOrder 标记。
function migrateOrder(o: any, p: any) {
  const was = p && p.timingOrder;
  o.timingOrder = 2;
  if (!p || was === 2) return;
  const map = J.parseOrderV1 ? J.parseOrderV1(o.lyrics || '') : null;
  if (!map) return;
  const mv = (obj: any) => {
    const r: any = {};
    for (const [k, v] of Object.entries(obj || {})) {
      const i = +k;
      r[Number.isInteger(i) && map[i] != null ? map[i] : k] = v;
    }
    return r;
  };
  o.timing.lineTimes = mv(o.timing.lineTimes);
  o.overrides = mv(o.overrides);
  // exportRange 不迁移：上游按行存，本 fork 按切分存，语义不同无法按行号映射
}

export function loadLocal() {
  try {
    const s = localStorage.getItem(LS_KEY);
    if (s) return mergeProject(JSON.parse(s));
  } catch {
  }
  return mergeProject(null);
}

let saveTimer = 0;

export function autosave() {
  clearTimeout(saveTimer);
  saveTimer = setTimeout(flushSave, 700);
}

export function flushSave() {
  clearTimeout(saveTimer);
  try {
    localStorage.setItem(LS_KEY, JSON.stringify(S.project));
  } catch {
  }
}

window.addEventListener('pagehide', () => {
  if (S.project) flushSave();
});

export async function resetAllProject() {
  S.project = mergeProject(null);
  S.project.lyrics = '';
  S.audio = null;
  if (J.forgetSong) await J.forgetSong();
  try {
    localStorage.removeItem('jizura.kgsubmap');
  } catch {
  }
}
