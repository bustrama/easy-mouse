// Independent check of tests/vectors.json using Node's crypto (OpenSSL), not our C# code.
import { readFileSync } from 'node:fs';
import { createHmac, hkdfSync, createCipheriv, createDecipheriv } from 'node:crypto';

const v = JSON.parse(readFileSync(process.argv[2], 'utf8'));
const h = (s) => Buffer.from(s, 'hex');
const hex = (b) => Buffer.from(b).toString('hex');
let failures = 0;
const eq = (name, got, want) => { if (got !== want) { failures++; console.log('FAIL', name, '\n got ', got, '\n want', want); } else console.log('ok  ', name); };

const hkdf = (ikm, salt, info, len) => Buffer.from(hkdfSync('sha256', ikm, salt, Buffer.from(info, 'ascii'), len));
const hmac = (key, data) => createHmac('sha256', key).update(data).digest();
const seal = (key, nonce, pt) => { const c = createCipheriv('chacha20-poly1305', key, nonce, { authTagLength: 16 }); const ct = Buffer.concat([c.update(pt), c.final()]); return Buffer.concat([ct, c.getAuthTag()]); };

// RFC section
const r = v.rfc.hkdf_rfc5869_a1;
eq('rfc5869 A.1', hex(hkdf(h(r.ikm), h(r.salt), Buffer.from(h(r.info)).toString('latin1'), r.length)), r.okm);
const m = v.rfc.hmac_rfc4231_case1;
eq('rfc4231 case 1', hex(hmac(h(m.key), h(m.data))), m.mac);
const a = v.rfc.chacha20poly1305_rfc8439_2_8_2;
{
  const c = createCipheriv('chacha20-poly1305', h(a.key), h(a.nonce), { authTagLength: 16 });
  c.setAAD(h(a.aad));
  const ct = Buffer.concat([c.update(h(a.plaintext)), c.final()]);
  eq('rfc8439 2.8.2 ciphertext', hex(ct), a.ciphertext);
  eq('rfc8439 2.8.2 tag', hex(c.getAuthTag()), a.tag);
}

// Pairing
const p = v.pairing;
const k = h(p.k), nc = h(p.nc), ns = h(p.ns), S = h(p.deviceSecret);
const salt = Buffer.concat([nc, ns]);
eq('pair mac', hex(hmac(k, Buffer.concat([Buffer.from('hello'), nc, Buffer.from(p.name, 'utf8')]))), p.mac);
eq('pair mac (hebrew)', hex(hmac(k, Buffer.concat([Buffer.from('hello'), nc, Buffer.from(p.nameHe, 'utf8')]))), p.macHe);
eq('pair code', String(hkdf(k, salt, 'code', 4).readUInt32BE(0) % 10000).padStart(4, '0'), p.code);
const wrapKey = hkdf(k, salt, 'wrap', 32);
eq('wrap key', hex(wrapKey), p.wrapKey);
eq('wrap', hex(seal(wrapKey, Buffer.alloc(12), S)), p.wrap);
eq('b64url k', k.toString('base64url'), p.kB64);
eq('b64url wrap', h(p.wrap).toString('base64url'), p.wrapB64);

// Connect
const c = v.connect;
const cnc = h(c.nc), cns = h(c.ns), cS = h(c.deviceSecret);
const csalt = Buffer.concat([cnc, cns]);
eq('client mac', hex(hmac(cS, Buffer.concat([Buffer.from('c'), cnc, cns]))), c.clientMac);
eq('server mac', hex(hmac(cS, Buffer.concat([Buffer.from('s'), cnc, cns]))), c.serverMac);
const kc2s = hkdf(cS, csalt, 'c2s', 32), ks2c = hkdf(cS, csalt, 's2c', 32);
eq('key c2s', hex(kc2s), c.keyC2S);
eq('key s2c', hex(ks2c), c.keyS2C);

// Frames: counter(8 BE) | ciphertext | tag; nonce = dir | 000 | counter
const frameFor = (dir, counter, key, pt) => {
  const ctr = Buffer.alloc(8); ctr.writeBigUInt64BE(BigInt(counter));
  const nonce = Buffer.alloc(12); nonce[0] = dir === 'c2s' ? 0 : 1; ctr.copy(nonce, 4);
  return Buffer.concat([ctr, seal(key, nonce, pt)]);
};
for (const f of v.frames) eq('frame ' + f.description, hex(frameFor(f.dir, f.counter, f.dir === 'c2s' ? kc2s : ks2c, h(f.plaintext))), f.frame);

// Receive sequence: replay/tamper semantics implemented independently here.
let last = 0n;
for (const step of v.receiveSequenceC2S) {
  const fr = h(step.frame);
  const counter = fr.readBigUInt64BE(0);
  let ok = counter > last;
  if (ok) {
    const nonce = Buffer.alloc(12); nonce[0] = 0; fr.subarray(0, 8).copy(nonce, 4);
    try {
      const d = createDecipheriv('chacha20-poly1305', kc2s, nonce, { authTagLength: 16 });
      d.setAuthTag(fr.subarray(fr.length - 16));
      d.update(fr.subarray(8, fr.length - 16)); d.final();
    } catch { ok = false; }
  }
  if (ok) last = counter;
  eq('sequence: ' + step.description, ok ? 'accept' : 'drop', step.expect);
}

console.log(failures ? `${failures} FAILURES` : 'ALL INDEPENDENT CHECKS PASSED');
process.exit(failures ? 1 : 0);
