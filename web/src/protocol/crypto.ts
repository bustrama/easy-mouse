import { chacha20poly1305 } from '@noble/ciphers/chacha.js';
import { equalBytes } from '@noble/ciphers/utils.js';
import { hkdf } from '@noble/hashes/hkdf.js';
import { hmac } from '@noble/hashes/hmac.js';
import { sha256 } from '@noble/hashes/sha2.js';
import { concatBytes } from '@noble/hashes/utils.js';

const enc = new TextEncoder();
const ascii = (s: string) => enc.encode(s);

export const NONCE_LEN = 16;
export const SECRET_LEN = 32;
export const MAX_FRAME = 4096;

export const randomBytes = (n: number): Uint8Array => crypto.getRandomValues(new Uint8Array(n));

/** HKDF-SHA256, 32 bytes: ikm, salt, ASCII info label. */
export function kdf(ikm: Uint8Array, salt: Uint8Array, info: string): Uint8Array {
  return hkdf(sha256, ikm, salt, ascii(info), 32);
}

export const mac = (key: Uint8Array, ...parts: Uint8Array[]): Uint8Array => hmac(sha256, key, concatBytes(...parts));

export const macEqual = (a: Uint8Array, b: Uint8Array): boolean => a.length === b.length && equalBytes(a, b);

// ---- Pairing ----

export function pairHelloMac(k: Uint8Array, nc: Uint8Array, name: string): Uint8Array {
  return mac(k, ascii('hello'), nc, enc.encode(name));
}

/** 4 decimal digits, shown on both the phone and the PC. */
export function pairingCode(k: Uint8Array, nc: Uint8Array, ns: Uint8Array): string {
  const d = kdf(k, concatBytes(nc, ns), 'code');
  const n = new DataView(d.buffer, d.byteOffset, 4).getUint32(0, false) % 10000;
  return String(n).padStart(4, '0');
}

const ZERO_NONCE = new Uint8Array(12);

/** Server side of `wrap`; also used by the mock server and tests. */
export function wrapSecret(k: Uint8Array, nc: Uint8Array, ns: Uint8Array, secret: Uint8Array): Uint8Array {
  const key = kdf(k, concatBytes(nc, ns), 'wrap');
  return chacha20poly1305(key, ZERO_NONCE).encrypt(secret);
}

export function unwrapSecret(k: Uint8Array, nc: Uint8Array, ns: Uint8Array, wrap: Uint8Array): Uint8Array {
  const key = kdf(k, concatBytes(nc, ns), 'wrap');
  const s = chacha20poly1305(key, ZERO_NONCE).decrypt(wrap);
  if (s.length !== SECRET_LEN) throw new Error('bad device secret length');
  return s;
}

// ---- Connect ----

export const authMac = (s: Uint8Array, nc: Uint8Array, ns: Uint8Array): Uint8Array => mac(s, ascii('c'), nc, ns);
export const readyMac = (s: Uint8Array, nc: Uint8Array, ns: Uint8Array): Uint8Array => mac(s, ascii('s'), nc, ns);

export interface SessionKeys {
  c2s: Uint8Array;
  s2c: Uint8Array;
}

export function sessionKeys(s: Uint8Array, nc: Uint8Array, ns: Uint8Array): SessionKeys {
  const salt = concatBytes(nc, ns);
  return { c2s: kdf(s, salt, 'c2s'), s2c: kdf(s, salt, 's2c') };
}

// ---- Frames ----

export const DIR_C2S = 0;
export const DIR_S2C = 1;

function frameNonce(dir: number, counterBytes: Uint8Array): Uint8Array {
  const n = new Uint8Array(12);
  n[0] = dir;
  n.set(counterBytes, 4);
  return n;
}

/** Counters are u64 big-endian on the wire; JS numbers are exact up to 2^53, far beyond a session's life. */
export function encodeCounter(counter: number): Uint8Array {
  const out = new Uint8Array(8);
  new DataView(out.buffer).setBigUint64(0, BigInt(counter), false);
  return out;
}

export function sealFrame(key: Uint8Array, dir: number, counter: number, plaintext: Uint8Array): Uint8Array {
  const ctr = encodeCounter(counter);
  const ct = chacha20poly1305(key, frameNonce(dir, ctr)).encrypt(plaintext);
  return concatBytes(ctr, ct);
}

export class FrameError extends Error {}

/** Opens a frame; the caller owns the replay window. Throws FrameError on malformed or forged input. */
export function openFrame(key: Uint8Array, dir: number, frame: Uint8Array): { counter: number; plaintext: Uint8Array } {
  if (frame.length < 8 + 16 + 1 || frame.length > MAX_FRAME + 64) throw new FrameError('bad frame length');
  const ctrBytes = frame.subarray(0, 8);
  const big = new DataView(frame.buffer, frame.byteOffset, 8).getBigUint64(0, false);
  if (big > BigInt(Number.MAX_SAFE_INTEGER)) throw new FrameError('counter out of range');
  try {
    const plaintext = chacha20poly1305(key, frameNonce(dir, ctrBytes)).decrypt(frame.subarray(8));
    return { counter: Number(big), plaintext };
  } catch {
    throw new FrameError('authentication failed');
  }
}

/** Sends with strictly increasing counters starting at 1. */
export class FrameSender {
  private counter = 0;
  private readonly key: Uint8Array;
  private readonly dir: number;
  constructor(key: Uint8Array, dir: number) {
    this.key = key;
    this.dir = dir;
  }
  seal(plaintext: Uint8Array): Uint8Array {
    return sealFrame(this.key, this.dir, ++this.counter, plaintext);
  }
}

/** Accepts only counters strictly greater than the last accepted one; anything else is dropped (null). */
export class FrameReceiver {
  private last = 0;
  private readonly key: Uint8Array;
  private readonly dir: number;
  constructor(key: Uint8Array, dir: number) {
    this.key = key;
    this.dir = dir;
  }
  open(frame: Uint8Array): Uint8Array | null {
    let opened;
    try {
      opened = openFrame(this.key, this.dir, frame);
    } catch {
      return null;
    }
    if (opened.counter <= this.last) return null;
    this.last = opened.counter;
    return opened.plaintext;
  }
}
