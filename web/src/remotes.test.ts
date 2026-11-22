import { describe, expect, it } from 'vitest';
import { toastFor } from './client.ts';
import { detectLang, translate } from './i18n/strings.ts';
import { bindValue, formatTime, parseState } from './pcstate.ts';
import { sanitizeRemote, sanitizeRemotes } from './remotes.ts';

describe('remote sanitising', () => {
  const good = {
    schema: 1,
    id: 'stremio',
    name: 'Stremio',
    layout: {
      cols: 4,
      items: [
        { type: 'button', icon: 'play', action: { do: 'smtc', cmd: 'toggle' } },
        { type: 'button', id: 'fs', label: 'Fullscreen', action: [{ do: 'focus' }, { do: 'key', keys: 'F' }] },
        { type: 'slider', bind: 'volume.app', action: { do: 'volume' } },
        { type: 'label', bind: 'smtc.title' },
      ],
    },
  };

  it('accepts the architecture.md example; item ids default to their position', () => {
    const r = sanitizeRemote(good)!;
    expect(r.cols).toBe(4);
    expect(r.items.map((i) => [i.type, i.id])).toEqual([
      ['button', '0'],
      ['button', 'fs'],
      ['slider', '2'],
      ['label', '3'],
    ]);
  });

  it('never carries an action to the phone object (the PC owns actions)', () => {
    const r = sanitizeRemote(good)!;
    expect(JSON.stringify(r)).not.toContain('toggle');
    expect(JSON.stringify(r)).not.toContain('action');
  });

  it('keeps item ids aligned with the original positions when items are skipped', () => {
    const r = sanitizeRemote({
      ...good,
      layout: { cols: 2, items: [{ type: 'iframe' }, { type: 'button', label: 'x' }] },
    })!;
    expect(r.items).toEqual([{ type: 'button', id: '1', icon: undefined, label: 'x' }]);
  });

  it('drops unknown icons, unknown item types and iconless/labelless buttons', () => {
    const r = sanitizeRemote({
      ...good,
      layout: {
        cols: 2,
        items: [
          { type: 'button', icon: 'http://evil.example/x.svg' },
          { type: 'button', icon: '<svg onload=alert(1)>', label: 'ok' },
          { type: 'iframe', src: 'http://evil.example' },
          { type: 'pad' },
        ],
      },
    })!;
    expect(r.items).toEqual([{ type: 'button', id: '1', icon: undefined, label: 'ok' }]);
  });

  it('keeps labels as plain strings (rendering uses text nodes) and limits sizes', () => {
    const r = sanitizeRemote({
      ...good,
      layout: { cols: 99, items: Array.from({ length: 200 }, () => ({ type: 'button', label: '<b>x</b>'.repeat(50) })) },
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

  it('sanitises the pushed remote list', () => {
    expect(sanitizeRemotes({ t: 'remotes', remotes: [good, { schema: 9 }, 'x'] }).map((r) => r.id)).toEqual(['stremio']);
    expect(sanitizeRemotes({})).toEqual([]);
  });
});

describe('state parsing', () => {
  it('parses the 0x11 state schema and drops bad fields', () => {
    const s = parseState({
      t: 'state',
      media: { status: 'playing', title: 'T', artist: 'A', positionMs: 12000, durationMs: 100000 },
      volume: { level: 250, muted: true },
      host: { locked: true, elevated: false },
    });
    expect(s.volume).toEqual({ level: 100, muted: true });
    expect(s.media).toEqual({ status: 'playing', title: 'T', artist: 'A', positionMs: 12000, durationMs: 100000 });
    expect(s.locked).toBe(true);
    expect(parseState('nope').media).toBeNull();
    expect(parseState({ t: 'state', media: null, volume: null, host: {} }).volume).toBeNull();
    expect(parseState({ t: 'state', media: { status: 'bogus' } }).media?.status).toBe('none');
  });
  it('resolves remote binds', () => {
    const s = parseState({ t: 'state', media: { status: 'paused', title: 'Dune' }, volume: { level: 40, muted: false }, host: {} });
    expect(bindValue(s, 'smtc.title')).toBe('Dune');
    expect(bindValue(s, 'volume')).toBe(40);
    expect(bindValue(s, 'nope')).toBeUndefined();
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

describe('toastFor', () => {
  it.each([
    ['unsupported', 'unsupported'],
    ['denied', 'denied'],
    ['locked', 'locked'],
    ['busy', 'busy'],
    ['failed', 'failed'],
    ['something-new', 'failed'],
    [undefined, 'failed'],
  ])('maps %s to %s', (err, kind) => {
    expect(toastFor(err)).toBe(kind);
  });
});
