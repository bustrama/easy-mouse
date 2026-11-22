import { useEffect, useRef } from 'preact/hooks';
import { capture } from './capture.ts';
import { client } from '../client.ts';
import { useT } from '../i18n/useT.ts';
import { BUTTON } from '../protocol/ops.ts';
import { useStore } from '../store.ts';
import { Gestures } from '../touch/gestures.ts';
import { ButtonHold } from '../touch/hold.ts';
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
    capture(e);
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
    capture(e);
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
      <div class="padrow" dir="ltr">
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
      <div class="clicks" dir="ltr">
        <ClickButton label={t('touch.left')} button={BUTTON.left} />
        <ClickButton label={t('touch.right')} button={BUTTON.right} />
      </div>
    </div>
  );
}

function ClickButton({ label, button }: { label: string; button: number }) {
  const hold = useRef<ButtonHold | null>(null);
  if (!hold.current) hold.current = new ButtonHold(client);
  const h = hold.current;
  useEffect(() => {
    const stop = client.onRelease(() => h.releaseAll());
    return () => {
      stop();
      h.releaseAll();
    };
  }, []);
  // Holding the button holds the mouse button down, which gives a drag when combined with the pad.
  return (
    <button
      class="clickbtn"
      onPointerDown={(e) => {
        capture(e);
        h.down(e.pointerId, button);
      }}
      onPointerUp={(e) => h.release(e.pointerId)}
      onPointerCancel={(e) => h.release(e.pointerId)}
      onLostPointerCapture={(e) => h.release(e.pointerId)}
      onBlur={() => h.releaseAll()}
      onContextMenu={(e) => e.preventDefault()}
      // assistive technology and keyboard activation: a click without a pointer
      onClick={(e) => {
        if (e.detail === 0) {
          h.down('a11y', button);
          h.release('a11y');
        }
      }}
    >
      {label}
    </button>
  );
}
