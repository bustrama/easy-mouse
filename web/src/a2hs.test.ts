import { describe, expect, it } from 'vitest';
import { detectPlatform, isStandalone, loadFlags, MAX_AUTO, recordShown, setNever, shouldAutoShow, type Kv } from './a2hs.ts';
import { translate } from './i18n/strings.ts';

const IPHONE = 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1';
const IPHONE_CHROME = 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/126.0.0.0 Mobile/15E148 Safari/604.1';
const IPAD_DESKTOP = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Safari/605.1.15';
const ANDROID_CHROME = 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36';
const SAMSUNG = 'Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/25.0 Chrome/121.0.0.0 Mobile Safari/537.36';
const ANDROID_FIREFOX = 'Mozilla/5.0 (Android 14; Mobile; rv:127.0) Gecko/127.0 Firefox/127.0';
const DESKTOP = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36';

describe('platform detection', () => {
  it('detects iOS Safari', () => {
    expect(detectPlatform(IPHONE)).toBe('ios-safari');
    expect(detectPlatform(IPAD_DESKTOP, 5)).toBe('ios-safari');
  });
  it('treats a Mac user agent without touch as other', () => {
    expect(detectPlatform(IPAD_DESKTOP, 0)).toBe('other');
  });
  it('treats other iOS browsers as other', () => {
    expect(detectPlatform(IPHONE_CHROME)).toBe('other');
  });
  it('detects Android Chrome only', () => {
    expect(detectPlatform(ANDROID_CHROME)).toBe('android-chrome');
    expect(detectPlatform(SAMSUNG)).toBe('other');
    expect(detectPlatform(ANDROID_FIREFOX)).toBe('other');
    expect(detectPlatform(DESKTOP)).toBe('other');
  });
});

describe('standalone check', () => {
  const mm = (matches: boolean) => () => ({ matches });
  it('uses navigator.standalone (iOS)', () => {
    expect(isStandalone({ standalone: true }, mm(false))).toBe(true);
  });
  it('uses display-mode: standalone', () => {
    expect(isStandalone({}, mm(true))).toBe(true);
  });
  it('is false in a normal tab or without matchMedia', () => {
    expect(isStandalone({ standalone: false }, mm(false))).toBe(false);
    expect(isStandalone({}, undefined)).toBe(false);
  });
  it('survives matchMedia throwing', () => {
    expect(
      isStandalone({}, () => {
        throw new Error('x');
      }),
    ).toBe(false);
  });
});

function memory(): Kv {
  const data = new Map<string, string>();
  return { get: (k) => data.get(k) ?? null, set: (k, v) => void data.set(k, v) };
}
const base = { standalone: false, justPaired: true, offeredThisSession: false };

describe('show-once logic', () => {
  it('shows right after the first pairing', () => {
    expect(shouldAutoShow({ never: false, shown: 0 }, base)).toBe(true);
  });
  it('does not show for an already paired device that was never offered', () => {
    expect(shouldAutoShow({ never: false, shown: 0 }, { ...base, justPaired: false })).toBe(false);
  });
  it('does not show twice in one session', () => {
    expect(shouldAutoShow({ never: false, shown: 0 }, { ...base, offeredThisSession: true })).toBe(false);
  });
  it('"Not now" brings it back once in a later session, then stops', () => {
    const later = { ...base, justPaired: false };
    expect(shouldAutoShow({ never: false, shown: 1 }, later)).toBe(true);
    expect(shouldAutoShow({ never: false, shown: MAX_AUTO }, later)).toBe(false);
  });
  it('never shows again after "Don\'t show again"', () => {
    expect(shouldAutoShow({ never: true, shown: 0 }, base)).toBe(false);
    expect(shouldAutoShow({ never: true, shown: 1 }, { ...base, justPaired: false })).toBe(false);
  });
  it('never shows from the home screen', () => {
    expect(shouldAutoShow({ never: false, shown: 0 }, { ...base, standalone: true })).toBe(false);
  });
  it('persists flags and tolerates garbage', () => {
    const kv = memory();
    expect(loadFlags(kv)).toEqual({ never: false, shown: 0 });
    recordShown(kv);
    expect(loadFlags(kv)).toEqual({ never: false, shown: 1 });
    setNever(kv);
    expect(loadFlags(kv)).toEqual({ never: true, shown: 1 });
    kv.set('em.a2hs', '{oops');
    expect(loadFlags(kv)).toEqual({ never: false, shown: 0 });
  });
  it('keeps working when storage is blocked', () => {
    const blocked: Kv = { get: () => null, set: () => undefined };
    recordShown(blocked);
    expect(loadFlags(blocked)).toEqual({ never: false, shown: 0 });
  });
});

describe('strings', () => {
  it('has Hebrew for the iOS storage warning', () => {
    expect(translate('he', 'a2hs.ios.3')).not.toBe(translate('en', 'a2hs.ios.3'));
  });
});
