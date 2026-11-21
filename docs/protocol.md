# Easy Mouse - Wire protocol (draft v0)

Status: draft. The Core implementation (`src/Core`) and the web client (`web/`)
both follow this file. Change it in the same pull request that changes either
side. Algorithms are fixed here so both sides agree; details marked TBD are for
the implementer to settle and write back.

## Transport

- `GET /` and static assets: the embedded web app.
- `GET /api/info`: `{ "name": "Office PC", "version": "0.1.0", "proto": 0 }`.
- `GET /ws` (WebSocket, binary and text frames): all control traffic.
- Host header must be the PC's own IP or `<pcname>.local`. A WebSocket upgrade
  with a foreign `Origin` is rejected. No CORS headers, no cookies.

## Primitives

- KDF: HKDF-SHA256. MAC: HMAC-SHA256. AEAD: ChaCha20-Poly1305 (12-byte nonce, 16-byte tag).
- Randomness: 128-bit nonces from a CSPRNG. Browser: `crypto.getRandomValues`
  (available on plain HTTP). Libraries: `@noble/ciphers`, `@noble/hashes`, pinned exactly.
- All text frames are JSON, UTF-8. `b64` is base64url without padding.

## Pairing (once per device, only while the QR window is open)

QR content: `http://<host>:<port>/#k=<b64 16 bytes>`. `k` is single-use, valid 2
minutes. The page reads `k`, then removes it with `history.replaceState`.

1. C->S `{ "t":"pair.hello", "name":"Dana's iPhone", "nc":"<b64 16B>", "mac":"<b64 HMAC(k, "hello"|nc|name)>" }`
2. S->C `{ "t":"pair.pending", "ns":"<b64 16B>" }`. Both sides compute the
   confirmation code: first 4 decimal digits of `HKDF(k, nc|ns, "code")`, shown
   on the phone and on the PC dialog. The user must see the same number.
3. PC user clicks Allow. S->C `{ "t":"pair.ok", "id":"<b64 8B device id>", "wrap":"<b64 AEAD(key=HKDF(k, nc|ns, "wrap"), nonce=0, plaintext=S)>" }`
   where `S` is a fresh 256-bit device secret. The phone stores `id` and `S` in
   browser storage. Deny: `{ "t":"pair.denied" }`.
4. `k` is now spent. Five failed `pair.hello` attempts regenerate `k`.

## Connect (every session)

1. C->S `{ "t":"hello", "id":"<b64>", "nc":"<b64 16B>" }`
2. S->C `{ "t":"challenge", "ns":"<b64 16B>" }`
3. C->S `{ "t":"auth", "mac":"<b64 HMAC(S, "c"|nc|ns)>" }`
4. S->C `{ "t":"ready", "mac":"<b64 HMAC(S, "s"|nc|ns)>" }`. The client verifies it.
   Unknown, revoked or expired `id`: server closes the socket with code 4401.
5. Both derive two keys: `Kc2s = HKDF(S, nc|ns, "c2s")`, `Ks2c = HKDF(S, nc|ns, "s2c")`.

One controlling device at a time: a second authenticated socket gets
`{ "t":"busy" }` and may send `{ "t":"takeover" }`, which the PC user approves.

## Frames after `ready`

Every WebSocket message is binary: `nonce_counter (u64 BE, 8 bytes) | ciphertext | tag`.
The AEAD nonce is `dir (1 byte: 0 c2s, 1 s2c) | 3 zero bytes | counter (8 bytes)`.
The receiver requires each counter to be strictly greater than the last one seen
and drops anything else (replay and reorder protection). Counters start at 1.

Plaintext layout: `op (u8) | payload`. Integers are big-endian.

| op | name | payload | direction |
|---|---|---|---|
| 0x01 | move | `i16 dx, i16 dy` | C->S |
| 0x02 | button | `u8 button (0 left, 1 right, 2 middle), u8 down (1) or up (0)` | C->S |
| 0x03 | wheel | `i16 dx, i16 dy` (units of 1/120 notch) | C->S |
| 0x04 | text | UTF-8 string, max 1024 bytes | C->S |
| 0x05 | key | `u16 virtual-key, u8 flags (1 down, 2 up, 3 press)` | C->S |
| 0x06 | media | `u8 cmd (0 playpause, 1 next, 2 prev, 3 stop, 4 volup, 5 voldown, 6 mute)` | C->S |
| 0x10 | json | UTF-8 JSON control message (remote actions, settings) | both |
| 0x11 | state | UTF-8 JSON: now playing, volume, lock/elevation status | S->C |
| 0x7F | ping | empty | both |

Limits enforced by the server: input frames per second, text length, one
controller. Frames larger than 4 KiB are rejected. TBD by the Core implementer:
exact rate limits, and the JSON schemas of 0x10/0x11 (write them back here).

## Errors

The server closes with: 4400 malformed, 4401 unauthorised or revoked, 4408
pairing window closed or expired, 4429 rate limited, 4409 replaced by another
controller.

## Test vectors

The Core implementer adds fixed vectors for the HKDF/HMAC/AEAD steps in
`tests/vectors.json`; the web client's unit tests must pass the same vectors.
