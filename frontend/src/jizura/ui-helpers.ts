/* 徽章（追加/和/ホ・文・キ等套装标记）与通用 UI 帮手 */
import { J } from '../engine';

const SET_UI: Record<string, { name: string; badge: string }> = {
  horror: { name: '恐怖', badge: 'ホ' },
  typo: { name: '文字PV系', badge: '文' },
  kinetic: { name: '动感', badge: 'キ' },
};

export interface Badge { text: string; cls: string; title: string }

export function badgesOf(d: any): Badge[] {
  const out: Badge[] = [];
  if (!d) return out;
  if (d.extra) out.push({ text: '追加', cls: 'ex', title: '最初公开版之后追加' });
  if (d.wa) out.push({ text: '和', cls: '', title: '和风演出' });
  if (d.set && SET_UI[d.set]) out.push({ text: SET_UI[d.set].badge, cls: `set-${d.set}`, title: SET_UI[d.set].name });
  return out;
}

export function swatchHTML(cols: string[]) { return cols.filter(c => /^#[0-9a-f]{3,8}$/i.test(String(c))); }

export function toColorInput(v: string) {
  const h = String(v || '#000000');
  return /^#[0-9a-f]{6}$/i.test(h) ? h.toLowerCase() : J.toHex(...J.hex(h)).toLowerCase();
}
