import { BUTTON } from '../protocol/ops.ts';

/** Where recognised gestures go. `move`/`wheel` are called at most once per `flush()`. */
export interface Sink {
  move(dx: number, dy: number): void;
  wheel(dx: number, dy: number): void;
  button(button: number, down: boolean): void;
}

export const TAP_MS = 250;
export const TAP_SLOP = 10;
export const DEADZONE = 4;
export const DRAG_REARM_MS = 300;
/** Wheel units (1/120 notch) per pixel of two-finger travel. */
export const WHEEL_PER_PX = 3;
const I16 = 32767;

interface Pt {
  x: number;
  y: number;
}

type Mode = 'idle' | 'one' | 'two' | 'dead';

/**
 * Pure touchpad gesture recogniser. The DOM layer feeds it pointer events with timestamps and calls
 * `flush()` once per animation frame; deltas are summed (with sub-pixel remainders carried over) so
 * a frame produces one `move` and one `wheel` at most.
 */
export class Gestures {
  sensitivity = 1.4;
  private readonly sink: Sink;
  private readonly pts = new Map<number, Pt>();
  private mode: Mode = 'idle';
  // move/wheel accumulators (already scaled, fractional)
  private mx = 0;
  private my = 0;
  private wx = 0;
  private wy = 0;
  // single finger
  private oneStart = 0;
  private travel = 0;
  private moved = false;
  private held: Pt = { x: 0, y: 0 };
  private dragArmed = false;
  private dragging = false;
  private lastTapEnd = -Infinity;
  // two fingers
  private twoStart = 0;
  private twoTravel = 0;
  private twoDone = false;

  constructor(sink: Sink) {
    this.sink = sink;
  }

  down(id: number, x: number, y: number, t: number): void {
    this.pts.set(id, { x, y });
    const n = this.pts.size;
    if (n === 1) {
      this.mode = 'one';
      this.oneStart = t;
      this.travel = 0;
      this.moved = false;
      this.held = { x: 0, y: 0 };
      this.dragArmed = t - this.lastTapEnd < DRAG_REARM_MS;
      this.dragging = false;
    } else if (n === 2) {
      this.releaseDrag();
      this.mode = 'two';
      this.twoStart = this.oneStart;
      this.twoTravel = 0;
      this.twoDone = false;
    } else {
      this.mode = 'dead';
    }
  }

  move(id: number, x: number, y: number): void {
    const p = this.pts.get(id);
    if (!p) return;
    const dx = x - p.x;
    const dy = y - p.y;
    p.x = x;
    p.y = y;
    if (this.mode === 'one') {
      this.travel += Math.abs(dx) + Math.abs(dy);
      if (!this.moved) {
        this.held.x += dx;
        this.held.y += dy;
        if (this.travel < DEADZONE) return;
        this.moved = true;
        if (this.dragArmed) {
          this.flush();
          this.sink.button(BUTTON.left, true);
          this.dragging = true;
        }
        this.addMove(this.held.x, this.held.y);
        return;
      }
      this.addMove(dx, dy);
    } else if (this.mode === 'two') {
      // centroid delta of two fingers = average of per-finger deltas
      const sx = dx / 2;
      const sy = dy / 2;
      this.twoTravel += Math.abs(sx) + Math.abs(sy);
      // natural scrolling: dragging down moves content down, which is wheel "up" (positive dy)
      this.wx += sx * WHEEL_PER_PX;
      this.wy += sy * WHEEL_PER_PX;
    }
  }

  up(id: number, t: number): void {
    if (!this.pts.delete(id)) return;
    if (this.mode === 'one' && this.pts.size === 0) {
      if (this.dragging) {
        this.flush();
        this.sink.button(BUTTON.left, false);
        this.dragging = false;
      } else if (!this.moved && this.travel < TAP_SLOP && t - this.oneStart < TAP_MS) {
        this.flush();
        this.sink.button(BUTTON.left, true);
        this.sink.button(BUTTON.left, false);
        // a second tap (double click) does not re-arm a drag
        this.lastTapEnd = this.dragArmed ? -Infinity : t;
      }
    } else if (this.mode === 'two') {
      if (!this.twoDone && this.twoTravel < TAP_SLOP && t - this.twoStart < TAP_MS) {
        this.flush();
        this.sink.button(BUTTON.right, true);
        this.sink.button(BUTTON.right, false);
      }
      this.twoDone = true;
      this.mode = 'dead';
    }
    if (this.pts.size === 0) this.mode = 'idle';
  }

  /** pointercancel, page hidden, or socket lost: never leave a button held on the PC. */
  cancel(): void {
    this.releaseDrag();
    this.pts.clear();
    this.mode = 'idle';
    this.mx = this.my = this.wx = this.wy = 0;
  }

  private releaseDrag(): void {
    if (this.dragging) {
      this.flush();
      this.sink.button(BUTTON.left, false);
      this.dragging = false;
    }
  }

  private addMove(dx: number, dy: number): void {
    this.mx += dx * this.sensitivity;
    this.my += dy * this.sensitivity;
  }

  /** Emit the batched deltas (call once per animation frame). */
  flush(): void {
    const mxi = clamp(Math.trunc(this.mx));
    const myi = clamp(Math.trunc(this.my));
    if (mxi || myi) {
      this.mx -= mxi;
      this.my -= myi;
      this.sink.move(mxi, myi);
    }
    const wxi = clamp(Math.trunc(this.wx));
    const wyi = clamp(Math.trunc(this.wy));
    if (wxi || wyi) {
      this.wx -= wxi;
      this.wy -= wyi;
      this.sink.wheel(wxi, wyi);
    }
  }
}

const clamp = (n: number): number => Math.max(-I16, Math.min(I16, n));
