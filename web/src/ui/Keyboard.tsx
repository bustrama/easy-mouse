import { useEffect, useRef, useState } from 'preact/hooks';
import { client } from '../client.ts';
import type { Key } from '../i18n/strings.ts';
import { useT } from '../i18n/useT.ts';
import { KEYFLAG } from '../protocol/ops.ts';

interface KeyDef {
  id: string;
  vk: number;
}

const MODS: KeyDef[] = [
  { id: 'Ctrl', vk: 0xa2 },
  { id: 'Alt', vk: 0xa4 },
  { id: 'Shift', vk: 0xa0 },
  { id: 'Win', vk: 0x5b },
];

const KEYS: KeyDef[] = [
  { id: 'Esc', vk: 0x1b },
  { id: 'Tab', vk: 0x09 },
  { id: 'Enter', vk: 0x0d },
  { id: 'Delete', vk: 0x2e },
  { id: 'Left', vk: 0x25 },
  { id: 'Up', vk: 0x26 },
  { id: 'Down', vk: 0x28 },
  { id: 'Right', vk: 0x27 },
  { id: 'Backspace', vk: 0x08 },
  { id: 'Space', vk: 0x20 },
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
  const [held, setHeld] = useState<ReadonlySet<number>>(new Set());
  const heldRef = useRef<Set<number>>(new Set());

  const syncHeld = () => setHeld(new Set(heldRef.current));

  const releaseAll = () => {
    for (const vk of heldRef.current) client.key(vk, KEYFLAG.up);
    heldRef.current.clear();
    syncHeld();
  };

  // A held modifier must never outlive the page, the tab or the connection.
  useEffect(() => {
    const stop = client.onRelease(releaseAll);
    return () => {
      stop();
      releaseAll();
    };
  }, []);

  const toggleMod = (vk: number) => {
    if (heldRef.current.has(vk)) {
      heldRef.current.delete(vk);
      client.key(vk, KEYFLAG.up);
    } else {
      heldRef.current.add(vk);
      client.key(vk, KEYFLAG.down);
    }
    syncHeld();
  };

  const press = (vk: number) => {
    client.key(vk, KEYFLAG.press);
    releaseAll(); // latched modifiers apply to one key
  };

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
      <div class="keys">
        {MODS.map((k) => (
          <button key={k.id} class={`key mod${held.has(k.vk) ? ' on' : ''}`} aria-pressed={held.has(k.vk)} onClick={() => toggleMod(k.vk)}>
            {t(`key.${k.id}` as Key)}
          </button>
        ))}
        {KEYS.map((k) => (
          <button key={k.id} class="key" onClick={() => press(k.vk)}>
            {t(`key.${k.id}` as Key)}
          </button>
        ))}
      </div>

      <div class="sect">{t('kb.shortcuts')}</div>
      <div class="chips">
        {SHORTCUTS.map((s) => (
          <button key={s.label} class="chip" dir="ltr" onClick={() => chord(s.vks)}>
            {s.label}
          </button>
        ))}
      </div>
    </div>
  );
}
