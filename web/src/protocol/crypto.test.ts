import { createCipheriv, createHmac, hkdfSync } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { bytesToHex, hexToBytes } from './b64.ts';
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

// The same file the C# core tests load.
const V = JSON.parse(readFileSync(new URL('../../../tests/vectors.json', import.meta.url), 'utf8'));
const h = hexToBytes;
const hex = bytesToHex;

describe('shared test vectors', () => {
  const p = V.pairing;
  const c = V.connect;

  it('pairing: hello MAC, code, wrap', () => {
    expect(hex(pairHelloMac(h(p.k), h(p.nc), p.name))).toBe(p.helloMac);
    expect(pairingCode(h(p.k), h(p.nc), h(p.ns))).toBe(p.code);
    expect(hex(wrapSecret(h(p.k), h(p.nc), h(p.ns), h(p.secret)))).toBe(p.wrap);
    expect(hex(unwrapSecret(h(p.k), h(p.nc), h(p.ns), h(p.wrap)))).toBe(p.secret);
  });

  it('connect: auth/ready MACs and session keys', () => {
    expect(hex(authMac(h(c.secret), h(c.nc), h(c.ns)))).toBe(c.authMac);
    expect(hex(readyMac(h(c.secret), h(c.nc), h(c.ns)))).toBe(c.readyMac);
    const k = sessionKeys(h(c.secret), h(c.nc), h(c.ns));
    expect(hex(k.c2s)).toBe(c.kc2s);
    expect(hex(k.s2c)).toBe(c.ks2c);
  });

  it('frames seal to the vector bytes and open back', () => {
    for (const f of V.frames) {
      const dir = f.dir === 'c2s' ? DIR_C2S : DIR_S2C;
      const key = h(f.dir === 'c2s' ? c.kc2s : c.ks2c);
      expect(hex(sealFrame(key, dir, f.counter, h(f.plaintext)))).toBe(f.frame);
      const opened = openFrame(key, dir, h(f.frame));
      expect(opened.counter).toBe(f.counter);
      expect(hex(opened.plaintext)).toBe(f.plaintext);
    }
  });
});

describe('independent cross-check against node:crypto', () => {
  it('HKDF-SHA256, HMAC-SHA256 and ChaCha20-Poly1305 agree with the noble-based code', () => {
    const p = V.pairing;
    const salt = Buffer.concat([Buffer.from(p.nc, 'hex'), Buffer.from(p.ns, 'hex')]);
    const codeKey = Buffer.from(hkdfSync('sha256', Buffer.from(p.k, 'hex'), salt, 'code', 32));
    expect(codeKey.toString('hex')).toBe(p.codeKey);
    expect(String(codeKey.readUInt32BE(0) % 10000).padStart(4, '0')).toBe(p.code);

    const mac = createHmac('sha256', Buffer.from(p.k, 'hex'))
      .update('hello')
      .update(Buffer.from(p.nc, 'hex'))
      .update(Buffer.from(p.name, 'utf8'))
      .digest('hex');
    expect(mac).toBe(p.helloMac);

    const wrapKey = Buffer.from(hkdfSync('sha256', Buffer.from(p.k, 'hex'), salt, 'wrap', 32));
    const cipher = createCipheriv('chacha20-poly1305', wrapKey, Buffer.alloc(12), { authTagLength: 16 });
    const ct = Buffer.concat([cipher.update(Buffer.from(p.secret, 'hex')), cipher.final(), cipher.getAuthTag()]);
    expect(ct.toString('hex')).toBe(p.wrap);

    const f = V.frames[0];
    const nonce = Buffer.concat([Buffer.from([0, 0, 0, 0]), Buffer.from(f.frame.slice(0, 16), 'hex')]);
    const fc = createCipheriv('chacha20-poly1305', Buffer.from(V.connect.kc2s, 'hex'), nonce, { authTagLength: 16 });
    const fct = Buffer.concat([fc.update(Buffer.from(f.plaintext, 'hex')), fc.final(), fc.getAuthTag()]);
    expect(f.frame).toBe(f.frame.slice(0, 16) + fct.toString('hex'));
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

  it('bytesToHex/hexToBytes round-trip', () => {
    const b = randomBytes(20);
    expect(hexToBytes(bytesToHex(b))).toEqual(b);
  });
});
