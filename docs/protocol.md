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
  (available on plain HTTP; `crypto.subtle` is not, so the client uses the libraries).
  Libraries: `@noble/ciphers`, `@noble/hashes`, pinned exactly.
- All text frames are JSON, UTF-8. `b64` is base64url without padding.
- `HKDF(ikm, salt, info)` is HKDF-SHA256 (RFC 5869) with that input key material,
  salt and info, producing 32 bytes. `info` is the ASCII label.
- `|` joins raw bytes (decoded from b64, not the b64 text). Labels (`"hello"`,
  `"c"`, `"code"`, ...) are ASCII. `name` is UTF-8, 1 to 64 bytes.
- `HMAC(key, msg)` is HMAC-SHA256, 32 bytes. MACs are compared in constant time.
- AEAD calls use empty associated data everywhere.

## Pairing (once per device, only while the QR window is open)

QR content: `http://<host>:<port>/#k=<b64 16 bytes>`. `k` is single-use, valid 2
minutes. The page reads `k`, then removes it with `history.replaceState`.

1. C->S `{ "t":"pair.hello", "name":"Dana's iPhone", "nc":"<b64 16B>", "mac":"<b64 HMAC(k, "hello"|nc|name)>" }`.
   A bad MAC closes the socket with 4401; a spent, expired or closed window
   closes it with 4408. Both count as a failed attempt.
2. S->C `{ "t":"pair.pending", "ns":"<b64 16B>" }`. Both sides compute the
   confirmation code: `D = HKDF(k, nc|ns, "code")`, `code = (u32 big-endian of
   D[0..4]) mod 10000`, written as exactly 4 decimal digits with leading zeros.
   It is shown on the phone and on the PC dialog. The user must see the same number.
3. PC user clicks Allow. S->C `{ "t":"pair.ok", "id":"<b64 8B device id>", "wrap":"<b64 ciphertext|tag>" }`
   where `wrap = AEAD(key = HKDF(k, nc|ns, "wrap"), nonce = 12 zero bytes, plaintext = S)`
   and `S` is a fresh 256-bit device secret (so `wrap` decodes to 48 bytes). The
   phone stores `id` and `S` in browser storage. Deny: `{ "t":"pair.denied" }`,
   then the server closes the socket.
4. `k` is now spent. Five failed `pair.hello` attempts regenerate `k`.
5. After `pair.ok` the client continues on the same socket with step 1 of Connect
   below (fresh `nc`), so pairing ends in a normal session. The server accepts
   `hello` on a socket that has just paired.

## Connect (every session)

1. C->S `{ "t":"hello", "id":"<b64>", "nc":"<b64 16B>" }`
2. S->C `{ "t":"challenge", "ns":"<b64 16B>" }`
3. C->S `{ "t":"auth", "mac":"<b64 HMAC(S, "c"|nc|ns)>" }`
4. S->C `{ "t":"ready", "mac":"<b64 HMAC(S, "s"|nc|ns)>" }`. The client verifies it.
   Unknown, revoked or expired `id`, or a bad `auth` MAC: server closes the
   socket with code 4401.
5. Both derive two keys: `Kc2s = HKDF(S, nc|ns, "c2s")`, `Ks2c = HKDF(S, nc|ns, "s2c")`.

One controlling device at a time. If another device is already controlling, the
server sends the text frame `{ "t":"busy" }` right after `ready` and ignores
binary frames from this socket. The client may reply with the text frame
`{ "t":"takeover" }`; if the PC user approves, the old socket is closed with
4409 and the new one receives the text frame `{ "t":"granted" }`; if denied,
the new socket is closed with 4409. A socket that gets no `busy` frame is the
controller. After `ready`, text frames are only these control frames; all
other traffic is binary.

## Frames after `ready`

Every WebSocket message is binary: `nonce_counter (u64 BE, 8 bytes) | ciphertext | tag`.
The AEAD nonce is `dir (1 byte: 0 c2s, 1 s2c) | 3 zero bytes | counter (8 bytes)`,
using the same counter as the cleartext prefix. The key is `Kc2s` for c2s frames
and `Ks2c` for s2c frames.
The receiver requires each counter to be strictly greater than the last one seen
and drops anything else (replay and reorder protection). Counters start at 1.
A fresh `nc`/`ns` per connection means counters restart safely.

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
exact rate limits.

Conventions: `wheel` positive `dy` scrolls up (content moves down), positive
`dx` scrolls right. Clients split `text` longer than 1024 bytes on code-point
boundaries into several frames. `key` takes Windows virtual-key codes (for
example 0x1B Esc, 0x09 Tab, 0x0D Enter, 0x2E Delete, 0x25-0x28 arrows, 0xA2
LCtrl, 0xA4 LAlt, 0xA0 LShift, 0x5B LWin). Clients send `ping` about every 15 s
while idle and treat 25 s of silence from the server as a lost connection (the
server answers a `ping` with a `ping`).

### Control JSON (0x10, 0x11) - provisional

These schemas are provisional, written by the web client side; the Core
implementer may revise them here. Objects have a `t` field (except `state`).
Receivers ignore unknown `t` and unknown fields.

C->S (op 0x10):

- `{ "t":"volume", "set": 0..100 }` set master volume (the `media` op only has steps).
- `{ "t":"seek", "by": <seconds, signed> }` seek the active media session.
- `{ "t":"remotes.list" }` ask for the remote list.
- `{ "t":"remote.get", "id":"stremio" }` ask for one remote's layout.
- `{ "t":"action", "remote":"stremio", "action": <action object or array> }` run a
  remote button's action (action objects from `architecture.md`).
- `{ "t":"slider", "remote":"stremio", "bind":"volume.app", "value": 0..100 }`

S->C (op 0x10):

- `{ "t":"remotes", "list":[ { "id":"stremio", "name":"Stremio" } ] }`
- `{ "t":"remote", "remote": <remote JSON, schema 1> }`

S->C (op 0x11), sent after `ready` and on change:

```json
{ "pc":"Office PC", "volume":62, "muted":false,
  "media": { "app":"Plex", "title":"Dune: Part Two", "artist":"", "playing":true,
             "pos":4328, "dur":9960 },
  "bind": { "smtc.title":"Dune: Part Two", "volume.app":48 },
  "locked":false, "elevated":false }
```

`media` is `null` when there is no session. `pos` and `dur` are seconds; `pos`
is a snapshot that the client extrapolates while `playing`. `bind` holds values
for remote labels and sliders. `locked: true` means input is paused.

## Errors

The server closes with: 4400 malformed, 4401 unauthorised or revoked, 4408
pairing window closed or expired, 4429 rate limited, 4409 replaced by another
controller.

## Test vectors

`tests/vectors.json` holds fixed vectors for every HKDF/HMAC/AEAD step: pairing
code, `wrap`, both auth MACs, both session keys, and one frame in each
direction. The Core tests and the web client's unit tests (`web/src/*.test.ts`)
pass the same file.
