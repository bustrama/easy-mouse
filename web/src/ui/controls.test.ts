// @vitest-environment jsdom
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { h, render } from 'preact';
import { act } from 'preact/test-utils';
import { beforeEach, describe, expect, it } from 'vitest';
import { client } from '../client.ts';
import { ICON_NAMES } from './iconPaths.ts';
import { Keyboard } from './Keyboard.tsx';
import { Touchpad } from './Touchpad.tsx';

const css = readFileSync(resolve(process.cwd(), 'src/styles.css'), 'utf8');
const rule = (sel: string): string => {
  const esc = sel.replace(/[.[\]()*+?^$|\\]/g, (c) => '\\' + c);
  const m = new RegExp('(?:^|\\n)' + esc + '\\s*\\{([^}]*)\\}').exec(css);
  return m?.[1] ?? '';
};

let root: HTMLElement;
beforeEach(() => {
  document.body.innerHTML = '<div id="r"></div>';
  root = document.getElementById('r')!;
});

describe('physical controls do not mirror in RTL', () => {
  it('mouse buttons: left is first in the DOM, the row is forced ltr in markup and CSS, in Hebrew too', async () => {
    document.documentElement.dir = 'rtl';
    client.setLangPref('he');
    await act(() => render(h(Touchpad, {}), root));
    const row = root.querySelector('.clicks') as HTMLElement;
    expect(row.getAttribute('dir')).toBe('ltr');
    expect(rule('.clicks')).toMatch(/direction:\s*ltr/);
    const labels = [...row.querySelectorAll('button')].map((b) => b.textContent);
    expect(labels).toEqual(['לחיצה שמאלית', 'לחיצה ימנית']); // left, then right
    expect(root.querySelector('.padrow')!.getAttribute('dir')).toBe('ltr'); // scroll strip stays on the right
    client.setLangPref('en');
  });

  it('keyboard grid is ltr so Left/Right arrows and modifier order keep their physical places', async () => {
    await act(() => render(h(Keyboard, {}), root));
    expect(root.querySelector('.keys')!.getAttribute('dir')).toBe('ltr');
    expect(rule('.keys')).toMatch(/direction:\s*ltr/);
    const arrows = [...root.querySelectorAll('.keys button')].map((b) => b.getAttribute('aria-label')).slice(-4);
    expect(arrows).toEqual(['Left', 'Up', 'Down', 'Right']);
  });

  it('progress bar and the transport row are ltr', () => {
    expect(rule('.bar')).toMatch(/direction:\s*ltr/);
  });
});

describe('keyboard controls', () => {
  it('every icon-only key has an accessible name and a 44px+ target; Backspace is present', async () => {
    await act(() => render(h(Keyboard, {}), root));
    const keys = [...root.querySelectorAll<HTMLElement>('.keys button')];
    expect(keys.map((k) => k.getAttribute('aria-label'))).toContain('Backspace');
    for (const k of keys) {
      expect(k.getAttribute('aria-label') || k.textContent).toBeTruthy();
      if (k.querySelector('svg')) expect(k.getAttribute('aria-label')).toBeTruthy();
    }
    expect(rule('.key')).toMatch(/height:\s*52px/);
    expect(rule('.key')).toMatch(/min-width:\s*44px/);
  });

  it('uses symbols from the built-in set for the well-known keys; Ctrl and Alt stay text', async () => {
    for (const n of ['mouse', 'enter', 'backspace', 'delete', 'tab', 'shift', 'win', 'arrow-left', 'arrow-right', 'arrow-up', 'arrow-down']) {
      expect(ICON_NAMES.has(n), n).toBe(true);
    }
    await act(() => render(h(Keyboard, {}), root));
    const byLabel = (l: string) => [...root.querySelectorAll('.keys button')].find((b) => b.getAttribute('aria-label') === l)!;
    expect(byLabel('Ctrl').querySelector('svg')).toBeNull();
    expect(byLabel('Alt').querySelector('svg')).toBeNull();
    for (const l of ['Shift', 'Windows key', 'Enter', 'Backspace', 'Delete', 'Tab', 'Left', 'Right', 'Up', 'Down']) {
      expect(byLabel(l).querySelector('svg'), l).not.toBeNull();
    }
  });

  it('long-press UI is suppressed on the hold controls without disabling text fields', () => {
    expect(rule('button, .pad, .rail')).toMatch(/user-select:\s*none/);
    expect(css).toContain('-webkit-touch-callout: none');
    expect(rule('.field')).not.toMatch(/user-select:\s*none/);
  });
});

describe('wiring: press, hold and release safety through the DOM', () => {
  const calls: string[] = [];
  beforeEach(() => {
    calls.length = 0;
    client.key = (vk: number, f: number) => (calls.push(`${vk.toString(16)}:${f}`), true);
    client.button = (b: number, d: boolean) => (calls.push(`btn${b}:${d ? 'down' : 'up'}`), true);
  });
  const ptr = (el: Element, type: string, id = 1) => el.dispatchEvent(Object.assign(new Event(type, { bubbles: true }), { pointerId: id }));
  const keyEl = (label: string) => [...root.querySelectorAll('.keys button')].find((b) => b.getAttribute('aria-label') === label)!;

  it('Backspace: down on press, up on pointerup', async () => {
    await act(() => render(h(Keyboard, {}), root));
    ptr(keyEl('Backspace'), 'pointerdown');
    expect(calls).toEqual(['8:1']);
    ptr(keyEl('Backspace'), 'pointerup');
    expect(calls).toEqual(['8:1', '8:2']);
  });

  it.each(['pointercancel', 'lostpointercapture', 'blur'])('Backspace released on %s', async (type) => {
    await act(() => render(h(Keyboard, {}), root));
    ptr(keyEl('Backspace'), 'pointerdown');
    ptr(keyEl('Backspace'), type);
    expect(calls).toEqual(['8:1', '8:2']);
  });

  it('a held key is released when the client signals page hidden / socket loss', async () => {
    await act(() => render(h(Keyboard, {}), root));
    ptr(keyEl('Left'), 'pointerdown');
    // visibilitychange (hidden), window blur, pagehide, tab change and socket loss all call this
    (client as unknown as { releaseInputs(): void }).releaseInputs();
    expect(calls.filter((c) => c === '25:2').length).toBe(1);
    (client as unknown as { releaseInputs(): void }).releaseInputs();
    expect(calls.filter((c) => c === '25:2').length).toBe(1); // idempotent
  });

  it('mouse button: down on press, up on release and on lost capture', async () => {
    await act(() => render(h(Touchpad, {}), root));
    const left = root.querySelector('.clicks button')!;
    ptr(left, 'pointerdown');
    expect(calls).toEqual(['btn0:down']);
    ptr(left, 'lostpointercapture');
    expect(calls).toEqual(['btn0:down', 'btn0:up']);
  });
});
