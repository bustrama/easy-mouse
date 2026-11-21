import { useEffect, useRef } from 'preact/hooks';
import { client } from '../client.ts';
import { useT } from '../i18n/useT.ts';
import { BUTTON } from '../protocol/ops.ts';
import { useStore } from '../store.ts';
import { Gestures } from '../touch/gestures.ts';
import { Icon } from './icons.tsx';

export function Touchpad() {
  const t = useT();
  const { sensitivity } = useStore(client.store);
  const pad = useRef<HTMLDivElement>(null);
  const rail = useRef<HTMLDivElement>(null);
  const g = useRef<Gestures | null>(null);
  const frame = useRef(0);

  useEffect(() => {
    const gestures = new Gestures({
      move: (dx, dy) => client.move(dx, dy),
      wheel: (dx, dy) => client.wheel(dx, dy),
      button: (b, d) => client.button(b, d),
    });
    g.current = gestures;
    const tick = () => {
      frame.current = 0;
      gestures.flush();
    };
    schedule.current = () => {
      if (!frame.current) frame.current = requestAnimationFrame(tick);
    };
    const stop = client.onRelease(() => gestures.cancel());
    // iOS Safari scrolls/zooms unless touchmove is cancelled with a non-passive listener.
    const block = (e: TouchEvent) => e.preventDefault();
    const els = [pad.current, rail.current];
    for (const el of els) el?.addEventListener('touchmove', block, { passive: false });
    return () => {
      stop();
      gestures.cancel();
      if (frame.current) cancelAnimationFrame(frame.current);
      for (const el of els) el?.removeEventListener('touchmove', block);
    };
  }, []);

  useEffect(() => {
    if (g.current) g.current.sensitivity = sensitivity;
  }, [sensitivity]);

  const schedule = useRef<() => void>(() => {});

  const onDown = (e: PointerEvent) => {
    (e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId);
    g.current?.down(e.pointerId, e.clientX, e.clientY, e.timeStamp);
    schedule.current();
  };
  const onMove = (e: PointerEvent) => {
    g.current?.move(e.pointerId, e.clientX, e.clientY);
    schedule.current();
  };
  const onUp = (e: PointerEvent) => {
    g.current?.up(e.pointerId, e.timeStamp);
    schedule.current();
  };
  const onCancel = () => g.current?.cancel();

  // scroll rail
  const railY = useRef<number | null>(null);
  const railDown = (e: PointerEvent) => {
    (e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId);
    railY.current = e.clientY;
  };
  const railMove = (e: PointerEvent) => {
    if (railY.current === null) return;
    g.current?.scrollBy(e.clientY - railY.current);
    railY.current = e.clientY;
    schedule.current();
  };
  const railUp = () => {
    railY.current = null;
  };

  return (
    <div class="touch">
      <div class="padrow">
        <div
          ref={pad}
          class="pad"
          onPointerDown={onDown}
          onPointerMove={onMove}
          onPointerUp={onUp}
          onPointerCancel={onCancel}
          onContextMenu={(e) => e.preventDefault()}
        >
          <div class="padhint">
            <Icon name="mouse" size={44} stroke={1.5} />
            <div>{t('touch.hint')}</div>
          </div>
        </div>
        <div
          ref={rail}
          class="rail"
          role="slider"
          aria-label={t('touch.scrollRail')}
          onPointerDown={railDown}
          onPointerMove={railMove}
          onPointerUp={railUp}
          onPointerCancel={railUp}
        >
          <Icon name="up" size={18} stroke={2.2} />
          <div class="railbar" />
          <Icon name="down" size={18} stroke={2.2} />
        </div>
      </div>
      <div class="clicks">
        <ClickButton label={t('touch.left')} button={BUTTON.left} />
        <ClickButton label={t('touch.right')} button={BUTTON.right} />
      </div>
    </div>
  );
}

function ClickButton({ label, button }: { label: string; button: number }) {
  const held = useRef(false);
  const release = () => {
    if (held.current) {
      held.current = false;
      client.button(button, false);
    }
  };
  useEffect(() => {
    const stop = client.onRelease(release);
    return () => {
      stop();
      release();
    };
  }, []);
  return (
    <button
      class="clickbtn"
      onPointerDown={(e) => {
        (e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId);
        if (!held.current) {
          held.current = true;
          client.button(button, true);
        }
      }}
      onPointerUp={release}
      onPointerCancel={release}
      onContextMenu={(e) => e.preventDefault()}
    >
      {label}
    </button>
  );
}
