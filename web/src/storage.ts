import { b64decode, b64encode } from './protocol/b64.ts';
import type { Credentials } from './protocol/session.ts';

// Browser storage can throw (private mode, blocked site data) or be absent: every access is guarded
// and the app keeps working in memory.

export function storageGet(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

export function storageSet(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    /* ignore */
  }
}

export function storageRemove(key: string): void {
  try {
    localStorage.removeItem(key);
  } catch {
    /* ignore */
  }
}

const CREDS = 'em.creds';

export function loadCredentials(): Credentials | null {
  const raw = storageGet(CREDS);
  if (!raw) return null;
  try {
    const o = JSON.parse(raw) as { id?: unknown; secret?: unknown };
    if (typeof o.id !== 'string' || typeof o.secret !== 'string') return null;
    const secret = b64decode(o.secret);
    if (secret.length !== 32 || b64decode(o.id).length !== 8) return null;
    return { id: o.id, secret };
  } catch {
    return null;
  }
}

export function saveCredentials(c: Credentials): void {
  storageSet(CREDS, JSON.stringify({ id: c.id, secret: b64encode(c.secret) }));
}

export function clearCredentials(): void {
  storageRemove(CREDS);
}
