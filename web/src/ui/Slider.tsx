import { useRef, useState } from 'preact/hooks';

/** Horizontal 0..100 slider. Direction follows the document (RTL flips it). Throttles `onChange` while dragging. */
export function Slider({ value, onChange, label }: { value: number; onChange: (v: number) => void; label: string }) {
  const [drag, setDrag] = useState<number | null>(null);
  const el = useRef<HTMLDivElement>(null);
  const last = useRef(0);
  const shown = drag ?? value;

  const calc = (e: PointerEvent): number => {
    const r = el.current!.getBoundingClientRect();
    let f = (e.clientX - r.left) / r.width;
    if (getComputedStyle(el.current!).direction === 'rtl') f = 1 - f;
    return Math.round(Math.max(0, Math.min(1, f)) * 100);
  };
  const update = (e: PointerEvent, final: boolean) => {
    const v = calc(e);
    setDrag(final ? null : v);
    const now = performance.now();
    if (final || now - last.current > 90) {
      last.current = now;
      onChange(v);
    }
  };

  return (
    <div
      ref={el}
      class="slider"
      role="slider"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={shown}
      tabIndex={0}
      onPointerDown={(e) => {
        (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
        update(e, false);
      }}
      onPointerMove={(e) => {
        if (drag !== null) update(e, false);
      }}
      onPointerUp={(e) => drag !== null && update(e, true)}
      onPointerCancel={() => setDrag(null)}
      onKeyDown={(e) => {
        if (e.key === 'ArrowRight' || e.key === 'ArrowUp') onChange(Math.min(100, shown + 5));
        if (e.key === 'ArrowLeft' || e.key === 'ArrowDown') onChange(Math.max(0, shown - 5));
      }}
    >
      <div class="slider-fill" style={{ width: `${shown}%` }} />
      <div class="slider-knob" style={{ insetInlineStart: `${shown}%` }} />
    </div>
  );
}
