// DEPRECATED: disabled for 0.1, scheduled for removal (docs/remotes.md).
// Declarative remotes (docs/architecture.md, "Remotes"). The PC sends the layouts; the phone only
// renders them and names the item the user pressed (`invoke`). It never sends or runs an action.
// Everything is validated and copied into a plain structure: labels become text nodes in the UI,
// icons must come from the built-in set, nothing is ever evaluated or fetched.

import { ICON_NAMES } from './ui/iconPaths.ts';

export type RemoteItem =
  | { type: 'button'; id: string; icon?: string; label?: string }
  | { type: 'slider'; id: string; bind?: string; label?: string }
  | { type: 'label'; id: string; bind?: string; text?: string };

export interface Remote {
  id: string;
  name: string;
  cols: number;
  items: RemoteItem[];
}

const MAX_ITEMS = 64;
const MAX_REMOTES = 32;
const str = (v: unknown, max = 64): string | undefined => (typeof v === 'string' ? v.slice(0, max) : undefined);
const isObj = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);

/** `{ t:"remotes", remotes:[ <remote JSON, schema 1>, ... ] }` pushed by the PC. */
export function sanitizeRemotes(msg: unknown): Remote[] {
  if (!isObj(msg) || !Array.isArray(msg['remotes'])) return [];
  const out: Remote[] = [];
  for (const raw of msg['remotes'].slice(0, MAX_REMOTES)) {
    const r = sanitizeRemote(raw);
    if (r) out.push(r);
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
  layout['items'].slice(0, MAX_ITEMS).forEach((it: unknown, index) => {
    if (!isObj(it)) return;
    // The item id defaults to its zero-based position in `layout.items` (docs/protocol.md).
    const itemId = str(it['id'], 64) ?? String(index);
    const label = str(it['label'], 40);
    switch (it['type']) {
      case 'button': {
        const icon = typeof it['icon'] === 'string' && ICON_NAMES.has(it['icon']) ? it['icon'] : undefined;
        if (icon || label) items.push({ type: 'button', id: itemId, icon, label });
        break;
      }
      case 'slider':
        items.push({ type: 'slider', id: itemId, bind: str(it['bind'], 64), label });
        break;
      case 'label': {
        const bind = str(it['bind'], 64);
        const text = str(it['text'], 80) ?? label;
        if (bind || text) items.push({ type: 'label', id: itemId, bind, text });
        break;
      }
      default:
        break; // unknown item types are skipped
    }
  });
  return { id, name, cols, items };
}
