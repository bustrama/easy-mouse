import { storageGet, storageSet } from './storage.ts';

// "Add to Home Screen" suggestion. The app is served over plain HTTP, so there is no service worker
// and no install prompt: all we can do is explain the browser's own menu. This file holds the pure
// logic (platform, standalone check, show-once rules); the sheet lives in ui/AddToHome.tsx.

export type A2hsPlatform = 'ios-safari' | 'android-chrome' | 'other';

export function detectPlatform(ua: string, maxTouchPoints = 0): A2hsPlatform {
  // iPadOS 13+ reports a desktop Mac user agent; it is the touch screen that gives it away.
  const ios = /iPhone|iPad|iPod/.test(ua) || (/Macintosh/.test(ua) && maxTouchPoints > 1);
  if (ios) {
    // Chrome, Firefox, Edge, Opera and the Google app on iOS have other menus: Safari only.
    return /CriOS|FxiOS|EdgiOS|OPiOS|GSA\/|DuckDuckGo/.test(ua) ? 'other' : 'ios-safari';
  }
  if (/Android/.test(ua) && /Chrome\//.test(ua) && !/EdgA|OPR\/|SamsungBrowser|Firefox|Vivaldi|YaBrowser|UCBrowser/.test(ua)) return 'android-chrome';
  return 'other';
}

/** True when the page already runs from a home-screen icon as a standalone app. */
export function isStandalone(nav: { standalone?: boolean }, mm: ((q: string) => { matches: boolean }) | undefined): boolean {
  if (nav.standalone === true) return true;
  try {
    return !!mm?.('(display-mode: standalone)').matches;
  } catch {
    return false;
  }
}

export interface Kv {
  get(key: string): string | null;
  set(key: string, value: string): void;
}
const defaultKv: Kv = { get: storageGet, set: storageSet };

export interface A2hsFlags {
  /** "Don't show again" was chosen */
  never: boolean;
  /** how many times the suggestion was shown on its own */
  shown: number;
}

const KEY = 'em.a2hs';
/** The first offer comes right after pairing; "Not now" allows exactly one more, in a later session. */
export const MAX_AUTO = 2;

export function loadFlags(kv: Kv = defaultKv): A2hsFlags {
  const raw = kv.get(KEY);
  if (!raw) return { never: false, shown: 0 };
  try {
    const o = JSON.parse(raw) as { never?: unknown; shown?: unknown };
    const shown = typeof o.shown === 'number' && Number.isFinite(o.shown) ? Math.max(0, Math.floor(o.shown)) : 0;
    return { never: o.never === true, shown };
  } catch {
    return { never: false, shown: 0 };
  }
}

function saveFlags(f: A2hsFlags, kv: Kv): void {
  kv.set(KEY, JSON.stringify(f));
}

export interface OfferContext {
  standalone: boolean;
  /** the device was paired during this page load */
  justPaired: boolean;
  /** the suggestion was already offered during this page session */
  offeredThisSession: boolean;
}

/** Whether to show the suggestion by itself now. Callers only ask once the device is paired and online. */
export function shouldAutoShow(flags: A2hsFlags, ctx: OfferContext): boolean {
  if (ctx.standalone || flags.never || ctx.offeredThisSession) return false;
  if (ctx.justPaired) return flags.shown === 0;
  // a later session: only a postponed ("Not now") suggestion comes back
  return flags.shown >= 1 && flags.shown < MAX_AUTO;
}

export function recordShown(kv: Kv = defaultKv): void {
  const f = loadFlags(kv);
  saveFlags({ ...f, shown: f.shown + 1 }, kv);
}

export function setNever(kv: Kv = defaultKv): void {
  saveFlags({ ...loadFlags(kv), never: true }, kv);
}

let offered = false;

/** Decides, and when yes records that it was shown. Call when the online screen appears. */
export function takeAutoOffer(justPaired: boolean, kv: Kv = defaultKv): boolean {
  const standalone = isStandalone(navigator as { standalone?: boolean }, typeof matchMedia === 'function' ? matchMedia.bind(globalThis) : undefined);
  if (!shouldAutoShow(loadFlags(kv), { standalone, justPaired, offeredThisSession: offered })) return false;
  offered = true;
  recordShown(kv);
  return true;
}

export const platform = (): A2hsPlatform => detectPlatform(navigator.userAgent, navigator.maxTouchPoints);
