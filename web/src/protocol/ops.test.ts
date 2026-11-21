import { describe, expect, it } from 'vitest';
import { BUTTON, decodeOp, encButton, encJson, encKey, encMedia, encMove, encText, encWheel, MAX_TEXT_BYTES, splitText } from './ops.ts';

describe('op encoding', () => {
  it('move is op, i16 dx, i16 dy big-endian, clamped', () => {
    expect(encMove(1, -2)).toEqual(Uint8Array.of(0x01, 0, 1, 0xff, 0xfe));
    expect(decodeOp(encMove(100000, -100000))).toEqual({ op: 'move', dx: 32767, dy: -32768 });
    expect(decodeOp(encWheel(-120, 240))).toEqual({ op: 'wheel', dx: -120, dy: 240 });
  });
  it('button, key, media', () => {
    expect(encButton(BUTTON.right, true)).toEqual(Uint8Array.of(0x02, 1, 1));
    expect(encKey(0x1b, 3)).toEqual(Uint8Array.of(0x05, 0x00, 0x1b, 3));
    expect(decodeOp(encKey(0x5b, 1))).toEqual({ op: 'key', vk: 0x5b, flags: 1 });
    expect(encMedia(6)).toEqual(Uint8Array.of(0x06, 6));
  });
  it('json round-trips non-ASCII', () => {
    expect(decodeOp(encJson({ t: 'x', v: 'שלום' }))).toEqual({ op: 'json', value: { t: 'x', v: 'שלום' } });
  });
  it('rejects malformed payloads', () => {
    expect(decodeOp(Uint8Array.of(0x01, 0))).toBeNull();
    expect(decodeOp(Uint8Array.of(0x99))).toBeNull();
    expect(decodeOp(Uint8Array.of(0x10, 0x7b))).toBeNull();
  });
});

describe('text splitting', () => {
  it('keeps short text in one frame', () => {
    expect(encText('hello')).toHaveLength(1);
    expect(decodeOp(encText('שלום עולם')[0]!)).toEqual({ op: 'text', text: 'שלום עולם' });
  });
  it('splits on code points, never mid-character, within 1024 bytes', () => {
    const text = 'שלום😀'.repeat(300); // 2-byte and 4-byte code points
    const parts = splitText(text);
    expect(parts.length).toBeGreaterThan(1);
    for (const p of parts) expect(new TextEncoder().encode(p).length).toBeLessThanOrEqual(MAX_TEXT_BYTES);
    expect(parts.join('')).toBe(text);
    for (const f of encText(text)) expect(decodeOp(f)?.op).toBe('text');
  });
  it('sends nothing for empty text', () => {
    expect(encText('')).toEqual([]);
  });
});
