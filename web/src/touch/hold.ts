import { BUTTON, KEYFLAG } from '../protocol/ops.ts';

// Press-and-hold behaviour for on-screen keys and mouse buttons. Pure (injectable timers), so the
// "never leave anything stuck" rules can be unit tested without a DOM.

export interface Timers {
  setTimeout(fn: () => void, ms: number): unknown;
  clearTimeout(id: unknown): void;
  setInterval(fn: () => void, ms: number): unknown;
  clearInterval(id: unknown): void;
}

export const realTimers: Timers = {
  setTimeout: (fn, ms) => setTimeout(fn, ms),
  clearTimeout: (id) => clearTimeout(id as ReturnType<typeof setTimeout>),
  setInterval: (fn, ms) => setInterval(fn, ms),
  clearInterval: (id) => clearInterval(id as ReturnType<typeof setInterval>),
};

export const REPEAT_DELAY_MS = 400;
export const REPEAT_INTERVAL_MS = 33; // about 30 Hz
export const LOCK_MS = 500;

export interface KeySink {
  key(vk: number, flags: number): void;
}
export interface ButtonSink {
  button(button: number, down: boolean): void;
}

/** A pointer (or other owner) holding one key: down on press, auto-repeat down, up on release. */
export class KeyRepeater {
  private readonly held = new Map<unknown, { vk: number; delay: unknown; every: unknown }>();
  private readonly sink: KeySink;
  private readonly timers: Timers;
  private readonly onKeyUp: (vk: number) => void;

  constructor(sink: KeySink, timers: Timers = realTimers, onKeyUp: (vk: number) => void = () => {}) {
    this.sink = sink;
    this.timers = timers;
    this.onKeyUp = onKeyUp;
  }

  press(owner: unknown, vk: number, repeat: boolean): void {
    if (this.held.has(owner)) return;
    this.sink.key(vk, KEYFLAG.down);
    const entry = { vk, delay: null as unknown, every: null as unknown };
    if (repeat) {
      entry.delay = this.timers.setTimeout(() => {
        entry.every = this.timers.setInterval(() => this.sink.key(vk, KEYFLAG.down), REPEAT_INTERVAL_MS);
      }, REPEAT_DELAY_MS);
    }
    this.held.set(owner, entry);
  }

  release(owner: unknown): void {
    const e = this.held.get(owner);
    if (!e) return;
    this.held.delete(owner);
    if (e.delay !== null) this.timers.clearTimeout(e.delay);
    if (e.every !== null) this.timers.clearInterval(e.every);
    this.sink.key(e.vk, KEYFLAG.up);
    this.onKeyUp(e.vk);
  }

  releaseAll(): void {
    for (const owner of [...this.held.keys()]) this.release(owner);
  }

  get count(): number {
    return this.held.size;
  }
}

export type LatchState = 'sticky' | 'locked';

/**
 * Modifier keys: a tap latches ("sticky", released after the next non-modifier key), a long press
 * locks (stays until tapped again), a tap on a latched modifier releases it.
 */
export class ModifierLatch {
  private readonly state = new Map<number, LatchState>();
  private readonly timers: Timers;
  private readonly sink: KeySink;
  private readonly onChange: () => void;
  private readonly pending = new Map<unknown, { vk: number; timer: unknown; fired: boolean }>();

  constructor(sink: KeySink, timers: Timers = realTimers, onChange: () => void = () => {}) {
    this.sink = sink;
    this.timers = timers;
    this.onChange = onChange;
  }

  stateOf(vk: number): LatchState | undefined {
    return this.state.get(vk);
  }

  pointerDown(owner: unknown, vk: number): void {
    if (this.pending.has(owner)) return;
    const p = { vk, timer: null as unknown, fired: false };
    p.timer = this.timers.setTimeout(() => {
      p.fired = true;
      if (!this.state.has(vk)) this.sink.key(vk, KEYFLAG.down);
      this.state.set(vk, 'locked');
      this.onChange();
    }, LOCK_MS);
    this.pending.set(owner, p);
  }

  pointerUp(owner: unknown): void {
    const p = this.pending.get(owner);
    if (!p) return;
    this.pending.delete(owner);
    this.timers.clearTimeout(p.timer);
    if (p.fired) return; // it became a lock while held
    if (this.state.has(p.vk)) {
      this.state.delete(p.vk);
      this.sink.key(p.vk, KEYFLAG.up);
    } else {
      this.state.set(p.vk, 'sticky');
      this.sink.key(p.vk, KEYFLAG.down);
    }
    this.onChange();
  }

  /** pointercancel / lost capture: forget the press without acting (a lock that already fired stays). */
  pointerCancel(owner: unknown): void {
    const p = this.pending.get(owner);
    if (!p) return;
    this.pending.delete(owner);
    this.timers.clearTimeout(p.timer);
  }

  /** A non-modifier key finished: sticky modifiers are done, locked ones stay. */
  afterKey(): void {
    let changed = false;
    for (const [vk, s] of [...this.state]) {
      if (s === 'sticky') {
        this.state.delete(vk);
        this.sink.key(vk, KEYFLAG.up);
        changed = true;
      }
    }
    if (changed) this.onChange();
  }

  /** Page hidden, blur, tab change, socket loss: release everything, locks included. */
  releaseAll(): void {
    for (const p of this.pending.values()) this.timers.clearTimeout(p.timer);
    this.pending.clear();
    if (this.state.size === 0) return;
    for (const vk of [...this.state.keys()]) this.sink.key(vk, KEYFLAG.up);
    this.state.clear();
    this.onChange();
  }
}

/** Mouse button held while a pointer is down; holding gives a drag. */
export class ButtonHold {
  private readonly held = new Map<unknown, number>();
  private readonly sink: ButtonSink;

  constructor(sink: ButtonSink) {
    this.sink = sink;
  }

  down(owner: unknown, button: number = BUTTON.left): void {
    if (this.held.has(owner)) return;
    this.held.set(owner, button);
    this.sink.button(button, true);
  }

  release(owner: unknown): void {
    const b = this.held.get(owner);
    if (b === undefined) return;
    this.held.delete(owner);
    this.sink.button(b, false);
  }

  releaseAll(): void {
    for (const o of [...this.held.keys()]) this.release(o);
  }
}
