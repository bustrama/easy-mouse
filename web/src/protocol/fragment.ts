import { b64decode } from './b64.ts';

/** Parses `#k=<b64 16 bytes>`; null when absent or malformed. */
export function parsePairingFragment(hash: string): Uint8Array | null {
  const m = /^#(?:.*&)?k=([A-Za-z0-9_-]+)(?:&.*)?$/.exec(hash);
  if (!m) return null;
  try {
    const k = b64decode(m[1]!);
    return k.length === 16 ? k : null;
  } catch {
    return null;
  }
}

interface Loc {
  hash: string;
  pathname: string;
  search: string;
}
interface Hist {
  replaceState(data: unknown, unused: string, url?: string | null): void;
}

/**
 * Reads the one-time secret from the URL fragment and removes it from the address bar at once,
 * so it never lingers in history, bookmarks or screenshots. The secret stays in memory only.
 */
export function takePairingSecret(loc: Loc, hist: Hist): Uint8Array | null {
  const k = parsePairingFragment(loc.hash);
  if (loc.hash.includes('k=')) hist.replaceState(null, '', loc.pathname + loc.search);
  return k;
}
