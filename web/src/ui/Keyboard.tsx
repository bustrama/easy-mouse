import { useEffect, useRef, useState } from 'preact/hooks';
import { client } from '../client.ts';
import type { Key } from '../i18n/strings.ts';
import { useT } from '../i18n/useT.ts';
import { KEYFLAG } from '../protocol/ops.ts';
import { KeyRepeater, ModifierLatch, realTimers } from '../touch/hold.ts';
import { capture } from './capture.ts';
import { Icon } from './icons.tsx';

interface KeyDef {
  id: string;
  vk: number;
  /** icon name from the built-in set; text keys have none */
  icon?: string;
  /** auto-repeat while held */
  repeat: boolean;
  wide?: boolean;
}

// Modifiers: Ctrl and Alt stay text, Shift and Win use their symbols.
const MODS: KeyDef[] = [
  { id: 'Ctrl', vk: 0xa2, repeat: false },
  { id: 'Alt', vk: 0xa4, repeat: false },
  { id: 'Shift', vk: 0xa0, icon: 'shift', repeat: false },
  { id: 'Win', vk: 0x5b, icon: 'win', repeat: false },
];

const KEYS: KeyDef[] = [
  { id: 'Esc', vk: 0x1b, repeat: false },
  { id: 'Tab', vk: 0x09, icon: 'tab', repeat: true },
  { id: 'Backspace', vk: 0x08, icon: 'backspace', repeat: true },
  { id: 'Delete', vk: 0x2e, icon: 'delete', repeat: true },
  { id: 'Enter', vk: 0x0d, icon: 'enter', repeat: true },
  { id: 'Space', vk: 0x20, icon: 'space', repeat: true, wide: true },
  { id: 'Left', vk: 0x25, icon: 'arrow-left', repeat: true },
  { id: 'Up', vk: 0x26, icon: 'arrow-up', repeat: true },
  { id: 'Down', vk: 0x28, icon: 'arrow-down', repeat: true },
  { id: 'Right', vk: 0x27, icon: 'arrow-right', repeat: true },
];

/** Shortcut chips: modifiers first, the last entry is the key that is pressed. */
const SHORTCUTS: { label: string; vks: number[] }[] = [
  { label: 'Ctrl + C', vks: [0xa2, 0x43] },
  { label: 'Ctrl + V', vks: [0xa2, 0x56] },
  { label: 'Alt + Tab', vks: [0xa4, 0x09] },
  { label: 'Win + D', vks: [0x5b, 0x44] },
  { label: 'Alt + F4', vks: [0xa4, 0x73] },
];

export function Keyboard() {
  const t = useT();
  const [text, setText] = useState('');
  const [sent, setSent] = useState(false);
  const [, bump] = useState(0);
  const ctl = useRef<{ rep: KeyRepeater; latch: ModifierLatch } | null>(null);
  if (!ctl.current) {
    const latch = new ModifierLatch(client, realTimers, () => bump((n) => n + 1));
    const rep = new KeyRepeater(client, realTimers, () => latch.afterKey());
    ctl.current = { rep, latch };
  }
  const { rep, latch } = ctl.current;

  // Nothing may outlive the page, the tab or the connection: key-ups for everything held.
  useEffect(() => {
    const release = () => {
      rep.releaseAll();
      latch.releaseAll();
    };
    const stop = client.onRelease(release);
    return () => {
      stop();
      release();
    };
  }, []);

  const chord = (vks: number[]) => {
    const mods = vks.slice(0, -1);
    const key = vks[vks.length - 1]!;
    for (const m of mods) client.key(m, KEYFLAG.down);
    client.key(key, KEYFLAG.press);
    for (const m of [...mods].reverse()) client.key(m, KEYFLAG.up);
  };

  const send = () => {
    if (!text) return;
    if (client.text(text)) {
      setText('');
      setSent(true);
      setTimeout(() => setSent(false), 1200);
    }
  };

  const keyProps = (k: KeyDef) => ({
    onPointerDown: (e: PointerEvent) => {
      capture(e);
      rep.press(e.pointerId, k.vk, k.repeat);
    },
    onPointerUp: (e: PointerEvent) => rep.release(e.pointerId),
    onPointerCancel: (e: PointerEvent) => rep.release(e.pointerId),
    onLostPointerCapture: (e: PointerEvent) => rep.release(e.pointerId),
    onBlur: () => rep.releaseAll(),
    onContextMenu: (e: Event) => e.preventDefault(),
    // Assistive technology and keyboard activation arrive as a click with no pointer (detail 0).
    onClick: (e: MouseEvent) => {
      if (e.detail === 0) {
        rep.press('a11y', k.vk, false);
        rep.release('a11y');
      }
    },
  });

  const modProps = (k: KeyDef) => ({
    onPointerDown: (e: PointerEvent) => {
      capture(e);
      latch.pointerDown(e.pointerId, k.vk);
    },
    onPointerUp: (e: PointerEvent) => latch.pointerUp(e.pointerId),
    onPointerCancel: (e: PointerEvent) => latch.pointerCancel(e.pointerId),
    onLostPointerCapture: (e: PointerEvent) => latch.pointerCancel(e.pointerId),
    onContextMenu: (e: Event) => e.preventDefault(),
    onClick: (e: MouseEvent) => {
      if (e.detail === 0) {
        latch.pointerDown('a11y', k.vk);
        latch.pointerUp('a11y');
      }
    },
  });

  const label = (k: KeyDef) => t(`key.${k.id}` as Key);

  return (
    <div class="kb">
      <label class="sect" for="msg">
        {t('kb.label')}
      </label>
      <textarea
        id="msg"
        class="field"
        dir="auto"
        rows={4}
        autocomplete="off"
        autocapitalize="sentences"
        spellcheck={false}
        value={text}
        onInput={(e) => setText((e.currentTarget as HTMLTextAreaElement).value)}
      />
      <button class="btn primary" disabled={!text} onClick={send}>
        {sent ? t('kb.sent') : t('kb.send')}
      </button>

      <div class="sect">{t('kb.keys')}</div>
      {/* Physical layout (modifier order, arrow keys left/right) must not mirror in RTL. */}
      <div class="keys" dir="ltr">
        {MODS.map((k) => {
          const st = latch.stateOf(k.vk);
          return (
            <button
              key={k.id}
              class={`key mod${st ? ' on' : ''}${st === 'locked' ? ' lock' : ''}`}
              aria-pressed={st !== undefined}
              aria-label={st === 'locked' ? `${label(k)}, ${t('key.locked')}` : label(k)}
              {...modProps(k)}
            >
              {k.icon ? <Icon name={k.icon} size={22} fill={k.icon === 'win'} /> : label(k)}
            </button>
          );
        })}
        {KEYS.map((k) => (
          <button key={k.id} class={`key${k.wide ? ' wide' : ''}`} aria-label={label(k)} {...keyProps(k)}>
            {k.icon ? <Icon name={k.icon} size={22} /> : label(k)}
          </button>
        ))}
      </div>

      <div class="sect">{t('kb.shortcuts')}</div>
      <div class="chips">
        {SHORTCUTS.map((s) => (
          <button key={s.label} class="chip" dir="ltr" onClick={() => chord(s.vks)} onContextMenu={(e) => e.preventDefault()}>
            {s.label}
          </button>
        ))}
      </div>
    </div>
  );
}
