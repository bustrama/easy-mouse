import { describe, expect, it, vi } from 'vitest';
import { b64decode, b64encode } from './b64.ts';
import { parsePairingFragment, takePairingSecret } from './fragment.ts';

const k = Uint8Array.from({ length: 16 }, (_, i) => i * 17);
const kb64 = b64encode(k);

describe('base64url', () => {
  it('round-trips without padding', () => {
    for (let n = 0; n < 40; n++) {
      const b = Uint8Array.from({ length: n }, (_, i) => (i * 37 + n) & 0xff);
      const s = b64encode(b);
      expect(s).not.toMatch(/[+/=]/);
      expect(b64decode(s)).toEqual(b);
    }
  });
  it('rejects invalid input', () => {
    expect(() => b64decode('a+b/')).toThrow();
    expect(() => b64decode('a')).toThrow();
    expect(() => b64decode('AAAA=')).toThrow(); // padding
    expect(() => b64decode('AB')).toThrow(); // non-zero trailing bits
    expect(b64decode('AA')).toEqual(Uint8Array.of(0));
  });
});

describe('QR fragment', () => {
  it('parses #k=<b64 16 bytes>', () => {
    expect(parsePairingFragment(`#k=${kb64}`)).toEqual(k);
  });
  it('rejects missing, empty, wrong-length and malformed secrets', () => {
    expect(parsePairingFragment('')).toBeNull();
    expect(parsePairingFragment('#')).toBeNull();
    expect(parsePairingFragment('#k=')).toBeNull();
    expect(parsePairingFragment(`#k=${b64encode(new Uint8Array(15))}`)).toBeNull();
    expect(parsePairingFragment(`#k=${b64encode(new Uint8Array(17))}`)).toBeNull();
    expect(parsePairingFragment('#k=not*base64!')).toBeNull();
    expect(parsePairingFragment(`#x=${kb64}`)).toBeNull();
  });
  it('removes the fragment from the address bar via history.replaceState', () => {
    const replaceState = vi.fn();
    const got = takePairingSecret({ hash: `#k=${kb64}`, pathname: '/', search: '?a=1' }, { replaceState });
    expect(got).toEqual(k);
    expect(replaceState).toHaveBeenCalledWith(null, '', '/?a=1');
  });
  it('strips a malformed secret too, and leaves other fragments alone', () => {
    const replaceState = vi.fn();
    expect(takePairingSecret({ hash: '#k=bad!', pathname: '/', search: '' }, { replaceState })).toBeNull();
    expect(replaceState).toHaveBeenCalledTimes(1);
    const untouched = vi.fn();
    expect(takePairingSecret({ hash: '', pathname: '/', search: '' }, { replaceState: untouched })).toBeNull();
    expect(untouched).not.toHaveBeenCalled();
  });
});
