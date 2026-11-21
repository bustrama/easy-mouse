import { describe, expect, it } from 'vitest';
import { detectLang, translate } from './i18n/strings.ts';
import { formatTime, parseState } from './pcstate.ts';
import { sanitizeRemote, sanitizeRemoteList } from './remotes.ts';

describe('remote sanitising', () => {
  const good = {
    schema: 1,
    id: 'stremio',
    name: 'Stremio',
    layout: {
      cols: 4,
      items: [
        { type: 'button', icon: 'play', action: { do: 'smtc', cmd: 'toggle' } },
        { type: 'button', label: 'Fullscreen', action: [{ do: 'focus' }, { do: 'key', keys: 'F' }] },
        { type: 'slider', bind: 'volume.app', action: { do: 'volume' } },
        { type: 'label', bind: 'smtc.title' },
      ],
    },
  };

  it('accepts the architecture.md example', () => {
    const r = sanitizeRemote(good)!;
    expect(r.cols).toBe(4);
    expect(r.items.map((i) => i.type)).toEqual(['button', 'button', 'slider', 'label']);
  });

  it('drops unknown icons, unknown item types and iconless/labelless buttons', () => {
    const r = sanitizeRemote({
      ...good,
      layout: {
        cols: 2,
        items: [
          { type: 'button', icon: 'http://evil.example/x.svg', action: {} },
          { type: 'button', icon: '<svg onload=alert(1)>', label: 'ok', action: {} },
          { type: 'iframe', src: 'http://evil.example' },
          { type: 'pad' },
        ],
      },
    })!;
    expect(r.items).toEqual([{ type: 'button', icon: undefined, label: 'ok', action: {} }]);
  });

  it('keeps labels as plain strings (rendering uses text nodes) and limits sizes', () => {
    const r = sanitizeRemote({
      ...good,
      layout: { cols: 99, items: Array.from({ length: 200 }, () => ({ type: 'button', label: '<b>x</b>'.repeat(50), action: {} })) },
    })!;
    expect(r.cols).toBe(4);
    expect(r.items).toHaveLength(64);
    expect((r.items[0] as { label: string }).label.length).toBeLessThanOrEqual(40);
  });

  it('rejects other schema versions and malformed documents', () => {
    expect(sanitizeRemote({ ...good, schema: 2 })).toBeNull();
    expect(sanitizeRemote(null)).toBeNull();
    expect(sanitizeRemote({ schema: 1, id: 'x', name: 'x' })).toBeNull();
  });

  it('sanitises the remote list', () => {
    expect(sanitizeRemoteList({ t: 'remotes', list: [{ id: 'a', name: 'A' }, { id: 1 }, 'x'] })).toEqual([{ id: 'a', name: 'A' }]);
    expect(sanitizeRemoteList({})).toEqual([]);
  });
});

describe('state parsing', () => {
  it('parses the provisional state schema and drops bad fields', () => {
    const s = parseState({
      pc: 'Office PC',
      volume: 250,
      muted: true,
      media: { app: 'Plex', title: 'T', artist: 'A', playing: true, pos: 12, dur: 100 },
      bind: { a: 1, b: 'x', c: { nested: true } },
    });
    expect(s.volume).toBe(100);
    expect(s.muted).toBe(true);
    expect(s.media?.title).toBe('T');
    expect(s.bind).toEqual({ a: 1, b: 'x' });
    expect(parseState('nope').media).toBeNull();
    expect(parseState({ media: null }).media).toBeNull();
  });
  it('formats times', () => {
    expect(formatTime(72)).toBe('1:12');
    expect(formatTime(4328)).toBe('1:12:08');
  });
});

describe('i18n', () => {
  it('detects Hebrew (he, iw) and defaults to English', () => {
    expect(detectLang(['he-IL', 'en-US'])).toBe('he');
    expect(detectLang(['iw'])).toBe('he');
    expect(detectLang(['en-GB'])).toBe('en');
    expect(detectLang(['fr-FR'])).toBe('en');
    expect(detectLang([])).toBe('en');
  });
  it('interpolates parameters', () => {
    expect(translate('en', 'offline.title', { pc: 'Office PC' })).toBe('Lost connection to Office PC');
    expect(translate('he', 'tab.media')).toBe('מדיה');
  });
});
