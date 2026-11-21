import { chacha20poly1305 } from '@noble/ciphers/chacha.js';
import { hkdf } from '@noble/hashes/hkdf.js';
import { hmac } from '@noble/hashes/hmac.js';
import { sha256 } from '@noble/hashes/sha2.js';
import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { b64encode, bytesToHex, hexToBytes } from './b64.ts';
import {
  authMac,
  DIR_C2S,
  DIR_S2C,
  FrameReceiver,
  FrameSender,
  openFrame,
  pairHelloMac,
  pairingCode,
  randomBytes,
  readyMac,
  sealFrame,
  sessionKeys,
  unwrapSecret,
  wrapSecret,
} from './crypto.ts';

// The same file the C# core tests generate and check (tests/vectors.json).
const V = JSON.parse(readFileSync(new URL('../../../tests/vectors.json', import.meta.url), 'utf8'));
const h = hexToBytes;
const hex = bytesToHex;
const ascii = (s: string) => new TextEncoder().encode(s);

describe('RFC vectors (noble primitives)', () => {
  it('HKDF-SHA256, RFC 5869 A.1', () => {
    const r = V.rfc.hkdf_rfc5869_a1;
    expect(hex(hkdf(sha256, h(r.ikm), h(r.salt), h(r.info), r.length))).toBe(r.okm);
  });
  it('HMAC-SHA256, RFC 4231 case 1', () => {
    const r = V.rfc.hmac_rfc4231_case1;
    expect(hex(hmac(sha256, h(r.key), h(r.data)))).toBe(r.mac);
  });
  it('ChaCha20-Poly1305, RFC 8439 2.8.2', () => {
    const r = V.rfc.chacha20poly1305_rfc8439_2_8_2;
    const out = chacha20poly1305(h(r.key), h(r.nonce), h(r.aad)).encrypt(h(r.plaintext));
    expect(hex(out)).toBe(r.ciphertext + r.tag);
  });
});

describe('shared protocol vectors', () => {
  const p = V.pairing;
  const c = V.connect;

  it('pairing: hello MAC (ASCII and Hebrew names), code, wrap', () => {
    expect(hex(pairHelloMac(h(p.k), h(p.nc), p.name))).toBe(p.mac);
    expect(hex(pairHelloMac(h(p.k), h(p.nc), p.nameHe))).toBe(p.macHe);
    expect(pairingCode(h(p.k), h(p.nc), h(p.ns))).toBe(p.code);
    expect(hex(wrapSecret(h(p.k), h(p.nc), h(p.ns), h(p.deviceSecret)))).toBe(p.wrap);
    expect(hex(unwrapSecret(h(p.k), h(p.nc), h(p.ns), h(p.wrap)))).toBe(p.deviceSecret);
    expect(b64encode(h(p.wrap))).toBe(p.wrapB64);
    expect(b64encode(h(p.k))).toBe(p.kB64);
  });

  it('connect: auth/ready MACs and session keys', () => {
    const S = h(c.deviceSecret);
    expect(hex(authMac(S, h(c.nc), h(c.ns)))).toBe(c.clientMac);
    expect(hex(readyMac(S, h(c.nc), h(c.ns)))).toBe(c.serverMac);
    const k = sessionKeys(S, h(c.nc), h(c.ns));
    expect(hex(k.c2s)).toBe(c.keyC2S);
    expect(hex(k.s2c)).toBe(c.keyS2C);
    expect(b64encode(h(c.id))).toBe(c.idB64);
  });

  it('every frame seals to the vector bytes and opens back', () => {
    expect(V.frames.length).toBeGreaterThanOrEqual(9);
    for (const f of V.frames) {
      const dir = f.dir === 'c2s' ? DIR_C2S : DIR_S2C;
      const key = h(f.dir === 'c2s' ? c.keyC2S : c.keyS2C);
      expect(hex(sealFrame(key, dir, f.counter, h(f.plaintext)))).toBe(f.frame);
      const opened = openFrame(key, dir, h(f.frame));
      expect(opened.counter).toBe(f.counter);
      expect(hex(opened.plaintext)).toBe(f.plaintext);
    }
  });

  it('receive sequence: replay, gap, reorder, tamper and counter 0 behave as specified', () => {
    const rx = new FrameReceiver(h(c.keyC2S), DIR_C2S);
    for (const s of V.receiveSequenceC2S) {
      const got = rx.open(h(s.frame));
      expect(got !== null ? 'accept' : 'drop', s.description).toBe(s.expect);
    }
  });
});

describe('frames', () => {
  const key = randomBytes(32);
  const msg = (n: number) => Uint8Array.of(0x7f, n);

  it('round-trips with strictly increasing counters starting at 1', () => {
    const tx = new FrameSender(key, DIR_C2S);
    const rx = new FrameReceiver(key, DIR_C2S);
    const frames = [tx.seal(msg(1)), tx.seal(msg(2)), tx.seal(msg(3))];
    expect(new DataView(frames[0]!.buffer).getBigUint64(0)).toBe(1n);
    expect(new DataView(frames[2]!.buffer).getBigUint64(0)).toBe(3n);
    frames.forEach((f, i) => expect(rx.open(f)).toEqual(msg(i + 1)));
  });

  it('rejects replayed frames', () => {
    const tx = new FrameSender(key, DIR_C2S);
    const rx = new FrameReceiver(key, DIR_C2S);
    const f1 = tx.seal(msg(1));
    expect(rx.open(f1)).not.toBeNull();
    expect(rx.open(f1)).toBeNull();
  });

  it('rejects reordered (older) frames but accepts gaps', () => {
    const tx = new FrameSender(key, DIR_C2S);
    const rx = new FrameReceiver(key, DIR_C2S);
    const f1 = tx.seal(msg(1));
    const f2 = tx.seal(msg(2));
    const f3 = tx.seal(msg(3));
    expect(rx.open(f3)).not.toBeNull(); // f1, f2 lost
    expect(rx.open(f2)).toBeNull();
    expect(rx.open(f1)).toBeNull();
  });

  it('rejects tampered counter, ciphertext and tag; a rejected frame does not advance the window', () => {
    const tx = new FrameSender(key, DIR_C2S);
    const rx = new FrameReceiver(key, DIR_C2S);
    const good = tx.seal(msg(1));
    for (const i of [7, 8, good.length - 1]) {
      const bad = good.slice();
      bad[i] = bad[i]! ^ 1;
      expect(rx.open(bad)).toBeNull();
    }
    expect(rx.open(good)).not.toBeNull();
  });

  it('rejects wrong direction, wrong key, truncated and oversized frames', () => {
    const tx = new FrameSender(key, DIR_C2S);
    const f = tx.seal(msg(1));
    expect(new FrameReceiver(key, DIR_S2C).open(f)).toBeNull();
    expect(new FrameReceiver(randomBytes(32), DIR_C2S).open(f)).toBeNull();
    expect(new FrameReceiver(key, DIR_C2S).open(f.slice(0, 20))).toBeNull();
    expect(new FrameReceiver(key, DIR_C2S).open(new Uint8Array(5000))).toBeNull();
  });

  it('rejects a counter of zero or beyond 2^53', () => {
    const rx = new FrameReceiver(key, DIR_C2S);
    expect(rx.open(sealFrame(key, DIR_C2S, 0, msg(1)))).toBeNull();
    const huge = sealFrame(key, DIR_C2S, 1, msg(1));
    huge.set([0xff, 0, 0, 0, 0, 0, 0, 1], 0);
    expect(rx.open(huge)).toBeNull();
  });

  it('labels are ASCII (guards against accidental encoding changes)', () => {
    expect(ascii('c2s')).toEqual(Uint8Array.of(0x63, 0x32, 0x73));
  });
});
