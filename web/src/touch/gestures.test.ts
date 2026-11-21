import { beforeEach, describe, expect, it } from 'vitest';
import { BUTTON } from '../protocol/ops.ts';
import { Gestures, type Sink } from './gestures.ts';

type Ev = ['move', number, number] | ['wheel', number, number] | ['button', number, boolean];

let ev: Ev[];
let g: Gestures;
const sink: Sink = {
  move: (dx, dy) => ev.push(['move', dx, dy]),
  wheel: (dx, dy) => ev.push(['wheel', dx, dy]),
  button: (b, d) => ev.push(['button', b, d]),
};

beforeEach(() => {
  ev = [];
  g = new Gestures(sink);
  g.sensitivity = 1;
});

describe('per-frame delta batching', () => {
  it('sums many pointer moves into one move per flush', () => {
    g.down(1, 100, 100, 0);
    g.move(1, 110, 100);
    g.move(1, 120, 105);
    g.move(1, 125, 95);
    expect(ev).toEqual([]); // nothing is sent between frames
    g.flush();
    expect(ev).toEqual([['move', 25, -5]]);
    g.flush();
    expect(ev).toHaveLength(1); // an empty frame sends nothing
  });

  it('applies sensitivity and carries the fractional remainder', () => {
    g.sensitivity = 0.5;
    g.down(1, 0, 0, 0);
    g.move(1, 5, 0); // leaves the deadzone: 2.5 -> 2, remainder .5
    g.flush();
    g.move(1, 6, 0); // +0.5 -> 3 total with remainder
    g.flush();
    expect(ev).toEqual([
      ['move', 2, 0],
      ['move', 1, 0],
    ]);
  });

  it('clamps to int16 and keeps the rest for the next frame', () => {
    g.down(1, 0, 0, 0);
    g.move(1, 50000, 0);
    g.flush();
    g.flush();
    expect(ev).toEqual([
      ['move', 32767, 0],
      ['move', 17233, 0],
    ]);
  });
});

describe('gestures', () => {
  it('tap = left click (and no pointer jitter)', () => {
    g.down(1, 50, 50, 0);
    g.move(1, 52, 51); // inside the deadzone
    g.up(1, 120);
    expect(ev).toEqual([
      ['button', BUTTON.left, true],
      ['button', BUTTON.left, false],
    ]);
  });

  it('a slow press is not a tap', () => {
    g.down(1, 50, 50, 0);
    g.up(1, 600);
    expect(ev).toEqual([]);
  });

  it('a drag is not a tap and does not click', () => {
    g.down(1, 0, 0, 0);
    g.move(1, 30, 0);
    g.up(1, 100);
    g.flush();
    expect(ev).toEqual([['move', 30, 0]]);
  });

  it('two-finger tap = right click', () => {
    g.down(1, 40, 40, 0);
    g.down(2, 80, 40, 20);
    g.up(1, 90);
    g.up(2, 100);
    expect(ev).toEqual([
      ['button', BUTTON.right, true],
      ['button', BUTTON.right, false],
    ]);
  });

  it('two-finger drag scrolls (natural direction) and never moves the pointer', () => {
    g.down(1, 40, 100, 0);
    g.down(2, 80, 100, 10);
    g.move(1, 40, 110);
    g.move(2, 80, 110);
    g.flush();
    expect(ev).toEqual([['wheel', 0, 30]]); // 10 px down on both fingers: 10 * 3
    g.up(1, 300);
    g.up(2, 310);
    expect(ev).toHaveLength(1); // no right click after a scroll
  });

  it('after one of two fingers lifts, the remaining finger is ignored', () => {
    g.down(1, 0, 0, 0);
    g.down(2, 50, 0, 10);
    g.move(1, 0, 30);
    g.move(2, 50, 30);
    g.up(1, 400);
    g.move(2, 80, 80);
    g.flush();
    expect(ev.every((e) => e[0] === 'wheel')).toBe(true);
  });

  it('tap-hold drag: tap, then touch again quickly and move holds the left button', () => {
    g.down(1, 10, 10, 0);
    g.up(1, 60); // tap = click
    ev.length = 0;
    g.down(1, 10, 10, 200); // within the re-arm window
    g.move(1, 40, 10);
    g.flush();
    expect(ev).toEqual([
      ['button', BUTTON.left, true],
      ['move', 30, 0],
    ]);
    g.up(1, 800);
    expect(ev.at(-1)).toEqual(['button', BUTTON.left, false]);
  });

  it('a second tap in the window is a double click, not a stuck drag', () => {
    g.down(1, 10, 10, 0);
    g.up(1, 60);
    g.down(1, 10, 10, 200);
    g.up(1, 260);
    expect(ev.filter((e) => e[0] === 'button')).toHaveLength(4);
    expect(ev.at(-1)).toEqual(['button', BUTTON.left, false]);
  });

  it('scroll rail drag produces wheel units', () => {
    g.scrollBy(10);
    g.scrollBy(-4);
    g.flush();
    expect(ev).toEqual([['wheel', 0, 18]]);
  });

  it('cancel releases a held drag button and drops pending deltas', () => {
    g.down(1, 10, 10, 0);
    g.up(1, 60);
    g.down(1, 10, 10, 200);
    g.move(1, 40, 10);
    g.cancel();
    expect(ev.at(-1)).toEqual(['button', BUTTON.left, false]);
    ev.length = 0;
    g.flush();
    expect(ev).toEqual([]);
  });
});
