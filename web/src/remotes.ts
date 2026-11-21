// Declarative remotes (docs/architecture.md, "Remotes"). The PC sends JSON; the phone only renders it.
// Everything is validated and copied into a plain structure: labels become text nodes in the UI,
// icons must come from the built-in set, nothing is ever evaluated or fetched.

import { ICON_NAMES } from './ui/iconPaths.ts';

export type RemoteItem =
  | { type: 'button'; icon?: string; label?: string; action: unknown }
  | { type: 'slider'; bind: string; label?: string }
  | { type: 'label'; bind?: string; text?: string };

export interface Remote {
  id: string;
  name: string;
  cols: number;
  items: RemoteItem[];
}

export interface RemoteInfo {
  id: string;
  name: string;
}

const MAX_ITEMS = 64;
const str = (v: unknown, max = 64): string | undefined => (typeof v === 'string' ? v.slice(0, max) : undefined);
const isObj = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);

export function sanitizeRemoteList(msg: unknown): RemoteInfo[] {
  if (!isObj(msg) || !Array.isArray(msg['list'])) return [];
  const out: RemoteInfo[] = [];
  for (const r of msg['list'].slice(0, 32)) {
    if (!isObj(r)) continue;
    const id = str(r['id'], 64);
    const name = str(r['name'], 40);
    if (id && name) out.push({ id, name });
  }
  return out;
}

export function sanitizeRemote(raw: unknown): Remote | null {
  if (!isObj(raw) || raw['schema'] !== 1) return null;
  const id = str(raw['id']);
  const name = str(raw['name'], 40);
  const layout = raw['layout'];
  if (!id || !name || !isObj(layout) || !Array.isArray(layout['items'])) return null;
  const colsRaw = Number(layout['cols']);
  const cols = Number.isInteger(colsRaw) ? Math.max(1, Math.min(4, colsRaw)) : 2;
  const items: RemoteItem[] = [];
  for (const it of layout['items'].slice(0, MAX_ITEMS)) {
    if (!isObj(it)) continue;
    const label = str(it['label'], 40);
    switch (it['type']) {
      case 'button': {
        const icon = typeof it['icon'] === 'string' && ICON_NAMES.has(it['icon']) ? it['icon'] : undefined;
        if (!icon && !label) continue;
        items.push({ type: 'button', icon, label, action: it['action'] ?? null });
        break;
      }
      case 'slider': {
        const bind = str(it['bind'], 64);
        if (bind) items.push({ type: 'slider', bind, label });
        break;
      }
      case 'label': {
        const bind = str(it['bind'], 64);
        const text = str(it['text'], 80) ?? label;
        if (bind || text) items.push({ type: 'label', bind, text });
        break;
      }
      default:
        break; // unknown item types are skipped
    }
  }
  return { id, name, cols, items };
}
