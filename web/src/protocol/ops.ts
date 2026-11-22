// Plaintext op encoders/decoders (docs/protocol.md, "Frames after ready").
// Node-compatible (no parameter properties, explicit .ts imports): the dev mock server imports this file.

export const OP = {
  move: 0x01,
  button: 0x02,
  wheel: 0x03,
  text: 0x04,
  key: 0x05,
  media: 0x06,
  json: 0x10,
  state: 0x11,
  art: 0x12,
  ping: 0x7f,
} as const;

export const BUTTON = { left: 0, right: 1, middle: 2 } as const;
export const MEDIA = { playpause: 0, next: 1, prev: 2, stop: 3, volup: 4, voldown: 5, mute: 6 } as const;
export const KEYFLAG = { down: 1, up: 2, press: 3 } as const;

export const MAX_TEXT_BYTES = 1024;
export const MAX_ART_BYTES = 128 * 1024;

const enc = new TextEncoder();
const dec = new TextDecoder('utf-8', { fatal: true });

export const clamp16 = (n: number): number => Math.max(-32768, Math.min(32767, Math.trunc(n)));

function i16pair(op: number, a: number, b: number): Uint8Array {
  const out = new Uint8Array(5);
  const v = new DataView(out.buffer);
  out[0] = op;
  v.setInt16(1, clamp16(a), false);
  v.setInt16(3, clamp16(b), false);
  return out;
}

export const encMove = (dx: number, dy: number): Uint8Array => i16pair(OP.move, dx, dy);
export const encWheel = (dx: number, dy: number): Uint8Array => i16pair(OP.wheel, dx, dy);
export const encButton = (button: number, down: boolean): Uint8Array => Uint8Array.of(OP.button, button, down ? 1 : 0);
export const encMedia = (cmd: number): Uint8Array => Uint8Array.of(OP.media, cmd);
export const encPing = (): Uint8Array => Uint8Array.of(OP.ping);

export function encKey(vk: number, flags: number): Uint8Array {
  const out = new Uint8Array(4);
  out[0] = OP.key;
  new DataView(out.buffer).setUint16(1, vk, false);
  out[3] = flags;
  return out;
}

function withOp(op: number, body: Uint8Array): Uint8Array {
  const out = new Uint8Array(1 + body.length);
  out[0] = op;
  out.set(body, 1);
  return out;
}

export const encJson = (obj: unknown): Uint8Array => withOp(OP.json, enc.encode(JSON.stringify(obj)));

/** Splits on code-point boundaries so every piece is at most `maxBytes` of UTF-8. */
export function splitText(text: string, maxBytes: number = MAX_TEXT_BYTES): string[] {
  const parts: string[] = [];
  let cur = '';
  let curBytes = 0;
  for (const ch of text) {
    const n = enc.encode(ch).length;
    if (curBytes + n > maxBytes) {
      parts.push(cur);
      cur = '';
      curBytes = 0;
    }
    cur += ch;
    curBytes += n;
  }
  if (cur) parts.push(cur);
  return parts;
}

export const encText = (text: string): Uint8Array[] => splitText(text).map((p) => withOp(OP.text, enc.encode(p)));

// ---- Decoding (server -> client, and the mock server's view of client frames) ----

export type Decoded =
  | { op: 'move'; dx: number; dy: number }
  | { op: 'wheel'; dx: number; dy: number }
  | { op: 'button'; button: number; down: boolean }
  | { op: 'text'; text: string }
  | { op: 'key'; vk: number; flags: number }
  | { op: 'media'; cmd: number }
  | { op: 'json'; value: unknown }
  | { op: 'state'; value: unknown }
  | { op: 'art'; id: string; type: 'jpeg' | 'png'; data: Uint8Array }
  | { op: 'ping' };

export function decodeOp(p: Uint8Array): Decoded | null {
  if (p.length < 1) return null;
  const v = new DataView(p.buffer, p.byteOffset, p.byteLength);
  try {
    switch (p[0]) {
      case OP.move:
        return p.length === 5 ? { op: 'move', dx: v.getInt16(1, false), dy: v.getInt16(3, false) } : null;
      case OP.wheel:
        return p.length === 5 ? { op: 'wheel', dx: v.getInt16(1, false), dy: v.getInt16(3, false) } : null;
      case OP.button:
        return p.length === 3 ? { op: 'button', button: p[1]!, down: p[2] === 1 } : null;
      case OP.text:
        return p.length - 1 <= MAX_TEXT_BYTES ? { op: 'text', text: dec.decode(p.subarray(1)) } : null;
      case OP.key:
        return p.length === 4 ? { op: 'key', vk: v.getUint16(1, false), flags: p[3]! } : null;
      case OP.media:
        return p.length === 2 ? { op: 'media', cmd: p[1]! } : null;
      case OP.json:
        return { op: 'json', value: JSON.parse(dec.decode(p.subarray(1))) };
      case OP.state:
        return { op: 'state', value: JSON.parse(dec.decode(p.subarray(1))) };
      case OP.art: {
        // 0x12 | u8 idLen (1..32) | id (a-z0-9) | u8 type (0 jpeg, 1 png) | image (1..128 KiB)
        const idLen = p[1] ?? 0;
        if (idLen < 1 || idLen > 32 || p.length < 2 + idLen + 2) return null;
        const id = String.fromCharCode(...p.subarray(2, 2 + idLen));
        if (!/^[a-z0-9]+$/.test(id)) return null;
        const type = p[2 + idLen];
        if (type !== 0 && type !== 1) return null;
        const data = p.subarray(3 + idLen);
        return data.length <= MAX_ART_BYTES ? { op: 'art', id, type: type === 0 ? 'jpeg' : 'png', data } : null;
      }
      case OP.ping:
        return p.length === 1 ? { op: 'ping' } : null;
      default:
        return null;
    }
  } catch {
    return null;
  }
}

export function encArt(id: string, type: 'jpeg' | 'png', data: Uint8Array): Uint8Array {
  const idb = enc.encode(id);
  const out = new Uint8Array(3 + idb.length + data.length);
  out[0] = OP.art;
  out[1] = idb.length;
  out.set(idb, 2);
  out[2 + idb.length] = type === 'jpeg' ? 0 : 1;
  out.set(data, 3 + idb.length);
  return out;
}

export const encState = (obj: unknown): Uint8Array => withOp(OP.state, enc.encode(JSON.stringify(obj)));
