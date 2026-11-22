import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { BUTTON, KEYFLAG } from '../protocol/ops.ts';
import { ButtonHold, KeyRepeater, ModifierLatch } from './hold.ts';

const D = KEYFLAG.down;
const U = KEYFLAG.up;
const BS = 0x08;
const CTRL = 0xa2;
const SHIFT = 0xa0;

let log: [number, number][];
const sink = { key: (vk: number, f: number) => log.push([vk, f]) };

beforeEach(() => {
  vi.useFakeTimers();
  log = [];
});
afterEach(() => vi.useRealTimers());

describe('KeyRepeater', () => {
  it('a quick tap is down then up with no repeat', () => {
    const k = new KeyRepeater(sink);
    k.press(1, BS, true);
    vi.advanceTimersByTime(100);
    k.release(1);
    vi.advanceTimersByTime(2000);
    expect(log).toEqual([[BS, D], [BS, U]]);
  });

  it('auto-repeats after 400 ms at about 30 Hz while held, then sends key-up once on release', () => {
    const k = new KeyRepeater(sink);
    k.press(1, BS, true);
    vi.advanceTimersByTime(399);
    expect(log).toHaveLength(1); // only the initial down
    vi.advanceTimersByTime(1 + 330); // 400 ms, then ten 33 ms ticks
    expect(log.filter(([, f]) => f === D).length).toBe(1 + 10);
    k.release(1);
    expect(log.at(-1)).toEqual([BS, U]);
    const n = log.length;
    vi.advanceTimersByTime(5000);
    expect(log).toHaveLength(n); // nothing after release
    k.release(1);
    expect(log).toHaveLength(n); // release is idempotent
  });

  it('keys that do not repeat (Esc) only send down and up', () => {
    const k = new KeyRepeater(sink);
    k.press(1, 0x1b, false);
    vi.advanceTimersByTime(3000);
    k.release(1);
    expect(log).toEqual([[0x1b, D], [0x1b, U]]);
  });

  it('releaseAll (page hidden, blur, socket loss) releases every held key and stops the timers', () => {
    const k = new KeyRepeater(sink);
    k.press('a', BS, true);
    k.press('b', 0x25, true);
    vi.advanceTimersByTime(1000);
    k.releaseAll();
    expect(k.count).toBe(0);
    const ups = log.filter(([, f]) => f === U);
    expect(ups).toEqual([[BS, U], [0x25, U]]);
    const n = log.length;
    vi.advanceTimersByTime(5000);
    expect(log).toHaveLength(n);
  });

  it('the same owner cannot press twice (no double down)', () => {
    const k = new KeyRepeater(sink);
    k.press(1, BS, true);
    k.press(1, BS, true);
    expect(log).toHaveLength(1);
  });

  it('reports each key-up so sticky modifiers can be released afterwards', () => {
    const ups: number[] = [];
    const k = new KeyRepeater(sink, undefined, (vk) => ups.push(vk));
    k.press(1, BS, true);
    k.release(1);
    expect(ups).toEqual([BS]);
  });
});

describe('ModifierLatch', () => {
  const mk = () => new ModifierLatch(sink);

  it('tap latches (down), is released after the next non-modifier key', () => {
    const m = mk();
    m.pointerDown(1, CTRL);
    vi.advanceTimersByTime(100);
    m.pointerUp(1);
    expect(m.stateOf(CTRL)).toBe('sticky');
    expect(log).toEqual([[CTRL, D]]);
    m.afterKey();
    expect(m.stateOf(CTRL)).toBeUndefined();
    expect(log).toEqual([[CTRL, D], [CTRL, U]]);
  });

  it('tapping a latched modifier releases it', () => {
    const m = mk();
    m.pointerDown(1, SHIFT);
    m.pointerUp(1);
    m.pointerDown(1, SHIFT);
    m.pointerUp(1);
    expect(m.stateOf(SHIFT)).toBeUndefined();
    expect(log).toEqual([[SHIFT, D], [SHIFT, U]]);
  });

  it('a long press locks: it survives the next key and is released by a tap', () => {
    const m = mk();
    m.pointerDown(1, CTRL);
    vi.advanceTimersByTime(600);
    expect(m.stateOf(CTRL)).toBe('locked');
    m.pointerUp(1); // the release of the long press does not toggle it off
    expect(m.stateOf(CTRL)).toBe('locked');
    m.afterKey();
    expect(m.stateOf(CTRL)).toBe('locked');
    m.pointerDown(1, CTRL);
    m.pointerUp(1);
    expect(m.stateOf(CTRL)).toBeUndefined();
    expect(log).toEqual([[CTRL, D], [CTRL, U]]);
  });

  it('long press on an already latched modifier locks it without a second down', () => {
    const m = mk();
    m.pointerDown(1, CTRL);
    m.pointerUp(1);
    m.pointerDown(1, CTRL);
    vi.advanceTimersByTime(600);
    m.pointerUp(1);
    expect(m.stateOf(CTRL)).toBe('locked');
    expect(log).toEqual([[CTRL, D]]);
  });

  it('pointercancel before the lock fires does nothing', () => {
    const m = mk();
    m.pointerDown(1, CTRL);
    m.pointerCancel(1);
    vi.advanceTimersByTime(2000);
    expect(m.stateOf(CTRL)).toBeUndefined();
    expect(log).toEqual([]);
  });

  it('releaseAll releases sticky and locked modifiers and cancels pending locks', () => {
    const m = mk();
    m.pointerDown(1, CTRL);
    m.pointerUp(1);
    m.pointerDown(2, SHIFT);
    vi.advanceTimersByTime(600); // SHIFT locks
    m.pointerDown(3, 0xa4); // ALT still pending
    m.releaseAll();
    vi.advanceTimersByTime(2000);
    expect(m.stateOf(CTRL)).toBeUndefined();
    expect(m.stateOf(SHIFT)).toBeUndefined();
    expect(m.stateOf(0xa4)).toBeUndefined();
    expect(log.filter(([, f]) => f === U).map(([vk]) => vk).sort()).toEqual([SHIFT, CTRL].sort());
    expect(log.some(([vk]) => vk === 0xa4)).toBe(false);
  });
});

describe('ButtonHold (mouse buttons)', () => {
  let ev: [number, boolean][];
  const bs = { button: (b: number, d: boolean) => ev.push([b, d]) };
  beforeEach(() => (ev = []));

  it('down on press, up on release, so holding gives a drag', () => {
    const h = new ButtonHold(bs);
    h.down(1, BUTTON.left);
    expect(ev).toEqual([[BUTTON.left, true]]);
    h.release(1);
    expect(ev).toEqual([[BUTTON.left, true], [BUTTON.left, false]]);
    h.release(1);
    expect(ev).toHaveLength(2);
  });

  it('releaseAll frees buttons held by any pointer', () => {
    const h = new ButtonHold(bs);
    h.down(1, BUTTON.left);
    h.down(2, BUTTON.right);
    h.releaseAll();
    expect(ev.filter(([, d]) => !d).map(([b]) => b).sort()).toEqual([BUTTON.left, BUTTON.right]);
  });
});
