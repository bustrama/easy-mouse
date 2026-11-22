import { describe, expect, it } from 'vitest';
import { coverFor, makeArt } from './art.ts';
import { parseState } from './pcstate.ts';

describe('album art', () => {
  it('builds a data: URL (the CSP allows img-src data:, not blob:) that decodes back to the bytes', () => {
    const bytes = Uint8Array.of(0xff, 0xd8, 0xff, 0xe0, 0, 1, 2, 3, 0xff, 0xd9);
    const art = makeArt('a1', 'jpeg', bytes);
    expect(art.id).toBe('a1');
    expect(art.url.startsWith('data:image/jpeg;base64,')).toBe(true);
    expect([...Buffer.from(art.url.split(',')[1]!, 'base64')]).toEqual([...bytes]);
    expect(makeArt('b', 'png', bytes).url.startsWith('data:image/png;base64,')).toBe(true);
  });

  it('handles the largest allowed image', () => {
    const big = new Uint8Array(128 * 1024).map((_, i) => i & 0xff);
    const art = makeArt('big', 'jpeg', big);
    expect(Buffer.from(art.url.split(',')[1]!, 'base64').equals(Buffer.from(big))).toBe(true);
  });

  it('shows the image only while it matches the state artId', () => {
    const art = { id: 'a1', url: 'data:x' };
    expect(coverFor(art, 'a1')).toBe('data:x');
    expect(coverFor(art, 'b2')).toBeNull(); // the track changed, the new image has not arrived yet
    expect(coverFor(art, null)).toBeNull(); // no art for this track
    expect(coverFor(null, 'a1')).toBeNull();
  });
});

describe('artId in the state frame', () => {
  it('is read when valid and null otherwise', () => {
    expect(parseState({ t: 'state', artId: 'a1b2', host: {} }).artId).toBe('a1b2');
    expect(parseState({ t: 'state', artId: null, host: {} }).artId).toBeNull();
    expect(parseState({ t: 'state', host: {} }).artId).toBeNull();
    expect(parseState({ t: 'state', artId: 'Bad Id!', host: {} }).artId).toBeNull();
    expect(parseState({ t: 'state', artId: 5, host: {} }).artId).toBeNull();
  });
});
